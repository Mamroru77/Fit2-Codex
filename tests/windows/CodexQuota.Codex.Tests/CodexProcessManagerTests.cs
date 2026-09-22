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

    internal Task<ICodexProcess> LaunchAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

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
