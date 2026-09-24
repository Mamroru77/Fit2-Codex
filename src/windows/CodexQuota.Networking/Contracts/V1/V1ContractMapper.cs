using CodexQuota.Core.Quota;

namespace CodexQuota.Networking.Contracts.V1;

/// <summary>
/// Maps the Bridge's domain model onto the v1 wire contract.
/// </summary>
/// <remarks>
/// The mapping lives here rather than on the domain records on purpose: the domain must not carry
/// wire-format attributes, and the wire contract must be free to evolve within v1 without touching
/// the domain.
/// </remarks>
public static class V1ContractMapper
{
    /// <summary>The API path version this contract describes.</summary>
    public const string ApiVersion = "v1";

    /// <summary>The <c>source</c> value the Bridge reports for Codex App Server data.</summary>
    public const string CodexAppServerSource = "codex_app_server";

    /// <summary>Maps a published snapshot onto the quota contract.</summary>
    public static QuotaResponse ToQuotaResponse(QuotaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new QuotaResponse(
            snapshot.SchemaVersion,
            snapshot.GeneratedAt,
            snapshot.Source,
            ToWireStatus(snapshot.Status),
            snapshot.LastSuccessfulSyncAt,
            new QuotaWindowsResponse(
                ToWindowResponse(snapshot.ShortWindow),
                ToWindowResponse(snapshot.Weekly)));
    }

    /// <summary>Maps persisted history points onto the history contract.</summary>
    public static HistoryResponse ToHistoryResponse(int hours, IReadOnlyList<HistoryPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        return new HistoryResponse(
            hours,
            points
                .Select(point => new HistoryPointResponse(
                    point.Timestamp,
                    point.ShortWindowRemainingPercent,
                    point.WeeklyRemainingPercent))
                .ToArray());
    }

    /// <summary>Maps persisted events onto the events contract.</summary>
    public static EventsResponse ToEventsResponse(int hours, IReadOnlyList<QuotaEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        return new EventsResponse(
            hours,
            events
                .Select(quotaEvent => new QuotaEventResponse(
                    ToWireEventType(quotaEvent.Type),
                    quotaEvent.OccurredAt,
                    quotaEvent.Detail))
                .ToArray());
    }

    /// <summary>The wire name of a source status.</summary>
    public static string ToWireStatus(QuotaSourceStatus status) => status switch
    {
        QuotaSourceStatus.Online => "online",
        QuotaSourceStatus.Stale => "stale",
        QuotaSourceStatus.Unavailable => "unavailable",
        QuotaSourceStatus.AuthRequired => "auth_required",
        QuotaSourceStatus.SourceError => "source_error",
        QuotaSourceStatus.SourceSchemaUnsupported => "source_schema_unsupported",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown quota source status."),
    };

    /// <summary>The wire name of an event type.</summary>
    public static string ToWireEventType(QuotaEventType type) => type switch
    {
        QuotaEventType.QuotaChanged => "quota_changed",
        QuotaEventType.WindowReset => "window_reset",
        QuotaEventType.BridgeStarted => "bridge_started",
        QuotaEventType.BridgeStopped => "bridge_stopped",
        QuotaEventType.CodexConnected => "codex_connected",
        QuotaEventType.CodexDisconnected => "codex_disconnected",
        QuotaEventType.AuthRequired => "auth_required",
        QuotaEventType.SourceError => "source_error",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown quota event type."),
    };

    private static QuotaWindowResponse ToWindowResponse(QuotaWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        return new QuotaWindowResponse(
            window.UsedPercent,
            window.RemainingPercent,
            window.WindowMinutes,
            window.ResetsAt);
    }
}
