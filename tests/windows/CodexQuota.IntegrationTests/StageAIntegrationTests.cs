using CodexQuota.Codex.Process;
using CodexQuota.Core.Quota;
using CodexQuota.Core.Runtime;
using CodexQuota.Desktop.Runtime;
using CodexQuota.Storage.History;

namespace CodexQuota.IntegrationTests;

/// <summary>
/// Drives the composed Bridge against a fake App Server: process manager, RPC client, account
/// service, quota sync, state store and history persistence, with no WPF and no real child
/// process involved.
/// </summary>
public class StageAIntegrationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task BridgeReachesReadyAndPublishesQuotaToHistory()
    {
        var server = new FakeCodexAppServer();
        var store = new InMemoryQuotaStateStore();
        var runtime = new BridgeRuntimeState();
        var history = new RecordingHistoryRepository();

        await using var worker = new HistoryPersistenceWorker(history);
        await using var host = new BridgeHostedService(
            _ => Task.FromResult<ICodexProcess>(server),
            store,
            runtime,
            worker,
            new RestartBackoff(),
            new ImmediateTimeSource());

        await host.StartAsync(CancellationToken.None);

        var reachedReady = await TryWaitUntilAsync(
            () => runtime.Current.Phase == BridgeRuntimePhase.Ready,
            Timeout);

        Assert.True(
            reachedReady,
            $"Never reached Ready. phase={runtime.Current.Phase}, source={runtime.Current.SourceStatus}, "
            + $"initialized={server.InitializedObserved.IsCompleted}, accountReads={server.AccountReadCount}, "
            + $"rateLimitReads={server.RateLimitReadCount}, store={(store.Current is null ? "null" : "set")}");

        // The documented handshake must have happened in order.
        Assert.True(server.InitializedObserved.IsCompleted, "The child never observed 'initialized'.");
        Assert.True(server.AccountReadCount >= 1, "The account was never read.");
        Assert.True(server.RateLimitReadCount >= 1, "Rate limits were never read.");

        // First read: 20% used on the short window, 40% on the weekly window.
        var first = store.Current;
        Assert.NotNull(first);
        Assert.Equal(80d, first!.ShortWindow.RemainingPercent, 0.0001);
        Assert.Equal(60d, first.Weekly.RemainingPercent, 0.0001);
        Assert.Equal(300, first.ShortWindow.WindowMinutes);
        Assert.Equal(10080, first.Weekly.WindowMinutes);

        // The server now pushes quota movement; the published state must follow it.
        await server.PushRateLimitUpdateAsync();

        var changed = await TryWaitUntilAsync(
            () => store.Current is { } current && current.ShortWindow.RemainingPercent < 79d,
            Timeout);

        Assert.True(
            changed,
            $"The pushed update never changed the state. source={runtime.Current.SourceStatus}, "
            + $"phase={runtime.Current.Phase}, sequence={store.Sequence}, "
            + $"short={store.Current?.ShortWindow.RemainingPercent}, "
            + $"accountReads={server.AccountReadCount}, rateLimitReads={server.RateLimitReadCount}, "
            + $"notifications={server.UpdatedNotificationCount}, history={history.Snapshots.Count}");

        var published = store.Current!;
        Assert.Equal(45d, published.ShortWindow.RemainingPercent, 0.0001);
        Assert.Equal(60d, published.Weekly.RemainingPercent, 0.0001);
        Assert.Equal(300, published.ShortWindow.WindowMinutes);
        Assert.Equal(10080, published.Weekly.WindowMinutes);

        // Persistence is strictly ordered, so the first read (20% used) and the pushed update
        // (55% used) must appear there in that order: the state really did change.
        await WaitUntilAsync(() => history.Snapshots.Count >= 2, Timeout);
        Assert.Equal(80d, history.Snapshots[0].ShortWindow.RemainingPercent, 0.0001);
        Assert.Equal(45d, history.Snapshots[1].ShortWindow.RemainingPercent, 0.0001);

        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CrashLoopReachesFaultedAfterTheConfiguredThreshold()
    {
        var launches = 0;
        var store = new InMemoryQuotaStateStore();
        var runtime = new BridgeRuntimeState();
        var history = new RecordingHistoryRepository();

        // Two crashes are tolerated inside the window; the third exhausts the budget.
        var backoff = new RestartBackoff(
            [TimeSpan.Zero, TimeSpan.Zero],
            TimeSpan.FromMinutes(5),
            crashThreshold: 2);

        await using var worker = new HistoryPersistenceWorker(history);
        await using var host = new BridgeHostedService(
            _ =>
            {
                Interlocked.Increment(ref launches);
                var child = new FakeCodexAppServer();
                child.Exit(1);
                return Task.FromResult<ICodexProcess>(child);
            },
            store,
            runtime,
            worker,
            backoff,
            new ImmediateTimeSource());

        await host.StartAsync(CancellationToken.None);

        await WaitUntilAsync(() => host.Status == CodexProcessStatus.Faulted, Timeout);

        Assert.True(launches >= 3, $"Expected at least three launches, saw {launches}.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);

        while (!condition())
        {
            await Task.Delay(10, deadline.Token);
        }
    }

    private static async Task<bool> TryWaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);

        while (!condition())
        {
            try
            {
                await Task.Delay(10, deadline.Token);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Records what the persistence worker hands over, so the host wiring is observable.</summary>
internal sealed class RecordingHistoryRepository : IHistoryRepository
{
    private readonly List<QuotaSnapshot> _snapshots = [];
    private readonly object _gate = new();

    internal TaskCompletionSource FirstSnapshot { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal IReadOnlyList<QuotaSnapshot> Snapshots
    {
        get
        {
            lock (_gate)
            {
                return _snapshots.ToArray();
            }
        }
    }

    public Task AppendSnapshotAsync(QuotaStateUpdate update, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _snapshots.Add(update.Current);
        }

        FirstSnapshot.TrySetResult();

        return Task.CompletedTask;
    }

    public Task AppendEventAsync(QuotaEvent quotaEvent, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task<IReadOnlyList<HistoryPoint>> ReadHistoryAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<HistoryPoint>>(Array.Empty<HistoryPoint>());

    public Task<IReadOnlyList<QuotaEvent>> ReadEventsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<QuotaEvent>>(Array.Empty<QuotaEvent>());

    public Task DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

/// <summary>Never actually waits: restart backoff is exercised without real time passing.</summary>
internal sealed class ImmediateTimeSource : ICodexTimeSource
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
}
