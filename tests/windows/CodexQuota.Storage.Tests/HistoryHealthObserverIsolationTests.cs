using CodexQuota.Core.Quota;
using CodexQuota.Storage.History;
using Xunit;

namespace CodexQuota.Storage.Tests;

/// <summary>
/// <see cref="HistoryPersistenceWorker.PersistenceHealthChanged"/> is raised from inside the
/// persistence failure path, which makes it the most dangerous observer seam in the Bridge: an
/// observer that throws there used to escape the repository-failure isolation, end the drain loop
/// permanently, and — on the recovery path — be mistaken for a persistence failure itself.
/// </summary>
public class HistoryHealthObserverIsolationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task AThrowingHealthObserverNeitherKillsTheWorkerNorInvertsHealth()
    {
        using var stop = new CancellationTokenSource();
        var repository = new SwitchableHistoryRepository { ShouldThrow = true };
        await using var worker = new HistoryPersistenceWorker(repository);

        var observed = new List<bool>();
        worker.PersistenceHealthChanged += healthy =>
        {
            lock (observed)
            {
                observed.Add(healthy);
            }

            throw new InvalidOperationException("health observer failure");
        };

        var run = worker.RunAsync(stop.Token);

        // 1. The failure path reports unhealthy. The observer throws from inside the catch block
        //    that reported it, so this is where the worker used to die.
        Assert.True(worker.TryEnqueue(HistoryFixtures.Update(null, HistoryFixtures.Snapshot(80, 60))));
        await WaitUntilAsync(() => !worker.IsPersistenceHealthy, Timeout);

        // 2. The recovery path must not be inverted: a successful write reports healthy, and the
        //    observer's exception must not be re-read as "persistence failed" and flip it back.
        repository.ShouldThrow = false;
        Assert.True(worker.TryEnqueue(HistoryFixtures.Update(null, HistoryFixtures.Snapshot(70, 55))));
        await WaitUntilAsync(() => repository.SuccessfulWrites == 1, Timeout);

        Assert.True(
            worker.IsPersistenceHealthy,
            "A throwing health observer turned a successful write into an unhealthy report.");
        Assert.False(run.IsCompleted, "The throwing health observer ended the persistence worker.");
        Assert.Equal(1, repository.SuccessfulWrites);

        // Every transition was still offered to the observer before it threw.
        lock (observed)
        {
            Assert.Equal(new[] { false, true }, observed);
        }

        await stop.CancelAsync();
        await run;
        Assert.False(run.IsFaulted);
    }

    [Fact]
    public async Task AThrowingHealthObserverDoesNotStarveLaterObservers()
    {
        using var stop = new CancellationTokenSource();
        var repository = new SwitchableHistoryRepository { ShouldThrow = true };
        await using var worker = new HistoryPersistenceWorker(repository);

        var laterObserverSaw = new List<bool>();

        worker.PersistenceHealthChanged += _ => throw new InvalidOperationException("first observer failure");
        worker.PersistenceHealthChanged += healthy =>
        {
            lock (laterObserverSaw)
            {
                laterObserverSaw.Add(healthy);
            }
        };

        var run = worker.RunAsync(stop.Token);

        Assert.True(worker.TryEnqueue(HistoryFixtures.Update(null, HistoryFixtures.Snapshot(80, 60))));
        await WaitUntilAsync(() => !worker.IsPersistenceHealthy, Timeout);

        lock (laterObserverSaw)
        {
            Assert.Equal(new[] { false }, laterObserverSaw);
        }

        Assert.False(run.IsCompleted);

        await stop.CancelAsync();
        await run;
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

/// <summary>A repository whose write outcome the test switches at will.</summary>
internal sealed class SwitchableHistoryRepository : IHistoryRepository
{
    private int _successfulWrites;

    internal bool ShouldThrow { get; set; }

    internal int SuccessfulWrites => Volatile.Read(ref _successfulWrites);

    public Task AppendSnapshotAsync(QuotaStateUpdate update, CancellationToken cancellationToken)
    {
        if (ShouldThrow)
        {
            return Task.FromException(new InvalidOperationException("Simulated disk failure."));
        }

        Interlocked.Increment(ref _successfulWrites);
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
