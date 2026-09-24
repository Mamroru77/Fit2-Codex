using System.Diagnostics;
using CodexQuota.Codex.Process;
using CodexQuota.Codex.Protocol;
using CodexQuota.Codex.Tests.Fakes;
using Xunit;

namespace CodexQuota.Codex.Tests;

public class CodexProcessManagerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task StartAsyncLaunchesTheProcessAndReportsRunning()
    {
        var launcher = new FakeCodexProcessLauncher();
        var time = new FakeCodexTimeSource();
        await using var manager = new CodexProcessManager(launcher.LaunchAsync, new RestartBackoff(), time);
        using var timeout = new CancellationTokenSource(TestTimeout);

        var session = await manager.StartAsync(timeout.Token);

        Assert.Equal(CodexProcessStatus.Running, manager.Status);
        Assert.Equal(1, launcher.LaunchCount);
        Assert.Same(session, manager.CurrentSession);
        Assert.NotNull(session.RpcClient);
    }

    [Fact]
    public async Task UnexpectedExitRelaunchesAfterTheDocumentedDelayAndExposesTheNewSession()
    {
        var launcher = new FakeCodexProcessLauncher();
        var time = new FakeCodexTimeSource();
        await using var manager = new CodexProcessManager(launcher.LaunchAsync, new RestartBackoff(), time);
        using var timeout = new CancellationTokenSource(TestTimeout);

        var firstSession = await manager.StartAsync(timeout.Token);

        var restarting = WaitForStatusAsync(manager, CodexProcessStatus.Restarting);
        launcher.Processes[0].Crash();
        await restarting;

        await time.WaitForDelayRequestsAsync(1, timeout.Token);
        Assert.Equal(new[] { TimeSpan.FromSeconds(1) }, time.Delays);

        var running = WaitForStatusAsync(manager, CodexProcessStatus.Running);
        time.CompleteDelay();
        await running;

        Assert.Equal(2, launcher.LaunchCount);
        Assert.NotSame(firstSession, manager.CurrentSession);
        Assert.True(launcher.Processes[0].IsDisposed);
    }

    [Fact]
    public async Task SixthCrashInsideTheRollingWindowFaultsWithoutAnotherLaunch()
    {
        var launcher = new FakeCodexProcessLauncher();
        var time = new FakeCodexTimeSource();
        await using var manager = new CodexProcessManager(launcher.LaunchAsync, new RestartBackoff(), time);
        using var timeout = new CancellationTokenSource(TestTimeout);

        await manager.StartAsync(timeout.Token);

        for (var crash = 1; crash <= 5; crash++)
        {
            launcher.Processes[crash - 1].Crash();
            await time.WaitForDelayRequestsAsync(crash, timeout.Token);
            time.CompleteDelay();
            await launcher.WaitForLaunchesAsync(crash + 1, timeout.Token);
        }

        Assert.Equal(5, time.Delays.Count);

        var faulted = WaitForStatusAsync(manager, CodexProcessStatus.Faulted);
        launcher.Processes[5].Crash();
        await faulted;

        // Faulted means the supervisor loop has ended: no sixth restart is ever scheduled.
        Assert.Equal(CodexProcessStatus.Faulted, manager.Status);
        Assert.Equal(6, launcher.LaunchCount);
        Assert.Equal(5, time.Delays.Count);

        // The documented delays are unchanged, and no session survives the fault: CurrentSession
        // is "the currently running session or null", and nothing is running any more.
        Assert.Equal(
            new[]
            {
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30),
            },
            time.Delays);
        Assert.Null(manager.CurrentSession);
        Assert.True(launcher.Processes[5].IsDisposed);
    }

    [Fact]
    public async Task TheExitedSessionIsNeverExposedDuringTheRestartBackoff()
    {
        var launcher = new FakeCodexProcessLauncher();
        var time = new FakeCodexTimeSource();
        await using var manager = new CodexProcessManager(launcher.LaunchAsync, new RestartBackoff(), time);
        using var timeout = new CancellationTokenSource(TestTimeout);

        await manager.StartAsync(timeout.Token);

        var restarting = WaitForStatusAsync(manager, CodexProcessStatus.Restarting);
        launcher.Processes[0].Crash();
        await restarting;

        // Restarting == no process is running == CurrentSession must be null, and the crashed
        // session must already be cleaned up rather than waiting for the replacement launch.
        Assert.Null(manager.CurrentSession);
        Assert.True(launcher.Processes[0].IsDisposed);

        await time.WaitForDelayRequestsAsync(1, timeout.Token);
        Assert.Null(manager.CurrentSession);

        var running = WaitForStatusAsync(manager, CodexProcessStatus.Running);
        time.CompleteDelay();
        await running;

        Assert.NotNull(manager.CurrentSession);
    }

    [Fact]
    public async Task ReplacementLaunchFailureFaultsWithoutLeakingTheCrashedSession()
    {
        var launcher = new FakeCodexProcessLauncher();
        launcher.FailFromLaunch(2);
        var time = new FakeCodexTimeSource();
        await using var manager = new CodexProcessManager(launcher.LaunchAsync, new RestartBackoff(), time);
        using var timeout = new CancellationTokenSource(TestTimeout);

        await manager.StartAsync(timeout.Token);

        var restarting = WaitForStatusAsync(manager, CodexProcessStatus.Restarting);
        launcher.Processes[0].Crash();
        await restarting;

        // The crashed session is cleaned up before the replacement is even attempted, so a
        // failing launch cannot strand it.
        Assert.Null(manager.CurrentSession);
        Assert.True(launcher.Processes[0].IsDisposed);

        await time.WaitForDelayRequestsAsync(1, timeout.Token);

        var faulted = WaitForStatusAsync(manager, CodexProcessStatus.Faulted);
        time.CompleteDelay();
        await faulted;

        Assert.Equal(CodexProcessStatus.Faulted, manager.Status);
        Assert.Null(manager.CurrentSession);
        Assert.Equal(1, launcher.LaunchCount);
        Assert.Equal(2, launcher.Attempts);
    }

    [Fact]
    public async Task FirstLaunchFailureSurfacesToStartAsyncWithoutHanging()
    {
        var launcher = new FakeCodexProcessLauncher();
        launcher.FailFromLaunch(1);
        var time = new FakeCodexTimeSource();
        await using var manager = new CodexProcessManager(launcher.LaunchAsync, new RestartBackoff(), time);
        using var timeout = new CancellationTokenSource(TestTimeout);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.StartAsync(timeout.Token));

        Assert.Contains("failed", exception.Message);
        Assert.Equal(CodexProcessStatus.Faulted, manager.Status);
        Assert.Null(manager.CurrentSession);
    }

    [Fact]
    public async Task ConcurrentStartAsyncStartsOnlyOneSupervisor()
    {
        var launcher = new FakeCodexProcessLauncher();
        var time = new FakeCodexTimeSource();
        await using var manager = new CodexProcessManager(launcher.LaunchAsync, new RestartBackoff(), time);
        using var timeout = new CancellationTokenSource(TestTimeout);

        var outcomes = await Task.WhenAll(
            Task.Run(() => TryStartAsync(manager, timeout.Token)),
            Task.Run(() => TryStartAsync(manager, timeout.Token)));

        // Exactly one caller may own the supervisor; the other fails predictably.
        Assert.Single(outcomes, session => session is not null);
        Assert.Equal(1, launcher.LaunchCount);
        Assert.Equal(1, launcher.Attempts);
    }

    [Fact]
    public async Task StartAsyncAfterStopIsRejectedWithoutLaunchingAnotherProcess()
    {
        var launcher = new FakeCodexProcessLauncher();
        var time = new FakeCodexTimeSource();
        await using var manager = new CodexProcessManager(launcher.LaunchAsync, new RestartBackoff(), time);
        using var timeout = new CancellationTokenSource(TestTimeout);

        await manager.StartAsync(timeout.Token);
        await manager.StopAsync(timeout.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync(timeout.Token));

        Assert.Equal(CodexProcessStatus.Stopped, manager.Status);
        Assert.Null(manager.CurrentSession);
        Assert.Equal(1, launcher.LaunchCount);
        Assert.Equal(1, launcher.Attempts);
    }

    [Fact]
    public async Task StartAsyncAfterDisposeIsRejected()
    {
        var launcher = new FakeCodexProcessLauncher();
        var time = new FakeCodexTimeSource();
        var manager = new CodexProcessManager(launcher.LaunchAsync, new RestartBackoff(), time);
        using var timeout = new CancellationTokenSource(TestTimeout);

        await manager.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.StartAsync(timeout.Token));

        Assert.Equal(0, launcher.Attempts);
    }

    [Fact]
    public async Task StopAsyncSuppressesThePendingRestart()
    {
        var launcher = new FakeCodexProcessLauncher();
        var time = new FakeCodexTimeSource();
        await using var manager = new CodexProcessManager(launcher.LaunchAsync, new RestartBackoff(), time);
        using var timeout = new CancellationTokenSource(TestTimeout);

        await manager.StartAsync(timeout.Token);

        var restarting = WaitForStatusAsync(manager, CodexProcessStatus.Restarting);
        launcher.Processes[0].Crash();
        await restarting;

        await time.WaitForDelayRequestsAsync(1, timeout.Token);

        await manager.StopAsync(timeout.Token);

        Assert.Equal(CodexProcessStatus.Stopped, manager.Status);
        Assert.Equal(1, launcher.LaunchCount);
        Assert.True(launcher.Processes[0].IsDisposed);
    }

    [Fact]
    public async Task ExceptionalSupervisorExitReleasesTheRunningSession()
    {
        var launcher = new FakeCodexProcessLauncher();
        var time = new FakeCodexTimeSource();
        await using var manager = new CodexProcessManager(launcher.LaunchAsync, new RestartBackoff(), time);
        using var timeout = new CancellationTokenSource(TestTimeout);

        await manager.StartAsync(timeout.Token);
        Assert.NotNull(manager.CurrentSession);

        // The supervisor can leave the loop exceptionally — not only through a child exit or a
        // failed launch. That path used to skip the session cleanup the ordinary paths perform,
        // so CurrentSession stayed non-null while nothing was being supervised, and the session
        // was never disposed.
        var faulted = WaitForStatusAsync(manager, CodexProcessStatus.Faulted);
        launcher.Processes[0].Fault(new InvalidOperationException("supervisor failure"));
        await faulted;

        Assert.Equal(CodexProcessStatus.Faulted, manager.Status);
        Assert.Null(manager.CurrentSession);
        Assert.True(launcher.Processes[0].IsDisposed);
    }

    private static async Task<CodexManagedSession?> TryStartAsync(
        CodexProcessManager manager,
        CancellationToken cancellationToken)
    {
        try
        {
            return await manager.StartAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static Task WaitForStatusAsync(CodexProcessManager manager, CodexProcessStatus status)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        manager.StatusChanged += changed =>
        {
            if (changed == status)
            {
                completion.TrySetResult();
            }
        };

        return completion.Task.WaitAsync(TestTimeout);
    }
}

