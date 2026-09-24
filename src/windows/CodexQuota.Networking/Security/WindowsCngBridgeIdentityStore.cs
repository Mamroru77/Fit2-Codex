using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexQuota.Networking.Security;

/// <summary>
/// Persists the Bridge identity so that it survives restarts, DHCP changes and leaf renewal.
/// </summary>
/// <remarks>
/// <para>
/// The RSA key lives in the current Windows user's key store under a named CNG key, and only the
/// public certificate plus the key's name are written to the Bridge folder. Nothing in the
/// persisted file is secret: possession of the file alone is not enough to impersonate the Bridge.
/// </para>
/// <para>
/// This is what makes the identity the trust anchor rather than the certificate: the leaf may be
/// reissued freely, the identity may not change without every paired client refusing to connect.
/// </para>
/// </remarks>
public sealed class WindowsCngBridgeIdentityStore : IBridgeIdentityStore
{
    /// <summary>File the non-secret identity metadata is written to.</summary>
    public const string MetadataFileName = "bridge-identity.json";

    /// <summary>Key size of the Bridge identity. 2048 bits is the current floor for RSA.</summary>
    private const int KeySizeBits = 2048;

    /// <summary>How long the identity certificate is valid for.</summary>
    private const int IdentityValidityYears = 10;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _storageDirectory;
    private readonly string _keyNamePrefix;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private BridgeIdentity? _identity;

    /// <param name="storageDirectory">Folder the non-secret identity metadata is written to.</param>
    /// <param name="keyNamePrefix">
    /// Prefix of the CNG key name. Overridable so tests can use a throwaway key store.
    /// </param>
    public WindowsCngBridgeIdentityStore(string storageDirectory, string keyNamePrefix = "CodexQuotaBridge")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyNamePrefix);

        _storageDirectory = storageDirectory;
        _keyNamePrefix = keyNamePrefix;
    }

    /// <summary>Path of the persisted, non-secret identity metadata.</summary>
    public string MetadataPath => Path.Combine(_storageDirectory, MetadataFileName);

    public async Task<BridgeIdentity> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The Bridge identity is stored in the Windows key store, which requires Windows.");
        }

        if (_identity is { } existing)
        {
            return existing;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _identity ??= await LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
            return _identity;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<BridgeIdentity> LoadOrCreateAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_storageDirectory);

        if (File.Exists(MetadataPath))
        {
            var persisted = await ReadMetadataAsync(cancellationToken).ConfigureAwait(false);

            if (persisted is not null
                && TryOpenKey(persisted.KeyName, out var existingKey)
                && TryBind(persisted.CertificatePem, existingKey, out var rebound))
            {
                return new BridgeIdentity(persisted.BridgeId, rebound);
            }

            // The metadata exists but the key does not: the key store was cleared. Falling back to
            // a fresh identity is the only option, and it is what forces clients to re-pair.
        }

        return await CreateAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<BridgeIdentity> CreateAsync(CancellationToken cancellationToken)
    {
        var bridgeId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var keyName = $"{_keyNamePrefix}-{bridgeId}";

        using var key = CreateKey(keyName);
        using var rsa = new RSACng(key);

        var request = new CertificateRequest(
            new X500DistinguishedName($"CN=CodexQuota Bridge {bridgeId}"),
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        // The identity is the issuer of every leaf, so it has to be a CA.
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature,
                true));

        var now = DateTimeOffset.UtcNow;

        // CreateSelfSigned already binds the request's key — the CNG-backed one — to the result, so
        // no second CopyWithPrivateKey is needed (and it would throw: the certificate already has a
        // private key). Signing therefore happens inside the Windows key store.
        var certificate = request.CreateSelfSigned(now.AddHours(-1), now.AddYears(IdentityValidityYears));

        var metadata = new IdentityMetadata(
            bridgeId,
            keyName,
            new string(PemEncoding.Write("CERTIFICATE", certificate.Export(X509ContentType.Cert))));

        await WriteMetadataAsync(metadata, cancellationToken).ConfigureAwait(false);

        return new BridgeIdentity(bridgeId, certificate);
    }

    private static CngKey CreateKey(string keyName)
    {
        var parameters = new CngKeyCreationParameters
        {
            // The key is never exported: signing happens inside the provider.
            ExportPolicy = CngExportPolicies.None,
            KeyUsage = CngKeyUsages.AllUsages,
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
            KeyCreationOptions = CngKeyCreationOptions.None,
        };

        parameters.Parameters.Add(
            new CngProperty("Length", BitConverter.GetBytes(KeySizeBits), CngPropertyOptions.None));

        return CngKey.Create(CngAlgorithm.Rsa, keyName, parameters);
    }

    private static bool TryOpenKey(string keyName, out CngKey key)
    {
        try
        {
            key = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider);
            return true;
        }
        catch (CryptographicException)
        {
            key = null!;
            return false;
        }
    }

    private static bool TryBind(string certificatePem, CngKey key, out X509Certificate2 certificate)
    {
        try
        {
            var publicOnly = X509Certificate2.CreateFromPem(certificatePem);
            using var rsa = new RSACng(key);

            certificate = publicOnly.CopyWithPrivateKey(rsa);
            return true;
        }
        catch (CryptographicException)
        {
            certificate = null!;
            return false;
        }
    }

    private async Task<IdentityMetadata?> ReadMetadataAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(MetadataPath);
            return await JsonSerializer
                .DeserializeAsync<IdentityMetadata>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return null;
        }
    }

    private async Task WriteMetadataAsync(IdentityMetadata metadata, CancellationToken cancellationToken)
    {
        // Write-then-replace, so a crash mid-write cannot leave a half-written identity behind.
        var temporaryPath = MetadataPath + ".tmp";

        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer
                .SerializeAsync(stream, metadata, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temporaryPath, MetadataPath, overwrite: true);
    }

    /// <summary>The persisted, non-secret identity metadata.</summary>
    /// <param name="BridgeId">Stable Bridge identifier.</param>
    /// <param name="KeyName">Name of the CNG key holding the private half.</param>
    /// <param name="CertificatePem">The public identity certificate.</param>
    public sealed record IdentityMetadata(string BridgeId, string KeyName, string CertificatePem);
}
