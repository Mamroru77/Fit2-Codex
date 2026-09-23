using CodexQuota.Core.Quota;
using CodexQuota.Storage.History;
using Xunit;

namespace CodexQuota.Storage.Tests;

/// <summary>
/// Persistence is a side effect of a quota update, never a precondition for one. A failing
/// repository may cost history, but it must never rewind, clear or corrupt the snapshot the
/// user is currently reading, and it must never kill the worker that serves future updates.
/// </summary>
public class HistoryFailureIsolationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task FailingPersistenceLeavesTheStoreOnTheNewValidSnapshot()
    {
        using var stop = new CancellationTokenSource();
        var store = new InMemoryQuotaStateStore();
        var repository = new FaultedWriteRepository();
        await using var worker = new HistoryPersistenceWorker(repository);
        var run = worker.RunAsync(stop.Token);

        var update = store.Replace(HistoryFixtures.Snapshot(80, 60));
        Assert.True(worker.TryEnqueue(update));
        await WaitForHealthAsync(worker, expectedHealthy: false, Timeout);

        // Both halves must be true at once: persistence really was attempted and really did
        // fail, and the store still holds the freshly published snapshot. Without the attempt
        // counter, "the store is correct" would also hold if nothing had been tried at all.
        Assert.Equal(1, repository.Attempts);
        Assert.Same(update.Current, store.Current);
        Assert.Equal(80d, store.Current!.ShortWindow.RemainingPercent);
        Assert.Equal(60d, store.Current.Weekly.RemainingPercent);
        Assert.Equal(1L, store.Sequence);

        stop.Cancel();
        await run;
        Assert.False(run.IsFaulted);
    }

    [Fact]
    public async Task WorkerSurvivesARepositoryThatThrowsSynchronously()
    {
        using var stop = new CancellationTokenSource();
        var store = new InMemoryQuotaStateStore();
        await using var worker = new HistoryPersistenceWorker(new SynchronouslyThrowingRepository());
        var run = worker.RunAsync(stop.Token);

        var update = store.Replace(HistoryFixtures.Snapshot(80, 60));
        Assert.True(worker.TryEnqueue(update));
        await WaitForHealthAsync(worker, expectedHealthy: false, Timeout);

        // A synchronous throw escapes before the await, so only a try/catch around the whole
        // persistence step keeps the loop alive. If it escaped, RunAsync would fault here.
        Assert.False(run.IsFaulted);
        Assert.Same(update.Current, store.Current);

        stop.Cancel();
        await run;
    }

    [Fact]
    public async Task WorkerKeepsAcceptingUpdatesAfterAFailure()
    {
        using var stop = new CancellationTokenSource();
        var store = new InMemoryQuotaStateStore();
        var repository = new FaultedWriteRepository();
        await using var worker = new HistoryPersistenceWorker(repository);
        var run = worker.RunAsync(stop.Token);

        Assert.True(worker.TryEnqueue(store.Replace(HistoryFixtures.Snapshot(80, 60))));
        await WaitForHealthAsync(worker, expectedHealthy: false, Timeout);

        Assert.True(worker.TryEnqueue(store.Replace(HistoryFixtures.Snapshot(70, 55))));
        await WaitUntilAsync(() => repository.Attempts >= 2, Timeout);

        Assert.False(run.IsFaulted);
        Assert.False(worker.IsPersistenceHealthy);
        Assert.Equal(2L, store.Sequence);
        Assert.Equal(70d, store.Current!.ShortWindow.RemainingPercent);

        stop.Cancel();
        await run;
    }

    [Fact]
    public async Task HealthRecoversAfterTheNextSuccessfulWrite()
    {
        using var stop = new CancellationTokenSource();
        var store = new InMemoryQuotaStateStore();
        await using var worker = new HistoryPersistenceWorker(new FailsOnceThenSucceedsRepository());
        var run = worker.RunAsync(stop.Token);

        Assert.True(worker.TryEnqueue(store.Replace(HistoryFixtures.Snapshot(80, 60))));
        await WaitForHealthAsync(worker, expectedHealthy: false, Timeout);

        Assert.True(worker.TryEnqueue(store.Replace(HistoryFixtures.Snapshot(70, 60))));
        await WaitForHealthAsync(worker, expectedHealthy: true, Timeout);

        stop.Cancel();
        await run;
        Assert.False(run.IsFaulted);
    }

    private static async Task WaitForHealthAsync(
        HistoryPersistenceWorker worker,
        bool expectedHealthy,
        TimeSpan timeout)
    {
        var reached = new TaskCompletionSource();

        void OnChanged(bool healthy)
        {
            if (healthy == expectedHealthy)
            {
                reached.TrySetResult();
            }
        }

        worker.PersistenceHealthChanged += OnChanged;
        try
        {
            if (worker.IsPersistenceHealthy == expectedHealthy)
            {
                return;
            }

            await reached.Task.WaitAsync(timeout);
        }
        finally
        {
            worker.PersistenceHealthChanged -= OnChanged;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);

        while (!condition())
        {
            await Task.Delay(10, deadline.Token);
        }
    }
}

/// <summary>Every write fails with a faulted task, the shape a real async repository produces.</summary>
internal sealed class FaultedWriteRepository : IHistoryRepository
{
    private int _attempts;

    internal int Attempts => Volatile.Read(ref _attempts);

    public Task AppendSnapshotAsync(QuotaStateUpdate update, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _attempts);
        return Task.FromException(new InvalidOperationException("Simulated disk failure."));
    }

    public Task AppendEventAsync(QuotaEvent quotaEvent, CancellationToken cancellationToken)
        => Task.FromException(new InvalidOperationException("Simulated disk failure."));

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
        => Task.FromException(new InvalidOperationException("Simulated disk failure."));
}

/// <summary>Throws before returning a task, so the exception escapes at the call site.</summary>
internal sealed class SynchronouslyThrowingRepository : IHistoryRepository
{
    public Task AppendSnapshotAsync(QuotaStateUpdate update, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Simulated disk failure.");

    public Task AppendEventAsync(QuotaEvent quotaEvent, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Simulated disk failure.");

    public Task<IReadOnlyList<HistoryPoint>> ReadHistoryAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
        => throw new InvalidOperationException("Simulated disk failure.");

    public Task<IReadOnlyList<QuotaEvent>> ReadEventsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
        => throw new InvalidOperationException("Simulated disk failure.");

    public Task DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Simulated disk failure.");
}

/// <summary>Fails the first attempt only, so recovery can be observed.</summary>
internal sealed class FailsOnceThenSucceedsRepository : IHistoryRepository
{
    private int _attempts;

    public Task AppendSnapshotAsync(QuotaStateUpdate update, CancellationToken cancellationToken)
    {
        var attempt = Interlocked.Increment(ref _attempts);

        return attempt == 1
            ? Task.FromException(new InvalidOperationException("Simulated transient failure."))
            : Task.CompletedTask;
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
