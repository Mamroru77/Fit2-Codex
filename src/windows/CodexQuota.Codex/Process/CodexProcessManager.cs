using CodexQuota.Codex.Protocol;

namespace CodexQuota.Codex.Process;

/// <summary>Lifecycle status of the managed Codex App Server child process.</summary>
public enum CodexProcessStatus
{
    Starting,
    Running,
    Restarting,
    Faulted,
    Stopped,
}

/// <summary>
/// Clock and delay seam for the restart supervisor. Tests substitute a fake so restart behaviour
/// is verified without any real time passing.
/// </summary>
public interface ICodexTimeSource
{
    /// <summary>Current UTC time, used for the rolling crash window.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>Waits for <paramref name="delay"/> to elapse.</summary>
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>A started App Server process and the RPC client bound to it.</summary>
public sealed class CodexManagedSession : IAsyncDisposable
{
    private readonly ICodexProcess _process;

    internal CodexManagedSession(ICodexProcess process)
    {
        _process = process;
        RpcClient = new CodexRpcClient(process.Transport);
    }

    /// <summary>The RPC client for this process. It is only usable while this session is current.</summary>
    public CodexRpcClient RpcClient { get; }

    public async ValueTask DisposeAsync()
    {
        await RpcClient.DisposeAsync().ConfigureAwait(false);
        await _process.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Owns the Codex App Server child process: it launches one process, restarts it after an
/// unexpected exit using <see cref="RestartBackoff"/>, faults once the restart budget is
/// exhausted, and never restarts after an explicit <see cref="StopAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// A manager instance is one-shot: it owns at most one supervisor and one lifetime. Automatic
/// restart is the supervisor's job; a manual retry after <see cref="CodexProcessStatus.Faulted"/>
/// creates a fresh manager together with a fresh <see cref="RestartBackoff"/>, so the crash budget
/// starts clean.
/// </para>
/// <para>
/// <see cref="CurrentSession"/> invariant: it is non-null exactly while a process is running
/// (<see cref="CodexProcessStatus.Running"/>), and null in
/// <see cref="CodexProcessStatus.Restarting"/>, <see cref="CodexProcessStatus.Faulted"/> and
/// <see cref="CodexProcessStatus.Stopped"/>. Whoever removes a session from it owns disposing that
/// session, so every session is disposed exactly once.
/// </para>
/// </remarks>
public sealed class CodexProcessManager : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<ICodexProcess>> _launchProcess;
    private readonly RestartBackoff _backoff;
    private readonly ICodexTimeSource _timeSource;
    private readonly CancellationTokenSource _shutdown = new();

    private Task? _supervisor;
    private TaskCompletionSource<CodexManagedSession>? _firstSession;
    private CodexManagedSession? _currentSession;
    private volatile bool _stopRequested;
    private volatile bool _disposed;
    private int _started;
    private volatile CodexProcessStatus _status = CodexProcessStatus.Stopped;

    /// <summary>
    /// Production constructor: launches the supported binary shipped beside the desktop executable.
    /// </summary>
    public CodexProcessManager(string appServerPath, string codexHomePath, RestartBackoff backoff)
        : this(
            _ => Task.FromResult<ICodexProcess>(CodexAppServerProcess.Start(appServerPath, codexHomePath)),
            backoff,
            new SystemCodexTimeSource())
    {
    }

    /// <summary>
    /// Testable constructor: the launcher and clock are supplied by the caller.
    /// </summary>
    public CodexProcessManager(
        Func<CancellationToken, Task<ICodexProcess>> launchProcess,
        RestartBackoff backoff,
        ICodexTimeSource timeSource)
    {
        ArgumentNullException.ThrowIfNull(launchProcess);
        ArgumentNullException.ThrowIfNull(backoff);
        ArgumentNullException.ThrowIfNull(timeSource);

        _launchProcess = launchProcess;
        _backoff = backoff;
        _timeSource = timeSource;
    }

    /// <summary>Current lifecycle status.</summary>
    public CodexProcessStatus Status => _status;

    /// <summary>
    /// The session for the process that is currently running, or <c>null</c> when none is.
    /// It is replaced whenever the supervisor restarts the process.
    /// </summary>
    public CodexManagedSession? CurrentSession => Volatile.Read(ref _currentSession);

    /// <summary>Raised on every status transition.</summary>
    public event Action<CodexProcessStatus>? StatusChanged;

    /// <summary>
    /// Launches the first process, starts the restart supervisor, and completes once that first
    /// process is running. A manager can only be started once.
    /// </summary>
    public async Task<CodexManagedSession> StartAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        // One-shot gate: concurrent callers, a second call after Stop, and a second call after a
        // fault are all rejected, so two supervisors (and therefore two child processes) can never
        // exist for one manager.
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            throw new InvalidOperationException(
                "This Codex App Server manager has already been started; create a new manager to start again.");
        }

