using System.Text.Json;

namespace CodexQuota.Codex.Protocol;

/// <summary>
/// A server-initiated JSON-RPC notification: it carries a <c>method</c> and no <c>id</c>.
/// </summary>
/// <param name="Method">The notification method, for example <c>account/rateLimits/updated</c>.</param>
/// <param name="Params">The notification parameters, when the message carries any.</param>
public sealed record CodexNotification(string Method, JsonElement? Params);
