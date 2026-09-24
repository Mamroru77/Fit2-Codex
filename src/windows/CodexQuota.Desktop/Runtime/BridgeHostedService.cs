using CodexQuota.Codex.Account;
using CodexQuota.Codex.Process;
using CodexQuota.Codex.Quota;
using CodexQuota.Core.Quota;
using CodexQuota.Core.Runtime;
using CodexQuota.Storage.History;
using Microsoft.Extensions.Hosting;

namespace CodexQuota.Desktop.Runtime;

/// <summary>
/// Composes the Bridge: it owns the Codex App Server child process, wires each live session to
/// the account and quota services, and forwards every published quota update to history
/// persistence.
/// </summary>
/// <remarks>
/// A restart replaces the child process and therefore its RPC client, so the sync service is
/// rebuilt for every new session. Nothing in this type touches WPF; the tray and the status
/// window are separate concerns that observe the same runtime state.
/// </remarks>
public sealed class BridgeHostedService : IHostedService, IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<ICodexProcess>> _launchProcess;
    private readonly IQuotaStateStore _store;
    private readonly BridgeRuntimeState _runtime;
    private readonly HistoryPersistenceWorker _history;
    private readonly RestartBackoff _backoff;
    private readonly ICodexTimeSource _timeSource;
    private readonly SemaphoreSlim _wireGate = new(1, 1);

    private CodexProcessManager? _manager;
    private CodexManagedSession? _wiredSession;
    private CodexQuotaSyncService? _sync;
    private CodexAccountService? _account;
    private CancellationTokenSource? _stop;
    private Task? _historyRun;
    private bool _stopped;

    /// <summary>Production composition: launches the supported runtime shipped beside the host.</summary>
    /// <param name="runtime">The supported runtime to launch.</param>
    /// <param name="codexHomePath">Isolated <c>CODEX_HOME</c> for the child.</param>
    /// <param name="store">The quota state store every published snapshot goes through.</param>
    /// <param name="runtimeState">Observable Bridge runtime state.</param>
    /// <param name="history">Persistence for published snapshots.</param>
    /// <param name="backoff">Restart policy; the documented default when omitted.</param>
    /// <param name="onStderrLine">
    /// Sink for the child's stderr, so its own diagnostics reach the redacted log. It is isolated
    /// from the drain: a throwing sink can never end it.
    /// </param>
    /// <param name="onDiagnostic">
    /// Sink for lifecycle conditions the Bridge cannot fix by itself, such as a child that survived
    /// termination.
    /// </param>
    public BridgeHostedService(
        CodexRuntime runtime,
        string codexHomePath,
        IQuotaStateStore store,
        BridgeRuntimeState runtimeState,
        HistoryPersistenceWorker history,
        RestartBackoff? backoff = null,
        Action<string>? onStderrLine = null,
        Action<string>? onDiagnostic = null)
        : this(
            _ => Task.FromResult<ICodexProcess>(
                CodexAppServerProcess.Start(runtime, codexHomePath, onStderrLine, onDiagnostic)),
            store,
            runtimeState,
            history,
            backoff,
            null)
    {
    }

    /// <summary>Testable composition: the child process launcher and clock are supplied.</summary>
    public BridgeHostedService(
        Func<CancellationToken, Task<ICodexProcess>> launchProcess,
        IQuotaStateStore store,
        BridgeRuntimeState runtime,
        HistoryPersistenceWorker history,
        RestartBackoff? backoff = null,
        ICodexTimeSource? timeSource = null)
    {
        ArgumentNullException.ThrowIfNull(launchProcess);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(history);

        _launchProcess = launchProcess;
        _runtime = runtime;
        _history = history;
        _backoff = backoff ?? new RestartBackoff();
        _timeSource = timeSource ?? new SystemTimeSource();

        // Every published snapshot is offered to persistence, and a snapshot is only ever
        // published by replacing it in the store.
        _store = new PublishingQuotaStateStore(store, history);
    }

    /// <summary>Lifecycle status of the managed App Server child process.</summary>
    public CodexProcessStatus Status => _manager?.Status ?? CodexProcessStatus.Stopped;

    /// <summary>
    /// The account service bound to the session that is currently running, or <c>null</c> while
    /// no session is up. It is replaced together with the session after every restart.
    /// </summary>
    public CodexAccountService? Account => _account;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_manager is not null)
        {
            throw new InvalidOperationException("The Bridge has already been started.");
        }

        _stop = new CancellationTokenSource();
        _historyRun = _history.RunAsync(_stop.Token);

        _manager = new CodexProcessManager(_launchProcess, _backoff, _timeSource);
        _manager.StatusChanged += OnProcessStatusChanged;

        var session = await _manager.StartAsync(cancellationToken).ConfigureAwait(false);

        await WireSessionAsync(session, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Manual "Refresh Now": one immediate quota read and nothing else.</summary>
    public Task RefreshNowAsync(CancellationToken cancellationToken)
        => _sync is { } sync ? sync.RefreshNowAsync(cancellationToken) : Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;

        var manager = Interlocked.Exchange(ref _manager, null);
        if (manager is not null)
        {
            manager.StatusChanged -= OnProcessStatusChanged;
            await manager.StopAsync(cancellationToken).ConfigureAwait(false);
            await manager.DisposeAsync().ConfigureAwait(false);
        }

        await DisposeSyncAsync().ConfigureAwait(false);

        var stop = Interlocked.Exchange(ref _stop, null);
        if (stop is not null)
        {
            await stop.CancelAsync().ConfigureAwait(false);

            var historyRun = _historyRun;
            if (historyRun is not null)
            {
                await historyRun.ConfigureAwait(false);
            }

            stop.Dispose();
        }

        _runtime.SetPhase(BridgeRuntimePhase.Stopped);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _wireGate.Dispose();
    }

    private void OnProcessStatusChanged(CodexProcessStatus status)
    {
        switch (status)
        {
            case CodexProcessStatus.Running:
                // A restart produced a new session; its RPC client is the only usable one now.
                _ = Task.Run(() => WireSessionAsync(_manager?.CurrentSession, CancellationToken.None));
                break;

            case CodexProcessStatus.Faulted:
                _runtime.SetSourceStatus(QuotaSourceStatus.Unavailable);
                break;
        }
    }

    private async Task WireSessionAsync(CodexManagedSession? session, CancellationToken cancellationToken)
    {
        if (session is null)
        {
            return;
        }

        await _wireGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(session, _wiredSession))
            {
                return;
            }

            await DisposeSyncAsync().ConfigureAwait(false);
            _wiredSession = session;

            var account = new CodexAccountService(session.RpcClient);
            var sync = new CodexQuotaSyncService(
                session.RpcClient,
                account,
                new RateLimitAdapter(),
                _store,
                _runtime);

            _account = account;
            _sync = sync;

            try
            {
                await sync.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The child can die mid-handshake during a restart loop. That must degrade the
                // reported source status, never tear down the host.
                _runtime.SetSourceStatus(QuotaSourceStatus.Unavailable);
            }
        }
        finally
        {
            _wireGate.Release();
        }
    }

    private async Task DisposeSyncAsync()
    {
        var sync = Interlocked.Exchange(ref _sync, null);
        if (sync is not null)
        {
            // The account service shares the session's RPC client, not its lifetime.
            _account = null;
            await sync.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Publishes into the real store and offers the resulting update to persistence. Persistence
    /// is downstream only: it can never hold up or roll back a publication.
    /// </summary>
    private sealed class PublishingQuotaStateStore : IQuotaStateStore
    {
        private readonly IQuotaStateStore _inner;
        private readonly HistoryPersistenceWorker _history;

        internal PublishingQuotaStateStore(IQuotaStateStore inner, HistoryPersistenceWorker history)
        {
            _inner = inner;
            _history = history;
        }

        public QuotaSnapshot? Current => _inner.Current;

        public long Sequence => _inner.Sequence;

        public QuotaStateUpdate Replace(QuotaSnapshot snapshot)
        {
            var update = _inner.Replace(snapshot);

            // A full backlog just means fewer history points; the store stays authoritative.
            _history.TryEnqueue(update);

            return update;
        }
    }
}

/// <summary>Production clock for the restart supervisor.</summary>
internal sealed class SystemTimeSource : ICodexTimeSource
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        => Task.Delay(delay, cancellationToken);
}
