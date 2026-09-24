namespace CodexQuota.Networking.Pairing;

/// <summary>
/// URL-safe base64 without padding, used for pairing ids and device credentials.
/// </summary>
/// <remarks>
/// The unpadded form is used everywhere because these values travel inside URLs, QR payloads and
/// <c>Authorization</c> headers, where <c>+</c>, <c>/</c> and <c>=</c> are all awkward or invalid.
/// </remarks>
public static class Base64Url
{
    /// <summary>Encodes bytes without padding, using the URL-safe alphabet.</summary>
    public static string Encode(ReadOnlySpan<byte> value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Decodes an unpadded URL-safe base64 string. Returns <c>false</c> for anything that is not
    /// well-formed, because a malformed credential must fail closed rather than throw.
    /// </summary>
    public static bool TryDecode(string? value, out byte[] bytes)
    {
        bytes = [];

        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var padded = value.Replace('-', '+').Replace('_', '/');

        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            0 => padded,
            _ => string.Empty,
        };

        if (padded.Length == 0)
        {
            return false;
        }

        // Convert.TryFromBase64String rejects the characters that would otherwise slip through.
        var buffer = new byte[(padded.Length / 4) * 3];

        return Convert.TryFromBase64String(padded, buffer, out var written)
               && Copy(buffer, written, out bytes);
    }

    private static bool Copy(byte[] buffer, int written, out byte[] bytes)
    {
        bytes = buffer[..written];
        return true;
    }
}
