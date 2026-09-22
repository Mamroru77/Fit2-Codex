using System.Text.Json;

namespace CodexQuota.Codex.Account;

/// <summary>
/// Codex account authentication state as understood by the Bridge. Authentication is only ever
/// claimed when the App Server reports an explicit signal for it, so an unrecognised payload
/// degrades to "authentication required" rather than to a false positive.
/// </summary>
/// <param name="Authenticated">Whether Codex reports an authenticated account.</param>
/// <param name="AuthMode">The reported authentication mode, for example <c>chatgpt</c>.</param>
/// <param name="Email">The signed-in account email, when the App Server reports one.</param>
public sealed record AccountState(bool Authenticated, string? AuthMode, string? Email)
{
    /// <summary>
    /// Reads account state from an <c>account/read</c> result or an <c>account/updated</c>
    /// notification payload. Both carry the same fields, either at the top level or nested under
    /// <c>account</c>; anything else is treated as unauthenticated.
    /// </summary>
    public static AccountState FromPayload(JsonElement payload)
    {
        var authMode = ReadString(payload, "authMode") ?? ReadNestedString(payload, "account", "authMode");
        var email = ReadString(payload, "email") ?? ReadNestedString(payload, "account", "email");

        var authenticated = !string.IsNullOrWhiteSpace(authMode) || ReadTrue(payload, "authenticated");

        return new AccountState(authenticated, authMode, email);
    }

    private static string? ReadString(JsonElement payload, string name)
        => payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text
                ? text
                : null;

    private static string? ReadNestedString(JsonElement payload, string parent, string name)
        => payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(parent, out var nested)
            && nested.ValueKind == JsonValueKind.Object
                ? ReadString(nested, name)
                : null;

    private static bool ReadTrue(JsonElement payload, string name)
        => payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.True;
}
