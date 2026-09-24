using CodexQuota.Codex.Quota;
using CodexQuota.Core.Quota;
using CodexQuota.Core.Runtime;
using Xunit;

namespace CodexQuota.Codex.Tests;

/// <summary>
/// Two composition boundaries that Stage A Task 8 depends on: the runtime-state observer seam, and
/// the startup ordering of the synchronization service. Both were identified as deferred hardening
/// before the host lifecycle was wired to them.
/// </summary>
public class SyncServiceHardeningTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>One unambiguous 300-minute window and one 10080-minute window.</summary>
    private const string ValidRateLimits =
        """
        {"primary":{"limitId":"codex","usedPercent":25,"windowDurationMins":300,"resetsAt":1790000000},"secondary":{"limitId":"codex_weekly","usedPercent":40,"windowDurationMins":10080,"resetsAt":1790500000}}
        """;

    private const string AuthenticatedAccount = """{"authMode":"chatgpt","email":"dev@example.com"}""";

    /// <summary>Only the 300-minute window: the adapter must refuse to map it.</summary>
    private const string UnsupportedRateLimits =
        """
        {"primary":{"limitId":"codex","usedPercent":25,"windowDurationMins":300,"resetsAt":1790000000}}
        """;

    private const string TransientAccountFailure = """{"id":2,"error":{"code":-32603,"message":"temporary failure"}}""";

    /// <summary>
    /// H1 consequence: <see cref="BridgeRuntimeState.RecordSuccessfulSync"/> is reached from inside
    /// the quota read, so an observer that throws used to be indistinguishable from a broken Codex
    /// source. It must not be able to mark a healthy source as errored, and it must not be able to
    /// end the reconciliation watchdog.
    /// </summary>
    [Fact]
    public async Task AThrowingRuntimeObserverCannotMisreportAHealthySync()
    {
        await using var harness = new CodexQuotaSyncServiceTests.SyncHarness();
        using var timeout = new CancellationTokenSource(TestTimeout);

        harness.Runtime.Changed += _ => throw new InvalidOperationException("observer failure");

        var start = harness.Service.StartAsync(timeout.Token);

        await harness.Transport.WaitForWritesAsync(1, timeout.Token);
        harness.Transport.EnqueueLine("""{"id":1,"result":{}}""");
        await harness.Transport.WaitForWritesAsync(3, timeout.Token);
        harness.Transport.EnqueueLine($$"""{"id":2,"result":{{AuthenticatedAccount}}}""");
        await harness.Transport.WaitForWritesAsync(4, timeout.Token);
        harness.Transport.EnqueueLine($$"""{"id":3,"result":{{ValidRateLimits}}}""");

        await start;

        Assert.Equal(BridgeRuntimePhase.Ready, harness.Runtime.Current.Phase);
        Assert.Equal(QuotaSourceStatus.Online, harness.Runtime.Current.SourceStatus);
        Assert.NotNull(harness.Runtime.Current.LastSuccessfulSyncAt);
        Assert.True(harness.Service.IsWatchdogRunning);
        Assert.Equal(1L, harness.Store.Sequence);
    }

    /// <summary>
    /// H1 consequence, the severe half: the watchdog reports its own failures through the same
    /// runtime state that observers subscribe to, so an observer that throws used to escape the
    /// watchdog's error handling and fault the loop. A faulted loop is never awaited by anyone, so
    /// reconciliation would disappear silently and permanently.
    /// </summary>
    [Fact]
    public async Task AThrowingRuntimeObserverCannotEndTheReconciliationWatchdog()
    {
        await using var harness = new CodexQuotaSyncServiceTests.SyncHarness(TimeSpan.FromMilliseconds(25));
        using var timeout = new CancellationTokenSource(TestTimeout);

        harness.Runtime.Changed += _ => throw new InvalidOperationException("observer failure");

        var start = harness.Service.StartAsync(timeout.Token);

        await harness.Transport.WaitForWritesAsync(1, timeout.Token);
        harness.Transport.EnqueueLine("""{"id":1,"result":{}}""");
        await harness.Transport.WaitForWritesAsync(3, timeout.Token);
        harness.Transport.EnqueueLine($$"""{"id":2,"result":{{AuthenticatedAccount}}}""");
        await harness.Transport.WaitForWritesAsync(4, timeout.Token);
        harness.Transport.EnqueueLine($$"""{"id":3,"result":{{ValidRateLimits}}}""");

        await start;
        Assert.True(harness.Service.IsWatchdogRunning);

        // Answer two reconciliation reads. The payload cannot be mapped, so the read reports a
        // source-status change — exactly the path that used to throw out of the watchdog's own
        // error handling. A second tick can only happen if the loop survived the first.
        for (var tick = 0; tick < 2; tick++)
        {
            var index = 4 + tick;
            await harness.Transport.WaitForWritesAsync(index + 1, timeout.Token);

            var id = RequestIdOf(harness.Transport.WrittenLines[index]);
            harness.Transport.EnqueueLine($$"""{"id":{{id}},"result":{{UnsupportedRateLimits}}}""");
        }

        Assert.True(harness.Service.IsWatchdogRunning, "The reconciliation watchdog ended.");
    }

    /// <summary>The request id of a line the client wrote.</summary>
    private static long RequestIdOf(string line)
    {
        using var document = System.Text.Json.JsonDocument.Parse(line);
        return document.RootElement.GetProperty("id").GetInt64();
    }

    /// <summary>
    /// H2: a transient <c>account/read</c> failure used to leave the service half-started — the
    /// notification pump and worker were already running while the documented startup sequence had
    /// not completed. Nothing may be left consuming notifications in that state.
    /// </summary>
    [Fact]
    public async Task AFailedAccountReadLeavesNoBackgroundWorkRunning()
    {
        await using var harness = new CodexQuotaSyncServiceTests.SyncHarness();
        using var timeout = new CancellationTokenSource(TestTimeout);

        var start = harness.Service.StartAsync(timeout.Token);

        await harness.Transport.WaitForWritesAsync(1, timeout.Token);
        harness.Transport.EnqueueLine("""{"id":1,"result":{}}""");
        await harness.Transport.WaitForWritesAsync(3, timeout.Token);
        Assert.Equal("account/read", harness.MethodOf(2));

        harness.Transport.EnqueueLine(TransientAccountFailure);

        await Assert.ThrowsAsync<InvalidOperationException>(() => start);

        // A quota notification arriving after the failed startup must not be consumed. If it were,
        // a notification pump would be running for a service that never finished starting.
        harness.Transport.EnqueueLine(
            """
            {"method":"account/rateLimits/updated","params":{"primary":{"limitId":"codex","usedPercent":10,"windowDurationMins":300,"resetsAt":1790000000},"secondary":{"limitId":"codex_weekly","usedPercent":20,"windowDurationMins":10080,"resetsAt":1790500000}}}
            """);

        await Task.Delay(200);

        Assert.Equal(0L, harness.Store.Sequence);
        Assert.False(harness.Service.IsWatchdogRunning);
        Assert.Equal(QuotaSourceStatus.Unavailable, harness.Runtime.Current.SourceStatus);
    }

    /// <summary>
    /// H2: the same instance must remain usable, so a retry after a transient failure is a normal
    /// operation rather than an <see cref="InvalidOperationException"/> about double-starting.
    /// </summary>
    [Fact]
    public async Task StartupCanBeRetriedOnTheSameInstanceAfterATransientAccountReadFailure()
    {
        await using var harness = new CodexQuotaSyncServiceTests.SyncHarness();
        using var timeout = new CancellationTokenSource(TestTimeout);

        var first = harness.Service.StartAsync(timeout.Token);

        await harness.Transport.WaitForWritesAsync(1, timeout.Token);
        harness.Transport.EnqueueLine("""{"id":1,"result":{}}""");
        await harness.Transport.WaitForWritesAsync(3, timeout.Token);
        harness.Transport.EnqueueLine(TransientAccountFailure);

        await Assert.ThrowsAsync<InvalidOperationException>(() => first);

        // Retry: the handshake is already complete, so only the account read is repeated.
        var second = harness.Service.StartAsync(timeout.Token);

        await harness.Transport.WaitForWritesAsync(4, timeout.Token);
        Assert.Equal("account/read", harness.MethodOf(3));
        harness.Transport.EnqueueLine("""{"id":3,"result":{"authMode":"chatgpt","email":"dev@example.com"}}""");

        await harness.Transport.WaitForWritesAsync(5, timeout.Token);
        Assert.Equal("account/rateLimits/read", harness.MethodOf(4));
        harness.Transport.EnqueueLine($$"""{"id":4,"result":{{ValidRateLimits}}}""");

        await second;

        Assert.Equal(BridgeRuntimePhase.Ready, harness.Runtime.Current.Phase);
        Assert.Equal(QuotaSourceStatus.Online, harness.Runtime.Current.SourceStatus);
        Assert.Equal(1L, harness.Store.Sequence);
        Assert.True(harness.Service.IsWatchdogRunning);
    }
}
