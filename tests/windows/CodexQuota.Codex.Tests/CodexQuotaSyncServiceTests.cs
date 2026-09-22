using System.Text.Json;
using CodexQuota.Codex.Account;
using CodexQuota.Codex.Protocol;
using CodexQuota.Codex.Quota;
using CodexQuota.Codex.Tests.Fakes;
using CodexQuota.Core.Quota;
using CodexQuota.Core.Runtime;
using Xunit;

namespace CodexQuota.Codex.Tests;

public class CodexQuotaSyncServiceTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>One unambiguous 300-minute window and one 10080-minute window.</summary>
    private const string ValidRateLimits =
        """
        {"primary":{"limitId":"codex","usedPercent":25,"windowDurationMins":300,"resetsAt":1790000000},"secondary":{"limitId":"codex_weekly","usedPercent":40,"windowDurationMins":10080,"resetsAt":1790500000}}
        """;

    [Fact]
    public async Task StartupInitializesReadsTheAccountThenRateLimitsAndReachesReady()
    {
        await using var harness = new SyncHarness();
        using var timeout = new CancellationTokenSource(TestTimeout);

        var start = harness.Service.StartAsync(timeout.Token);

        // 1. initialize, before anything else.
        await harness.Transport.WaitForWritesAsync(1, timeout.Token);
        Assert.Equal("initialize", harness.MethodOf(0));

        // 2. initialized, only once the initialize response arrived.
        harness.Transport.EnqueueLine("""{"id":1,"result":{}}""");
        await harness.Transport.WaitForWritesAsync(2, timeout.Token);
        Assert.Equal("initialized", harness.MethodOf(1));
        Assert.False(harness.HasRequestId(1));

        // 3. account/read, before any quota read.
        await harness.Transport.WaitForWritesAsync(3, timeout.Token);
        Assert.Equal("account/read", harness.MethodOf(2));

        // 4. account/rateLimits/read, only because the account is authenticated.
        harness.Transport.EnqueueLine("""{"id":2,"result":{"authMode":"chatgpt","email":"dev@example.com"}}""");
        await harness.Transport.WaitForWritesAsync(4, timeout.Token);
        Assert.Equal("account/rateLimits/read", harness.MethodOf(3));

        harness.Transport.EnqueueLine($$"""{"id":3,"result":{{ValidRateLimits}}}""");

        await start;

        Assert.Equal(BridgeRuntimePhase.Ready, harness.Runtime.Current.Phase);
        Assert.Equal(QuotaSourceStatus.Online, harness.Runtime.Current.SourceStatus);
        Assert.NotNull(harness.Runtime.Current.LastSuccessfulSyncAt);
        Assert.True(harness.Service.IsWatchdogRunning);

        Assert.Equal(1L, harness.Store.Sequence);
        Assert.Equal(75d, harness.Store.Current!.ShortWindow.RemainingPercent);
        Assert.Equal(60d, harness.Store.Current!.Weekly.RemainingPercent);
    }

    [Fact]
    public async Task StartupWithoutAuthenticationSettlesAtAuthRequiredWithoutReadingRateLimits()
    {
        await using var harness = new SyncHarness();
        using var timeout = new CancellationTokenSource(TestTimeout);

        var start = harness.Service.StartAsync(timeout.Token);

        await harness.Transport.WaitForWritesAsync(1, timeout.Token);
        harness.Transport.EnqueueLine("""{"id":1,"result":{}}""");

        await harness.Transport.WaitForWritesAsync(3, timeout.Token);
        harness.Transport.EnqueueLine("""{"id":2,"result":{}}""");

        await start;

        Assert.Equal(BridgeRuntimePhase.AuthRequired, harness.Runtime.Current.Phase);
        Assert.Equal(QuotaSourceStatus.AuthRequired, harness.Runtime.Current.SourceStatus);
        Assert.Null(harness.Runtime.Current.LastSuccessfulSyncAt);
        Assert.False(harness.Service.IsWatchdogRunning);

        Assert.Null(harness.Store.Current);
        Assert.Equal(0, harness.CountRequests("account/rateLimits/read"));

        // Nothing beyond the documented startup sequence may be sent.
        Assert.Equal(3, harness.WrittenLines.Count);
    }

    [Fact]
    public async Task RateLimitsUpdatedNotificationReplacesTheStateImmediately()
    {
        await using var harness = new SyncHarness();
        using var timeout = new CancellationTokenSource(TestTimeout);
        await StartAuthenticatedAsync(harness, timeout.Token);

        harness.Transport.EnqueueLine(
            """
            {"method":"account/rateLimits/updated","params":{"primary":{"limitId":"codex","usedPercent":10,"windowDurationMins":300,"resetsAt":1790000000},"secondary":{"limitId":"codex_weekly","usedPercent":20,"windowDurationMins":10080,"resetsAt":1790500000}}}
            """);

        await harness.Store.WaitForReplacementsAsync(2, timeout.Token);

        Assert.Equal(2L, harness.Store.Sequence);
        Assert.Equal(90d, harness.Store.Current!.ShortWindow.RemainingPercent);
        Assert.Equal(80d, harness.Store.Current!.Weekly.RemainingPercent);
        Assert.Equal(QuotaSourceStatus.Online, harness.Runtime.Current.SourceStatus);

        // The pushed payload is authoritative: no reconciliation read is needed.
        Assert.Equal(1, harness.CountRequests("account/rateLimits/read"));
    }

    [Fact]
    public async Task UnsupportedRateLimitsPayloadKeepsTheLastTrustedSnapshotAndFlagsTheSchema()
    {
        await using var harness = new SyncHarness();
        using var timeout = new CancellationTokenSource(TestTimeout);
        await StartAuthenticatedAsync(harness, timeout.Token);

        var trusted = harness.Store.Current;

        // No 10080-minute window: the payload must not be guessed at.
        harness.Transport.EnqueueLine(
            """
            {"method":"account/rateLimits/updated","params":{"primary":{"limitId":"codex","usedPercent":10,"windowDurationMins":300,"resetsAt":1790000000}}}
            """);

        await harness.WaitForSourceStatusAsync(QuotaSourceStatus.SourceSchemaUnsupported, timeout.Token);

        Assert.Same(trusted, harness.Store.Current);
        Assert.Equal(1L, harness.Store.Sequence);
        Assert.Equal(QuotaSourceStatus.SourceSchemaUnsupported, harness.Runtime.Current.SourceStatus);
        Assert.Equal(BridgeRuntimePhase.Ready, harness.Runtime.Current.Phase);
    }

    [Fact]
    public async Task RefreshNowAsyncPerformsExactlyOneRateLimitRead()
    {
        await using var harness = new SyncHarness();
        using var timeout = new CancellationTokenSource(TestTimeout);
        await StartAuthenticatedAsync(harness, timeout.Token);

        Assert.Equal(1, harness.CountRequests("account/rateLimits/read"));

        var refresh = harness.Service.RefreshNowAsync(timeout.Token);

        await harness.Transport.WaitForWritesAsync(5, timeout.Token);
        Assert.Equal("account/rateLimits/read", harness.MethodOf(4));

        harness.Transport.EnqueueLine(
            """
            {"id":4,"result":{"primary":{"limitId":"codex","usedPercent":10,"windowDurationMins":300,"resetsAt":1790000000},"secondary":{"limitId":"codex_weekly","usedPercent":20,"windowDurationMins":10080,"resetsAt":1790500000}}}
            """);

        await refresh;

        Assert.Equal(2, harness.CountRequests("account/rateLimits/read"));
        Assert.Equal(2L, harness.Store.Sequence);
        Assert.Equal(90d, harness.Store.Current!.ShortWindow.RemainingPercent);
    }

    [Fact]
    public async Task FailedLoginCompletionSettlesAtAuthRequiredWithoutRetrying()
    {
        await using var harness = new SyncHarness();
        using var timeout = new CancellationTokenSource(TestTimeout);
        await StartUnauthenticatedAsync(harness, timeout.Token);

        var writesBefore = harness.WrittenLines.Count;

        harness.Transport.EnqueueLine("""{"method":"account/login/completed","params":{"success":false}}""");

        // A cancelled login must not schedule a retry loop: no outbound traffic may follow.
        await harness.AssertNoFurtherWritesAsync(writesBefore, timeout.Token);

        Assert.Equal(BridgeRuntimePhase.AuthRequired, harness.Runtime.Current.Phase);
        Assert.Equal(QuotaSourceStatus.AuthRequired, harness.Runtime.Current.SourceStatus);
        Assert.False(harness.Service.IsWatchdogRunning);
        Assert.Equal(0, harness.CountRequests("account/login/start"));
        Assert.Equal(0, harness.CountRequests("account/rateLimits/read"));
    }

    [Fact]
    public async Task AccountUpdatedWithNullAuthModeSettlesAtAuthRequiredAndStopsTheWatchdog()
    {
        await using var harness = new SyncHarness();
        using var timeout = new CancellationTokenSource(TestTimeout);
        await StartAuthenticatedAsync(harness, timeout.Token);

        Assert.True(harness.Service.IsWatchdogRunning);
        var writesBefore = harness.WrittenLines.Count;

        harness.Transport.EnqueueLine("""{"method":"account/updated","params":{"authMode":null}}""");

        await harness.WaitForPhaseAsync(BridgeRuntimePhase.AuthRequired, timeout.Token);

        Assert.Equal(QuotaSourceStatus.AuthRequired, harness.Runtime.Current.SourceStatus);
        Assert.False(harness.Service.IsWatchdogRunning);

        // The last trusted snapshot is retained so the user still sees quota plus an error marker.
        Assert.Equal(1L, harness.Store.Sequence);
        Assert.Equal(0, harness.CountRequests("account/login/start"));
        await harness.AssertNoFurtherWritesAsync(writesBefore, timeout.Token);
    }

    [Fact]
    public async Task SuccessfulLoginCompletionReadsTheAccountOnceAndStartsSyncing()
    {
        await using var harness = new SyncHarness();
        using var timeout = new CancellationTokenSource(TestTimeout);
        await StartUnauthenticatedAsync(harness, timeout.Token);

        Assert.Equal(BridgeRuntimePhase.AuthRequired, harness.Runtime.Current.Phase);

        harness.Transport.EnqueueLine("""{"method":"account/login/completed","params":{"success":true}}""");

        // The Bridge confirms the account (id 3) and then performs exactly one quota read (id 4).
        await harness.Transport.WaitForWritesAsync(4, timeout.Token);
        Assert.Equal("account/read", harness.MethodOf(3));
        harness.Transport.EnqueueLine("""{"id":3,"result":{"authMode":"chatgpt"}}""");

        await harness.Transport.WaitForWritesAsync(5, timeout.Token);
        Assert.Equal("account/rateLimits/read", harness.MethodOf(4));
        harness.Transport.EnqueueLine($$"""{"id":4,"result":{{ValidRateLimits}}}""");

        await harness.WaitForPhaseAsync(BridgeRuntimePhase.Ready, timeout.Token);

        Assert.Equal(1, harness.CountRequests("account/rateLimits/read"));
        Assert.True(harness.Service.IsWatchdogRunning);
        Assert.Equal(1L, harness.Store.Sequence);
        Assert.Equal(75d, harness.Store.Current!.ShortWindow.RemainingPercent);
    }

    private static Task StartAuthenticatedAsync(SyncHarness harness, CancellationToken cancellationToken)
        => StartAsync(harness, """{"authMode":"chatgpt","email":"dev@example.com"}""", cancellationToken);

    private static Task StartUnauthenticatedAsync(SyncHarness harness, CancellationToken cancellationToken)
        => StartAsync(harness, "{}", cancellationToken);

    private static async Task StartAsync(SyncHarness harness, string accountResult, CancellationToken cancellationToken)
    {
        var start = harness.Service.StartAsync(cancellationToken);

        await harness.Transport.WaitForWritesAsync(1, cancellationToken);
        harness.Transport.EnqueueLine("""{"id":1,"result":{}}""");

        await harness.Transport.WaitForWritesAsync(3, cancellationToken);
        harness.Transport.EnqueueLine($$"""{"id":2,"result":{{accountResult}}}""");

        if (accountResult == "{}")
        {
            await start;
            return;
        }

        await harness.Transport.WaitForWritesAsync(4, cancellationToken);
        harness.Transport.EnqueueLine($$"""{"id":3,"result":{{ValidRateLimits}}}""");

        await start;
    }

    /// <summary>
    /// Wires the real RPC client, account service, adapter, store and runtime state together so
    /// the synchronization service is exercised end to end over an in-memory transport.
    /// </summary>
    internal sealed class SyncHarness : IAsyncDisposable
    {
        private readonly CodexRpcClient _client;

        internal SyncHarness()
        {
            Transport = new FakeJsonRpcTransport();
            _client = new CodexRpcClient(Transport);
            Store = new SignallingQuotaStateStore();
            Runtime = new BridgeRuntimeState();
            Service = new CodexQuotaSyncService(
                _client,
                new CodexAccountService(_client),
                new RateLimitAdapter(),
                Store,
                Runtime);
        }

        internal FakeJsonRpcTransport Transport { get; }

        internal SignallingQuotaStateStore Store { get; }

        internal BridgeRuntimeState Runtime { get; }

        internal CodexQuotaSyncService Service { get; }

        internal IReadOnlyList<string> WrittenLines => Transport.WrittenLines;

        internal string? MethodOf(int index) => MethodOfLine(WrittenLines[index]);

        internal bool HasRequestId(int index)
        {
            using var document = JsonDocument.Parse(WrittenLines[index]);
            return document.RootElement.TryGetProperty("id", out _);
        }

        internal int CountRequests(string method)
            => WrittenLines.Count(line => MethodOfLine(line) == method);

        internal Task WaitForPhaseAsync(BridgeRuntimePhase phase, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnChanged(BridgeRuntimeSnapshot snapshot)
            {
                if (snapshot.Phase == phase)
                {
                    completion.TrySetResult();
                }
            }

            Runtime.Changed += OnChanged;
            OnChanged(Runtime.Current);

            return completion.Task.WaitAsync(cancellationToken);
        }

        internal Task WaitForSourceStatusAsync(QuotaSourceStatus status, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnChanged(BridgeRuntimeSnapshot snapshot)
            {
                if (snapshot.SourceStatus == status)
                {
                    completion.TrySetResult();
                }
            }

            Runtime.Changed += OnChanged;
            OnChanged(Runtime.Current);

            return completion.Task.WaitAsync(cancellationToken);
        }

        /// <summary>
        /// Asserts that the service stays quiet: this is a bounded negative check, so it is only
        /// used where the expected behaviour is that no traffic is produced at all.
        /// </summary>
        internal async Task AssertNoFurtherWritesAsync(int writesBefore, CancellationToken cancellationToken)
        {
            using var quiet = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            quiet.CancelAfter(TimeSpan.FromMilliseconds(250));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => Transport.WaitForWritesAsync(writesBefore + 1, quiet.Token));
        }

        internal static string? MethodOfLine(string line)
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.TryGetProperty("method", out var method) ? method.GetString() : null;
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            await _client.DisposeAsync();
        }
    }
}

