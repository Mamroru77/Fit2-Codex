using System.Text.RegularExpressions;

namespace CodexQuota.Core.Logging;

/// <summary>
/// Removes credentials from any text before it is written to disk.
/// </summary>
/// <remarks>
/// This is the last gate before persistent output. It runs on the fully formatted log line,
/// so it covers both the message and the exception text. It is deliberately fail-open in one
/// direction only: it never throws, and it is allowed to over-redact. Under-redacting a
/// token is the only unacceptable outcome.
/// </remarks>
public static class SensitiveDataRedactor
{
    /// <summary>Replacement used for every removed value.</summary>
    public const string Placeholder = "[REDACTED]";

    /// <summary>
    /// Replacement for a whole PEM block. The <c>BEGIN/END PRIVATE KEY</c> markers are removed
    /// as well, because the leak scan treats any surviving marker as evidence of a leak.
    /// </summary>
    private const string PemPlaceholder = "[REDACTED-PRIVATE-KEY]";

    /// <summary>Whole PEM block, including the base64 body between the delimiters.</summary>
    private static readonly Regex PemPrivateKey = new(
        @"-----BEGIN(?:\s+[A-Z]+)*\s+PRIVATE KEY-----[\s\S]*?-----END(?:\s+[A-Z]+)*\s+PRIVATE KEY-----",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary><c>Authorization: Bearer &lt;token&gt;</c> and the other HTTP auth schemes.</summary>
    private static readonly Regex AuthorizationScheme = new(
        @"\b(?<scheme>bearer|basic|digest|token)\s+(?<value>[A-Za-z0-9._~+/=-]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary><c>Cookie:</c> / <c>Set-Cookie:</c> headers, whose values are credentials wholesale.</summary>
    private static readonly Regex CookieHeader = new(
        @"\b(?<name>set-cookie|cookie)\s*:\s*(?<value>[^\r\n]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>JSON property whose <em>name</em> marks the value as a credential.</summary>
    private static readonly Regex JsonSecretProperty = new(
        @"""(?<key>[^""]*(?:token|secret|password|passwd|api[-_]?key|credential|authorization|cookie|session)[^""]*)""\s*:\s*""(?<value>[^""]*)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Loose <c>key=value</c> / <c>key: value</c> pairs, for log lines that are not JSON.
    /// The lookbehind keeps it away from JSON keys, which the rule above already handled.
    /// The lookaheads keep it from re-redacting its own placeholder.
    /// </summary>
    private static readonly Regex SecretKeyValue = new(
        @"(?<![""'\w.-])(?<key>[A-Za-z0-9._-]*(?:secret|token|password|passwd|api[-_]?key|credential)[A-Za-z0-9._-]*)\s*[:=]\s*(?:""(?<dq>(?!\[REDACTED\])[^""]*)""|'(?<sq>(?!\[REDACTED\])[^']*)'|(?<bare>(?!\[REDACTED\])[^\s,;&}""]+))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Returns <paramref name="value"/> with every recognised credential replaced.</summary>
    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var redacted = PemPrivateKey.Replace(value, PemPlaceholder);
        redacted = AuthorizationScheme.Replace(redacted, match => $"{match.Groups["scheme"].Value} {Placeholder}");
        redacted = CookieHeader.Replace(redacted, match => $"{match.Groups["name"].Value}: {Placeholder}");
        redacted = JsonSecretProperty.Replace(redacted, match => $"\"{match.Groups["key"].Value}\": \"{Placeholder}\"");
        redacted = SecretKeyValue.Replace(redacted, match => $"{match.Groups["key"].Value}={Placeholder}");

        return redacted;
    }
}