public class CodexAppServerProcessTests
{
    [Fact]
    public void StartInfoMatchesTheDocumentedLaunchContract()
    {
        var startInfo = CodexAppServerProcess.CreateStartInfo(
            @"C:\app\runtime\codex-app-server.exe",
            @"C:\Users\me\AppData\Local\CodexQuotaBridge\codex-home");

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal(@"C:\app\runtime", startInfo.WorkingDirectory);
        Assert.Equal(new[] { "--listen", "stdio://" }, startInfo.ArgumentList);
        Assert.Equal(
            @"C:\Users\me\AppData\Local\CodexQuotaBridge\codex-home",
            startInfo.Environment["CODEX_HOME"]);
    }

    [Fact]
    public void ResolvesTheSupportedBinaryBesideTheDesktopExecutable()
    {
        Assert.Equal(
            Path.Combine(@"C:\app", "runtime", "codex-app-server.exe"),
            CodexRuntimeLocator.ResolveAppServerPath(@"C:\app"));
    }

    [Fact]
    public void ResolvesAnIsolatedCodexHome()
    {
        Assert.Equal(
            Path.Combine(@"C:\Users\me\AppData\Local", "CodexQuotaBridge", "codex-home"),
            CodexRuntimeLocator.ResolveCodexHome(@"C:\Users\me\AppData\Local"));
    }
}

