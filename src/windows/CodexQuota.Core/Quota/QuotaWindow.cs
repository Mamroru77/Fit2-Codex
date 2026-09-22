namespace CodexQuota.Core.Quota;

/// <summary>
/// A single normalized rate-limit window.
/// </summary>
/// <param name="UsedPercent">Percentage of the window already consumed.</param>
/// <param name="RemainingPercent">Percentage of the window still available.</param>
/// <param name="WindowMinutes">Length of the window in minutes.</param>
/// <param name="ResetsAt">UTC instant at which the window resets.</param>
public record QuotaWindow(
    double UsedPercent,
    double RemainingPercent,
    int WindowMinutes,
    DateTimeOffset ResetsAt);
