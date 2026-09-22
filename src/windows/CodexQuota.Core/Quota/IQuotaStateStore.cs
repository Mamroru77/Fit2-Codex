namespace CodexQuota.Core.Quota;

/// <summary>
/// Holds the current quota snapshot in memory. This is the only state the API
/// layer is allowed to read from.
/// </summary>
public interface IQuotaStateStore
{
    /// <summary>The current snapshot, or <c>null</c> when nothing has been published yet.</summary>
    QuotaSnapshot? Current { get; }

    /// <summary>Number of successful replacements performed so far.</summary>
    long Sequence { get; }

    /// <summary>
    /// Atomically replaces the current snapshot and returns the update, including the
    /// snapshot that was current beforehand.
    /// </summary>
    QuotaStateUpdate Replace(QuotaSnapshot snapshot);
}

/// <summary>
/// Result of a successful <see cref="IQuotaStateStore.Replace"/> call.
/// </summary>
/// <param name="Sequence">The sequence number assigned to this replacement.</param>
/// <param name="Previous">The snapshot that was current before this replacement, if any.</param>
/// <param name="Current">The snapshot that is now current.</param>
public record QuotaStateUpdate(
    long Sequence,
    QuotaSnapshot? Previous,
    QuotaSnapshot Current);
