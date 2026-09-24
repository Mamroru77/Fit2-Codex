namespace CodexQuota.Networking.Contracts.V1;

/// <summary>One point of the 24-hour history.</summary>
public sealed record HistoryPointResponse(
    DateTimeOffset Timestamp,
    double ShortWindowRemainingPercent,
    double WeeklyRemainingPercent);

/// <summary>
/// <c>GET /api/v1/history?hours=24</c>. Gaps in the series are real gaps: the client must render
/// them as gaps rather than interpolating continuous usage.
/// </summary>
public sealed record HistoryResponse(int Hours, IReadOnlyList<HistoryPointResponse> Points);
