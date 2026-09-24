using System.Text.Json.Nodes;

namespace CodexQuota.Networking.Contracts.V1;

/// <summary>
/// Explicit structural validation of a v1 document.
/// </summary>
/// <remarks>
/// Deserialization alone is not enough: a missing <c>remainingPercent</c> would simply bind to
/// <c>0</c>, and a client that trusted it would render "no quota left" for a payload that never
/// said so. The validator therefore answers one question — is every required member present with a
/// usable value — and reports <see cref="ApiErrorCodes.DataProtocolError"/> when it is not.
/// </remarks>
public static class V1ContractValidator
{
    /// <summary>The code returned for any contract violation.</summary>
    public const string DataProtocolError = ApiErrorCodes.DataProtocolError;

    /// <summary>The only schema version this validator understands.</summary>
    public const int SupportedSchemaVersion = 1;

    private static readonly HashSet<string> KnownStatuses = new(StringComparer.Ordinal)
    {
        "online",
        "stale",
        "unavailable",
        "auth_required",
        "source_error",
        "source_schema_unsupported",
    };

    /// <summary>Returns <c>null</c> when <paramref name="quota"/> is a valid quota document.</summary>
    public static string? ValidateQuota(JsonNode? quota)
    {
        if (quota is not JsonObject root)
        {
            return DataProtocolError;
        }

        if (ReadInt(root, "schemaVersion") is not { } schemaVersion || schemaVersion != SupportedSchemaVersion)
        {
            return DataProtocolError;
        }

        if (!HasTimestamp(root, "generatedAt")
            || !HasNonEmptyString(root, "source")
            || ReadString(root, "status") is not { } status
            || !KnownStatuses.Contains(status))
        {
            return DataProtocolError;
        }

        // lastSuccessfulSyncAt is nullable by design — a Bridge that has never synced reports null.
        if (root.TryGetPropertyValue("lastSuccessfulSyncAt", out var lastSync)
            && lastSync is not null
            && !IsTimestamp(lastSync))
        {
            return DataProtocolError;
        }

        if (root["windows"] is not JsonObject windows)
        {
            return DataProtocolError;
        }

        return ValidateWindow(windows, "shortWindow") ?? ValidateWindow(windows, "weekly");
    }

    /// <summary>Returns <c>null</c> when <paramref name="history"/> is a valid history document.</summary>
    public static string? ValidateHistory(JsonNode? history)
    {
        if (history is not JsonObject root || ReadInt(root, "hours") is null)
        {
            return DataProtocolError;
        }

        if (root["points"] is not JsonArray points)
        {
            return DataProtocolError;
        }

        foreach (var point in points)
        {
            if (point is not JsonObject item
                || !HasTimestamp(item, "timestamp")
                || !IsPercent(item, "shortWindowRemainingPercent")
                || !IsPercent(item, "weeklyRemainingPercent"))
            {
                return DataProtocolError;
            }
        }

        return null;
    }

    /// <summary>Returns <c>null</c> when <paramref name="events"/> is a valid events document.</summary>
    public static string? ValidateEvents(JsonNode? events)
    {
        if (events is not JsonObject root || ReadInt(root, "hours") is null)
        {
            return DataProtocolError;
        }

        if (root["events"] is not JsonArray items)
        {
            return DataProtocolError;
        }

        foreach (var item in items)
        {
            if (item is not JsonObject entry
                || !HasNonEmptyString(entry, "type")
                || !HasTimestamp(entry, "occurredAt"))
            {
                return DataProtocolError;
            }
        }

        return null;
    }

    private static string? ValidateWindow(JsonObject windows, string name)
    {
        if (windows[name] is not JsonObject window)
        {
            return DataProtocolError;
        }

        if (!IsPercent(window, "usedPercent")
            || !IsPercent(window, "remainingPercent")
            || ReadInt(window, "windowMinutes") is not { } minutes
            || minutes <= 0
            || !HasTimestamp(window, "resetsAt"))
        {
            return DataProtocolError;
        }

        return null;
    }

    private static bool IsPercent(JsonObject owner, string name)
        => ReadDouble(owner, name) is { } value && value >= 0d && value <= 100d;

    private static bool HasNonEmptyString(JsonObject owner, string name)
        => ReadString(owner, name) is { Length: > 0 };

    private static bool HasTimestamp(JsonObject owner, string name)
        => owner.TryGetPropertyValue(name, out var value) && value is not null && IsTimestamp(value);

    private static bool IsTimestamp(JsonNode value)
        => value.GetValueKind() == System.Text.Json.JsonValueKind.String
           && DateTimeOffset.TryParse(
               value.GetValue<string>(),
               System.Globalization.CultureInfo.InvariantCulture,
               System.Globalization.DateTimeStyles.RoundtripKind,
               out _);

    private static int? ReadInt(JsonObject owner, string name)
        => owner.TryGetPropertyValue(name, out var value)
           && value is not null
           && value.GetValueKind() == System.Text.Json.JsonValueKind.Number
           && value.AsValue().TryGetValue<int>(out var parsed)
            ? parsed
            : null;

    private static double? ReadDouble(JsonObject owner, string name)
        => owner.TryGetPropertyValue(name, out var value)
           && value is not null
           && value.GetValueKind() == System.Text.Json.JsonValueKind.Number
           && value.AsValue().TryGetValue<double>(out var parsed)
            ? parsed
            : null;

    private static string? ReadString(JsonObject owner, string name)
        => owner.TryGetPropertyValue(name, out var value)
           && value is not null
           && value.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? value.GetValue<string>()
            : null;
}
