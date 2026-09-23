using CodexQuota.Core.Quota;

namespace CodexQuota.Storage.History;

/// <summary>
/// Decides whether a published snapshot deserves a row in the 24-hour history.
/// </summary>
/// <remarks>
/// The goal is a chart that is dense enough to show a real change and sparse enough to stay
/// small: write when something meaningful happened, otherwise at most one sample every five
/// minutes.
/// </remarks>
public sealed class HistoryWritePolicy
{
    /// <summary>Longest interval between two samples that carry no new information.</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Percentages are derived by subtraction, so they are compared with a tolerance rather
    /// than for exact equality.
    /// </summary>
    private const double PercentTolerance = 0.0001;

    /// <param name="update">The update produced by the quota state store.</param>
    /// <param name="lastPersisted">The most recent sample actually written, or <c>null</c> when nothing has been written yet.</param>
    /// <param name="now">Current UTC instant.</param>
    public bool ShouldWrite(QuotaStateUpdate update, HistoryPoint? lastPersisted, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(update);

        // Nothing on disk yet: the very first sample anchors the chart.
        if (lastPersisted is null)
        {
            return true;
        }

        var current = update.Current;

        if (!NearlyEqual(lastPersisted.ShortWindowRemainingPercent, current.ShortWindow.RemainingPercent))
        {
            return true;
        }

        if (!NearlyEqual(lastPersisted.WeeklyRemainingPercent, current.Weekly.RemainingPercent))
        {
            return true;
        }

        // The disk row only stores percentages, so a reset is detected by comparing the two
        // in-memory snapshots. A window that restarted is a different window even when it
        // happens to report the same remaining percentage.
        if (update.Previous is not null
            && (update.Previous.ShortWindow.ResetsAt != current.ShortWindow.ResetsAt
                || update.Previous.Weekly.ResetsAt != current.Weekly.ResetsAt))
        {
            return true;
        }

        return now - lastPersisted.Timestamp >= MinimumInterval;
    }

    private static bool NearlyEqual(double left, double right)
        => Math.Abs(left - right) <= PercentTolerance;
}
