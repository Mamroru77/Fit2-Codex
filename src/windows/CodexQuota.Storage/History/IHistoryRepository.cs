using CodexQuota.Core.Quota;

namespace CodexQuota.Storage.History;

/// <summary>
/// Reads and writes the 24-hour quota history and the user-meaningful event log.
/// </summary>
/// <remarks>
/// Implementations may fail. Callers must treat every member as best effort: a failed
/// write costs history, never the in-memory quota state.
/// </remarks>
public interface IHistoryRepository
{
    /// <summary>Appends one sample taken from the snapshot that was just published.</summary>
    Task AppendSnapshotAsync(QuotaStateUpdate update, CancellationToken cancellationToken);

    /// <summary>Appends one user-meaningful event.</summary>
    Task AppendEventAsync(QuotaEvent quotaEvent, CancellationToken cancellationToken);

    /// <summary>Returns the samples recorded between <paramref name="from"/> and <paramref name="to"/> inclusive, oldest first.</summary>
    Task<IReadOnlyList<HistoryPoint>> ReadHistoryAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken);

    /// <summary>Returns the events recorded between <paramref name="from"/> and <paramref name="to"/> inclusive, oldest first.</summary>
    Task<IReadOnlyList<QuotaEvent>> ReadEventsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken);

    /// <summary>Deletes every sample and event older than <paramref name="cutoff"/>.</summary>
    Task DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}