public class BridgeRuntimeStateTests
{
    [Fact]
    public void ChangedFiresOnlyWhenTheStateActuallyChanges()
    {
        var runtime = new BridgeRuntimeState();
        var observed = new List<BridgeRuntimeSnapshot>();
        runtime.Changed += observed.Add;

        runtime.SetPhase(BridgeRuntimePhase.Ready);
        runtime.SetPhase(BridgeRuntimePhase.Ready);
        runtime.SetSourceStatus(QuotaSourceStatus.Online);
        runtime.RecordSuccessfulSync(new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));

        // Phase and source status move together, so consumers cannot observe a contradictory pair.
        runtime.SetPhase(BridgeRuntimePhase.AuthRequired, QuotaSourceStatus.AuthRequired);

        Assert.Equal(4, observed.Count);
        Assert.Equal(BridgeRuntimePhase.AuthRequired, runtime.Current.Phase);
        Assert.Equal(QuotaSourceStatus.AuthRequired, runtime.Current.SourceStatus);
        Assert.Equal(
            new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero),
            runtime.Current.LastSuccessfulSyncAt);
    }
}

/// <summary>
/// <see cref="IQuotaStateStore"/> that wraps the production in-memory store and lets a test await
/// the n-th replacement without sleeping.
/// </summary>
internal sealed class SignallingQuotaStateStore : IQuotaStateStore
{
    private readonly InMemoryQuotaStateStore _inner = new();
    private readonly object _gate = new();

    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public QuotaSnapshot? Current => _inner.Current;

    public long Sequence => _inner.Sequence;

    public QuotaStateUpdate Replace(QuotaSnapshot snapshot)
    {
        var update = _inner.Replace(snapshot);

        TaskCompletionSource signal;

        lock (_gate)
        {
            signal = _signal;
            _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        signal.TrySetResult();
        return update;
    }

    internal async Task WaitForReplacementsAsync(long count, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task signal;

            lock (_gate)
            {
                if (_inner.Sequence >= count)
                {
                    return;
                }

                signal = _signal.Task;
            }

            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
