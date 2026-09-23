using CodexQuota.Core.Logging;
using Xunit;

namespace CodexQuota.Core.Tests;

/// <summary>
/// Anything that reaches disk must be redacted. These are the samples named in the plan; each
/// one asserts its secret is gone, and the combined case asserts they are all gone at once.
/// </summary>
public class SensitiveDataRedactorTests
{
    [Fact]
    public void RedactsBearerAuthorizationValue()
    {
        var redacted = SensitiveDataRedactor.Redact("Authorization: Bearer abcdef123");

        Assert.DoesNotContain("abcdef123", redacted);
        Assert.Contains("Authorization", redacted);
    }

    [Fact]
    public void RedactsAccessTokenJsonProperty()
    {
        var redacted = SensitiveDataRedactor.Redact("{\"accessToken\":\"secret\"}");

        Assert.DoesNotContain("secret", redacted);
        Assert.Contains("accessToken", redacted);
    }

    [Fact]
    public void RedactsRefreshTokenJsonProperty()
    {
        var redacted = SensitiveDataRedactor.Redact("{\"refresh_token\":\"secret2\"}");

        Assert.DoesNotContain("secret2", redacted);
        Assert.Contains("refresh_token", redacted);
    }

    [Fact]
    public void RedactsCookieHeaderValue()
    {
        var redacted = SensitiveDataRedactor.Redact("Cookie: session=secret3");

        Assert.DoesNotContain("secret3", redacted);
    }

    [Fact]
    public void RedactsPemPrivateKeyBlock()
    {
        var pem = string.Join(
            Environment.NewLine,
            "-----BEGIN PRIVATE KEY-----",
            "secret",
            "-----END PRIVATE KEY-----");

        var redacted = SensitiveDataRedactor.Redact(pem);

        Assert.DoesNotContain("secret", redacted);

        // The BEGIN/END markers go too: the leak scan treats a surviving marker as a leak.
        Assert.DoesNotContain("PRIVATE KEY", redacted);
    }

    [Fact]
    public void RedactsPairingSecretKeyValue()
    {
        var redacted = SensitiveDataRedactor.Redact("pairingSecret=secret4");

        Assert.DoesNotContain("secret4", redacted);
    }

    [Fact]
    public void KeepsSafeDiagnosticFields()
    {
        var redacted = SensitiveDataRedactor.Redact("component=Codex phase=Ready");

        Assert.Contains("component=Codex", redacted);
        Assert.Contains("phase=Ready", redacted);
    }

    [Fact]
    public void RedactsEverySecretInOneCombinedLine()
    {
        var line = string.Join(
            Environment.NewLine,
            "Authorization: Bearer abcdef123",
            "{\"accessToken\":\"secret\"}",
            "{\"refresh_token\":\"secret2\"}",
            "Cookie: session=secret3",
            "-----BEGIN PRIVATE KEY-----",
            "secret",
            "-----END PRIVATE KEY-----",
            "pairingSecret=secret4",
            "component=Codex");

        var redacted = SensitiveDataRedactor.Redact(line);

        Assert.DoesNotContain("abcdef123", redacted);
        Assert.DoesNotContain("secret", redacted);
        Assert.DoesNotContain("secret2", redacted);
        Assert.DoesNotContain("secret3", redacted);
        Assert.DoesNotContain("secret4", redacted);
        Assert.DoesNotContain("PRIVATE KEY", redacted);
        Assert.Contains("component=Codex", redacted);
    }

    [Fact]
    public void RedactNeverThrowsOnUnusualInput()
    {
        // Redaction runs on the logging path, so it must degrade instead of raising.
        Assert.Equal(string.Empty, SensitiveDataRedactor.Redact(null!));
        Assert.Equal(string.Empty, SensitiveDataRedactor.Redact(string.Empty));
        Assert.Equal("   ", SensitiveDataRedactor.Redact("   "));
    }
}
