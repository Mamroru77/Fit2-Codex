using System.Diagnostics;
using CodexQuota.Codex.Process;
using Xunit;

namespace CodexQuota.Codex.Tests;

/// <summary>
/// The Bridge owns the Codex App Server child, so disposal has to end it. These tests pin the two
/// failure modes that make ownership meaningless: waiting forever for a child that will not die,
/// and reporting success for a child that is still running.
/// </summary>
public class ChildProcessTerminationTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(50);

    [Fact]
    public async Task AChildThatExitsOnTheFirstKillIsReportedAsExited()
    {
        var handle = new FakeChildProcessHandle { ExitsAfterKills = 1 };
        var diagnostics = new List<string>();

        var outcome = await ChildProcessTermination.TerminateAsync(
            handle, ShortTimeout, diagnostics.Add, CancellationToken.None);

        Assert.Equal(ChildTerminationOutcome.Exited, outcome);
        Assert.Equal(1, handle.KillCount);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task AChildThatIsAlreadyGoneIsNotKilled()
    {
        var handle = new FakeChildProcessHandle { HasExited = true };

        var outcome = await ChildProcessTermination.TerminateAsync(
            handle, ShortTimeout, null, CancellationToken.None);

        Assert.Equal(ChildTerminationOutcome.Exited, outcome);
        Assert.Equal(0, handle.KillCount);
    }

    [Fact]
    public async Task AChildThatSurvivesIsReportedInsteadOfSilentlyPretendedDead()
    {
        var handle = new FakeChildProcessHandle { ExitsAfterKills = int.MaxValue };
        var diagnostics = new List<string>();

        var outcome = await ChildProcessTermination.TerminateAsync(
            handle, ShortTimeout, diagnostics.Add, CancellationToken.None);

        // Not terminated, and said so — a caller must never be told a live child was reaped.
        Assert.Equal(ChildTerminationOutcome.StillRunning, outcome);

        // One initial attempt plus one final best-effort attempt, then the policy stops instead of
        // waiting forever.
        Assert.Equal(2, handle.KillCount);
        Assert.Equal(2, handle.WaitCount);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("still running", diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ASurvivingChildDoesNotBlockForever()
    {
        var handle = new FakeChildProcessHandle { ExitsAfterKills = int.MaxValue };

        var started = Stopwatch.StartNew();

        await ChildProcessTermination.TerminateAsync(handle, ShortTimeout, null, CancellationToken.None);

        started.Stop();

        // Two bounded waits, so the total is bounded by roughly twice the timeout.
        Assert.True(
            started.Elapsed < TimeSpan.FromSeconds(5),
            $"Termination took {started.Elapsed}; it must be bounded.");
    }

    [Fact]
    public async Task DisposingTheOwnedProcessEndsARealChild()
    {
        // A real child that stays alive until it is killed: an interactive shell reading stdin.
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var process = CodexAppServerProcess.StartFrom(startInfo);
        var child = System.Diagnostics.Process.GetProcessById(process.ProcessId);

        Assert.False(child.HasExited, "The child exited before disposal, so the test proves nothing.");

        await process.DisposeAsync();

        child.Refresh();
        Assert.True(child.HasExited, "Disposing the owned process left the child running.");
    }
}

/// <summary>
/// A child process the test controls completely, including the case a real process cannot produce
/// on demand: a kill that simply does not take effect.
/// </summary>
internal sealed class FakeChildProcessHandle : IChildProcessHandle
{
    private int _kills;

    /// <summary>Whether the child is considered gone. Public because it implements the interface.</summary>
    public bool HasExited { get; set; }

    internal int KillCount => Volatile.Read(ref _kills);

    internal int WaitCount { get; private set; }

    /// <summary>
    /// Number of kills after which the child is considered gone. <see cref="int.MaxValue"/> models a
    /// child that never dies.
    /// </summary>
    internal int ExitsAfterKills { get; set; }

    public void Kill()
    {
        var kills = Interlocked.Increment(ref _kills);

        if (kills >= ExitsAfterKills)
        {
            HasExited = true;
        }
    }

    public Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        WaitCount++;
        return Task.FromResult(HasExited);
    }
}