/// <summary>A child process whose exit the test controls.</summary>
internal sealed class FakeCodexProcess : ICodexProcess
{
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IJsonRpcTransport Transport { get; } = new FakeJsonRpcTransport();

    public bool IsDisposed { get; private set; }

    public void Crash(int exitCode = 1) => _exited.TrySetResult(exitCode);

    /// <summary>
    /// Makes waiting for the child throw, the way an exceptional supervisor failure (a broken
    /// process handle, an out-of-memory kill, a driver fault) reaches the supervisor.
    /// </summary>
    public void Fault(Exception exception) => _exited.TrySetException(exception);

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        => _exited.Task.WaitAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Launcher that hands out <see cref="FakeCodexProcess"/> instances and records them.</summary>
internal sealed class FakeCodexProcessLauncher
{
    private readonly List<FakeCodexProcess> _processes = [];
    private readonly object _gate = new();

    private TaskCompletionSource _launchSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _attempts;
    private int _failFromLaunch;

    internal IReadOnlyList<FakeCodexProcess> Processes
    {
        get
        {
            lock (_gate)
            {
                return _processes.ToArray();
            }
        }
    }

    internal int LaunchCount
    {
        get
        {
            lock (_gate)
            {
                return _processes.Count;
            }
        }
    }

    /// <summary>Launch attempts, including the ones that failed.</summary>
    internal int Attempts => Volatile.Read(ref _attempts);