        // Read the token on the caller's thread: if the manager is disposed concurrently the
        // failure surfaces here instead of inside the supervisor, where it could leave the caller
        // waiting for a session that will never be produced.
        var supervisorToken = _shutdown.Token;

        var firstSession = new TaskCompletionSource<CodexManagedSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        _firstSession = firstSession;
        _supervisor = Task.Run(() => RunSupervisorAsync(supervisorToken));

        return await firstSession.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops the managed process and suppresses any pending restart.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        _stopRequested = true;

        try
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);

            var supervisor = Interlocked.Exchange(ref _supervisor, null);
            if (supervisor is not null)
            {
                await supervisor.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // Ownership cleanup must not depend on the caller's token: cancellation may end the
            // caller's wait, but it must never leave a live child, a non-null CurrentSession or a
            // status outside Stopped behind. Whoever takes the session out of _currentSession owns
            // disposing it, so it is disposed exactly once.
            var session = Interlocked.Exchange(ref _currentSession, null);
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

            SetStatus(CodexProcessStatus.Stopped);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);

        _disposed = true;
        _shutdown.Dispose();
    }

    private async Task RunSupervisorAsync(CancellationToken cancellationToken)
    {
        try
        {
            SetStatus(CodexProcessStatus.Starting);

            while (true)
            {
                var process = await _launchProcess(cancellationToken).ConfigureAwait(false);
                var session = new CodexManagedSession(process);

                var previous = Interlocked.Exchange(ref _currentSession, session);
                if (previous is not null)
                {
                    await previous.DisposeAsync().ConfigureAwait(false);
                }

                _firstSession?.TrySetResult(session);
                SetStatus(CodexProcessStatus.Running);

                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

                // The process has exited: reclaim it now, before deciding what happens next. From
                // here until a replacement is running there is no running process, so CurrentSession
                // must be null — a caller can never observe a dead session, not even during the
                // backoff delay.
                var exited = Interlocked.Exchange(ref _currentSession, null);
                if (exited is not null)
                {
                    await exited.DisposeAsync().ConfigureAwait(false);
                }

                if (_stopRequested)
                {
                    break;
                }

                var delay = _backoff.NextDelay(_timeSource.UtcNow);

                if (delay is null)
                {
                    // Restart budget exhausted: stop looping and let the operator retry manually.
                    SetStatus(CodexProcessStatus.Faulted);
                    return;
                }

                SetStatus(CodexProcessStatus.Restarting);
                await _timeSource.DelayAsync(delay.Value, cancellationToken).ConfigureAwait(false);
            }

            SetStatus(CodexProcessStatus.Stopped);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetStatus(CodexProcessStatus.Stopped);

            // A StopAsync that races the very first launch still has to release a waiting
            // StartAsync caller, which would otherwise wait forever for a session that will never
            // be produced.
            _firstSession?.TrySetCanceled(cancellationToken);
        }
        catch (Exception exception)
        {
            // A failure to launch or supervise must surface to the caller of StartAsync rather
            // than leaving it waiting forever. The session for the crashed process has already been
            // cleaned up, so CurrentSession is null here.
            SetStatus(CodexProcessStatus.Faulted);
            _firstSession?.TrySetException(exception);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(CodexProcessManager));
        }
    }

    private void SetStatus(CodexProcessStatus status)
    {
        if (_status == status)
        {
            return;
        }

        _status = status;
        StatusChanged?.Invoke(status);
    }
}

/// <summary>Production clock: real UTC time and real delays.</summary>
internal sealed class SystemCodexTimeSource : ICodexTimeSource
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        => Task.Delay(delay, cancellationToken);
}
