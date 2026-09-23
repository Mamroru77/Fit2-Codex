using System.Threading.Channels;
using CodexQuota.Core.Quota;

namespace CodexQuota.Storage.History;

/// <summary>
/// Drains published quota updates into <see cref="IHistoryRepository"/> on a background reader.
/// </summary>
/// <remarks>
/// Persistence sits strictly downstream of the quota state store. The store is updated by its
/// own code path; this worker only observes the resulting <see cref="QuotaStateUpdate"/>. That
/// is what makes the isolation guarantee possible: when the repository fails, the snapshot the
/// user is reading has already been published and is never touched again.
/// </remarks>
public sealed class HistoryPersistenceWorker : IAsyncDisposable
{
    /// <summary>How much history is kept. The product promise is 24 hours, plus one hour of slack.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(25);

    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(30);

    private readonly IHistoryRepository _repository;
    private readonly HistoryWritePolicy _policy;
    private readonly Channel<QuotaStateUpdate> _queue;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _healthGate = new();

    private HistoryPoint? _lastPersisted;
    private DateTimeOffset _nextCleanupAt;
    private bool _healthy = true;
    private bool _disposed;

    public HistoryPersistenceWorker(
        IHistoryRepository repository,
        HistoryWritePolicy? policy = null,
        int capacity = 256)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _repository = repository;
        _policy = policy ?? new HistoryWritePolicy();
        _queue = Channel.CreateBounded<QuotaStateUpdate>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });

        _nextCleanupAt = DateTimeOffset.UtcNow + CleanupInterval;
    }

    /// <summary>
    /// <c>true</c> while the last persistence attempt succeeded. History may be missing while
    /// this is <c>false</c>, but the quota state the user sees is still valid.
    /// </summary>
    public bool IsPersistenceHealthy
    {
        get
        {
            lock (_healthGate)
            {
                return _healthy;
            }
        }
    }

    /// <summary>Raised when <see cref="IsPersistenceHealthy"/> changes.</summary>
    public event Action<bool>? PersistenceHealthChanged;

    /// <summary>
    /// Queues an update for persistence. Returns <c>false</c> when the backlog is full or the
    /// worker has been disposed; the caller must not treat that as a failure of the quota state.
    /// </summary>
    public bool TryEnqueue(QuotaStateUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        return !_disposed && _queue.Writer.TryWrite(update);
    }

    /// <summary>
    /// Reads queued updates until the channel is completed, <paramref name="cancellationToken"/>
    /// is cancelled, or the worker is disposed. Never faults: repository failures are absorbed.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);

        try
        {
            await DrainAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested.
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _queue.Writer.TryComplete();

        // Not disposed on purpose: RunAsync still holds a linked token source over it, and
        // cancelling is enough to release the reader.
        _stop.Cancel();

        return ValueTask.CompletedTask;
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        while (await _queue.Reader.WaitToReadAsync(cancellationToken))
        {
            while (_queue.Reader.TryRead(out var update))
            {
                await PersistAsync(update, cancellationToken);
            }

            await PruneIfDueAsync(cancellationToken);
        }
    }

    private async Task PersistAsync(QuotaStateUpdate update, CancellationToken cancellationToken)
    {
        // Everything below is best effort. A failure here may cost history points; it must
        // never escape into the caller and must never reach back into the quota state store.
        try
        {
            if (!_policy.ShouldWrite(update, _lastPersisted, DateTimeOffset.UtcNow))
            {
                return;
            }

            await _repository.AppendSnapshotAsync(update, cancellationToken);

            // Only recorded after a real write, so a failed attempt still leaves the next
            // update eligible under the "nothing persisted yet" rule.
            _lastPersisted = new HistoryPoint(
                update.Current.GeneratedAt,
                update.Current.ShortWindow.RemainingPercent,
                update.Current.Weekly.RemainingPercent);

            SetHealthy(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetHealthy(false);
        }
    }

    private async Task PruneIfDueAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _nextCleanupAt)
        {
            return;
        }

        _nextCleanupAt = now + CleanupInterval;

        try
        {
            await _repository.DeleteOlderThanAsync(now - Retention, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetHealthy(false);
        }
    }

    private void SetHealthy(bool healthy)
    {
        bool changed;
        lock (_healthGate)
        {
            changed = _healthy != healthy;
            _healthy = healthy;
        }

        if (changed)
        {
            PersistenceHealthChanged?.Invoke(healthy);
        }
    }
}
