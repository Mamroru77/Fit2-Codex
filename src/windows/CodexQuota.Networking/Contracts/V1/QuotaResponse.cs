namespace CodexQuota.Networking.Contracts.V1;

/// <summary>
/// One quota window on the wire. The primary UI semantic is <see cref="RemainingPercent"/>;
/// <see cref="UsedPercent"/> is carried so a client can show it as secondary information without
/// having to derive it.
/// </summary>
public sealed record QuotaWindowResponse(
    double UsedPercent,
    double RemainingPercent,
    int WindowMinutes,
    DateTimeOffset ResetsAt);

/// <summary>The two logical windows the Bridge identifies from explicit source metadata.</summary>
public sealed record QuotaWindowsResponse(QuotaWindowResponse ShortWindow, QuotaWindowResponse Weekly);

/// <summary>
/// <c>GET /api/v1/quota</c> and the WebSocket <c>quota.updated</c> payload: the complete normalised
/// snapshot, never a delta.
/// </summary>
public sealed record QuotaResponse(
    int SchemaVersion,
    DateTimeOffset GeneratedAt,
    string Source,
    string Status,
    DateTimeOffset? LastSuccessfulSyncAt,
    QuotaWindowsResponse Windows);
