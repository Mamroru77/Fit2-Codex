namespace CodexQuota.Core.Quota;

/// <summary>
/// One persisted sample of the 24-hour history. Timestamps are UTC.
/// </summary>
/// <param name="Timestamp">UTC instant at which the sample was recorded.</param>
/// <param name="ShortWindowRemainingPercent">Remaining percentage of the short window.</param>
/// <param name="WeeklyRemainingPercent">Remaining percentage of the weekly window.</param>
public record HistoryPoint(
    DateTimeOffset Timestamp,
    double ShortWindowRemainingPercent,
    double WeeklyRemainingPercent);
