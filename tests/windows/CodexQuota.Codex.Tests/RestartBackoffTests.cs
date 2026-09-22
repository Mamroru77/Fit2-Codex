using CodexQuota.Codex.Process;
using Xunit;

namespace CodexQuota.Codex.Tests;

public class RestartBackoffTests
{
    private static readonly DateTimeOffset Origin = new(2026, 9, 22, 13, 30, 0, TimeSpan.Zero);

    [Fact]
    public void PinsTheDocumentedBackoffDelays()
    {
        var backoff = new RestartBackoff();
        var crashedAt = Origin;
        var delays = new List<TimeSpan>();

        for (var crash = 0; crash < 5; crash++)
        {
            var delay = RequireDelay(backoff, crashedAt);
            delays.Add(delay);
            crashedAt += delay;
        }

        Assert.Equal(
            new[]
            {
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30),
            },
            delays);
    }

    [Fact]
    public void FaultsOnTheSixthCrashInsideTheRollingWindow()
    {
        var backoff = new RestartBackoff();
        var crashedAt = Origin;

        for (var crash = 0; crash < 5; crash++)
        {
            crashedAt += RequireDelay(backoff, crashedAt);
        }

        // The sixth crash inside five minutes exhausts the restart budget.
        Assert.Null(backoff.NextDelay(crashedAt));
        Assert.Equal(6, backoff.CrashesInWindow);
    }

    [Fact]
    public void ForgetsCrashesThatFallOutsideTheRollingWindow()
    {
        var backoff = new RestartBackoff();

        Assert.Equal(TimeSpan.FromSeconds(1), RequireDelay(backoff, Origin));

        // Six minutes of quiet: the earlier crash no longer counts against the budget.
        var muchLater = Origin + TimeSpan.FromMinutes(6);

        Assert.Equal(TimeSpan.FromSeconds(1), RequireDelay(backoff, muchLater));
        Assert.Equal(1, backoff.CrashesInWindow);
    }

    private static TimeSpan RequireDelay(RestartBackoff backoff, DateTimeOffset crashedAt)
        => backoff.NextDelay(crashedAt)
           ?? throw new InvalidOperationException("Expected a restart delay, but the restart budget was exhausted.");
}