    /// <summary>Makes every launch attempt from <paramref name="launchNumber"/> onwards throw.</summary>
    internal void FailFromLaunch(int launchNumber) => Volatile.Write(ref _failFromLaunch, launchNumber);

    internal Task<ICodexProcess> LaunchAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var attempt = Interlocked.Increment(ref _attempts);
        var failFrom = Volatile.Read(ref _failFromLaunch);

        if (failFrom > 0 && attempt >= failFrom)
        {
            throw new InvalidOperationException($"Launch attempt {attempt} failed.");
        }

        var process = new FakeCodexProcess();
        TaskCompletionSource signal;

        lock (_gate)
        {
            _processes.Add(process);
            signal = _launchSignal;
            _launchSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        signal.TrySetResult();
        return Task.FromResult<ICodexProcess>(process);
    }

    internal async Task WaitForLaunchesAsync(int count, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task signal;

            lock (_gate)
            {
                if (_processes.Count >= count)
                {
                    return;
                }

                signal = _launchSignal.Task;
            }

            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Clock and delay seam: delays never really elapse, they are released by the test, which also
/// advances the clock so the rolling crash window behaves as it would in production.
/// </summary>
internal sealed class FakeCodexTimeSource : ICodexTimeSource
{
    private readonly List<TimeSpan> _delays = [];
    private readonly object _gate = new();

    private TaskCompletionSource _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _requestSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DateTimeOffset UtcNow { get; private set; } = new(2026, 9, 22, 13, 30, 0, TimeSpan.Zero);

    internal IReadOnlyList<TimeSpan> Delays
    {
        get
        {
            lock (_gate)
            {
                return _delays.ToArray();
            }
        }
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Task pending;
        TaskCompletionSource requestSignal;

        lock (_gate)
        {
            _delays.Add(delay);
            _pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = _pending.Task;
            requestSignal = _requestSignal;
            _requestSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        requestSignal.TrySetResult();
        return pending.WaitAsync(cancellationToken);
    }

    internal async Task WaitForDelayRequestsAsync(int count, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task signal;

            lock (_gate)
            {
                if (_delays.Count >= count)
                {
                    return;
                }

                signal = _requestSignal.Task;
            }

            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Releases the pending delay and advances the clock as if it had elapsed.</summary>
    internal void CompleteDelay()
    {
        TaskCompletionSource pending;
        TimeSpan delay;

        lock (_gate)
        {
            pending = _pending;
            delay = _delays[^1];
            _pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        UtcNow += delay;
        pending.TrySetResult();
    }
}

public class CodexStandardErrorDrainTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task DrainConsumesEveryLineWhileTheChildIsStillRunning()
    {
        var reader = new ControllableStderrReader();
        var consumed = new List<string>();
        using var stop = new CancellationTokenSource();
        using var timeout = new CancellationTokenSource(TestTimeout);

        var drain = CodexStandardErrorDrain.DrainAsync(
            reader,
            line =>
            {
                lock (consumed)
                {
                    consumed.Add(line);
                }
            },
            stop.Token);

        reader.Enqueue("warn: one");
        reader.Enqueue("warn: two");
        await reader.WaitForReadsAsync(2, timeout.Token);

        // Consumed while the "child" is still alive: nothing waits for exit, so the pipe can never
        // fill and block the child.
        Assert.Equal(new[] { "warn: one", "warn: two" }, consumed);
        Assert.False(drain.IsCompleted);

        reader.Complete();
        await drain.WaitAsync(timeout.Token);

        Assert.True(drain.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task AThrowingSinkNeverEndsTheDrain()
    {
        var reader = new ControllableStderrReader();
        var consumed = new List<string>();
        using var stop = new CancellationTokenSource();
        using var timeout = new CancellationTokenSource(TestTimeout);

        var drain = CodexStandardErrorDrain.DrainAsync(
            reader,
            line =>
            {
                lock (consumed)
                {
                    consumed.Add(line);
                }

                if (line == "warn: one")
                {
                    throw new InvalidOperationException("log sink failure");
                }
            },
            stop.Token);

        reader.Enqueue("warn: one");
        reader.Enqueue("warn: two");
        await reader.WaitForReadsAsync(2, timeout.Token);

        // A logging sink is third-party code from the drain's point of view. If its exception
        // ended the drain, the child's stderr pipe would fill, the child would block inside its
        // own write and never exit — the silent permanent stall the drain exists to prevent.
        Assert.False(drain.IsCompleted);
        Assert.Equal(new[] { "warn: one", "warn: two" }, consumed);

        reader.Complete();
        await drain.WaitAsync(timeout.Token);

        Assert.True(drain.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DrainEndsCleanlyWhenDisposalCancelsIt()
    {
        var reader = new ControllableStderrReader();
        using var stop = new CancellationTokenSource();
        using var timeout = new CancellationTokenSource(TestTimeout);

        var drain = CodexStandardErrorDrain.DrainAsync(reader, _ => { }, stop.Token);

        reader.Enqueue("warn: one");
        await reader.WaitForReadsAsync(1, timeout.Token);

        await stop.CancelAsync();
        await drain.WaitAsync(timeout.Token);

        // Cancelling the drain is an ordinary shutdown step, not a failure.
        Assert.True(drain.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DrainEndsCleanlyWhenStderrIsDisposedUnderneathIt()
    {
        var reader = new ControllableStderrReader();
        using var stop = new CancellationTokenSource();
        using var timeout = new CancellationTokenSource(TestTimeout);

        var drain = CodexStandardErrorDrain.DrainAsync(reader, _ => { }, stop.Token);

        reader.Enqueue("warn: one");
        await reader.WaitForReadsAsync(1, timeout.Token);

        // Disposing stderr to end a blocked read is the same trick the approved transport uses for
        // stdout; it must end the drain, not fault it.
        reader.Fail(new IOException("The stderr pipe was closed during shutdown."));
        await drain.WaitAsync(timeout.Token);

        Assert.True(drain.IsCompletedSuccessfully);
    }
}

/// <summary>
/// A stderr reader the test controls: it hands out lines on demand and only reaches end of stream
/// when the test says so, so the drain can be observed without any real process or any sleeping.
/// </summary>
internal sealed class ControllableStderrReader : TextReader
{
    private readonly object _gate = new();
    private readonly Queue<string> _lines = new();

    private TaskCompletionSource _inputSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _readSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? _fault;
    private bool _completed;
    private int _reads;

    internal int Reads => Volatile.Read(ref _reads);

    internal void Enqueue(string line)
    {
        lock (_gate)
        {
            _lines.Enqueue(line);
            SignalInputLocked();
        }
    }

    internal void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            SignalInputLocked();
        }
    }

    internal void Fail(Exception exception)
    {
        lock (_gate)
        {
            _fault = exception;
            SignalInputLocked();
        }
    }

    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task input;

            lock (_gate)
            {
                if (_fault is { } fault)
                {
                    throw fault;
                }

                if (_lines.Count > 0)
                {
                    var line = _lines.Dequeue();
                    Interlocked.Increment(ref _reads);
                    SignalReadLocked();
                    return line;
                }

                if (_completed)
                {
                    Interlocked.Increment(ref _reads);
                    SignalReadLocked();
                    return null;
                }

                input = _inputSignal.Task;
            }

            await input.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task WaitForReadsAsync(int count, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task signal;

            lock (_gate)
            {
                if (Volatile.Read(ref _reads) >= count)
                {
                    return;
                }

                signal = _readSignal.Task;
            }

            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void SignalInputLocked()
    {
        var signal = _inputSignal;
        _inputSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.TrySetResult();
    }

    private void SignalReadLocked()
    {
        var signal = _readSignal;
        _readSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.TrySetResult();
    }
}
