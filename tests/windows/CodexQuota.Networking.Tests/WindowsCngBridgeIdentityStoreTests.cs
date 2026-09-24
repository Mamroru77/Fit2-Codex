using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using CodexQuota.Networking.Security;

namespace CodexQuota.Networking.Tests;

/// <summary>
/// Exercises the real Windows key store, because the property that matters — the private key never
/// leaves it — cannot be observed through a fake.
/// </summary>
public class WindowsCngBridgeIdentityStoreTests
{
    [Fact]
    public async Task TheIdentitySurvivesASecondProcessStart()
    {
        using var workspace = new IdentityWorkspace();

        var first = new WindowsCngBridgeIdentityStore(workspace.Path, workspace.KeyPrefix);
        using var firstIdentity = await first.GetOrCreateAsync(CancellationToken.None);

        // A second store instance is what a Bridge restart looks like.
        var second = new WindowsCngBridgeIdentityStore(workspace.Path, workspace.KeyPrefix);
        using var secondIdentity = await second.GetOrCreateAsync(CancellationToken.None);

        Assert.Equal(firstIdentity.BridgeId, secondIdentity.BridgeId);
        Assert.Equal(firstIdentity.SpkiSha256, secondIdentity.SpkiSha256);
        Assert.Equal(firstIdentity.VerificationCode, secondIdentity.VerificationCode);
        Assert.Equal(firstIdentity.DnsName, secondIdentity.DnsName);
    }

    [Fact]
    public async Task TheReloadedIdentityCanStillSignALeaf()
    {
        using var workspace = new IdentityWorkspace();

        var first = new WindowsCngBridgeIdentityStore(workspace.Path, workspace.KeyPrefix);
        using var created = await first.GetOrCreateAsync(CancellationToken.None);

        var second = new WindowsCngBridgeIdentityStore(workspace.Path, workspace.KeyPrefix);
        using var reloaded = await second.GetOrCreateAsync(CancellationToken.None);

        // Signing is the only thing that proves the private key was rebound from the key store
        // rather than lost with the process.
        using var leaf = LeafCertificateFactory.CreateServerCertificate(
            reloaded,
            [IPAddress.Parse("192.168.1.23")],
            ["localhost", reloaded.DnsName],
            DateTimeOffset.UtcNow);

        Assert.Equal(reloaded.Certificate.Subject, leaf.Issuer);
        Assert.True(leaf.HasPrivateKey);
    }

    [Fact]
    public async Task ThePersistedMetadataContainsNoPrivateKey()
    {
        using var workspace = new IdentityWorkspace();

        var store = new WindowsCngBridgeIdentityStore(workspace.Path, workspace.KeyPrefix);
        using var identity = await store.GetOrCreateAsync(CancellationToken.None);

        var persisted = await File.ReadAllTextAsync(store.MetadataPath);

        Assert.Contains("BEGIN CERTIFICATE", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("RSA PRIVATE", persisted, StringComparison.Ordinal);

        // And what the store hands out for persistence is public-only.
        Assert.False(identity.PublicCertificate.HasPrivateKey);
    }

    [Fact]
    public async Task ConcurrentFirstUseProducesExactlyOneIdentity()
    {
        using var workspace = new IdentityWorkspace();

        var store = new WindowsCngBridgeIdentityStore(workspace.Path, workspace.KeyPrefix);

        var identities = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => store.GetOrCreateAsync(CancellationToken.None)));

        Assert.Single(identities.Select(identity => identity.SpkiSha256).Distinct(StringComparer.Ordinal));
    }
}

/// <summary>
/// A throwaway identity folder plus the CNG key name it created, so the test can remove both.
/// </summary>
internal sealed class IdentityWorkspace : IDisposable
{
    internal IdentityWorkspace()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"codexquota-identity-{Guid.NewGuid():N}");
        KeyPrefix = $"CodexQuotaTest-{Guid.NewGuid():N}";

        Directory.CreateDirectory(Path);
    }

    internal string Path { get; }

    internal string KeyPrefix { get; }

    public void Dispose()
    {
        DeleteCreatedKey();
        DeleteFolder();
    }

    private void DeleteCreatedKey()
    {
        var metadataPath = System.IO.Path.Combine(Path, WindowsCngBridgeIdentityStore.MetadataFileName);

        if (!File.Exists(metadataPath))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));

            if (!document.RootElement.TryGetProperty("keyName", out var keyNameElement)
                || keyNameElement.GetString() is not { } keyName)
            {
                return;
            }

            using var key = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider);
            key.Delete();
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or IOException)
        {
            // Best effort: a leftover test key in the user key store is harmless.
        }
    }

    private void DeleteFolder()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
