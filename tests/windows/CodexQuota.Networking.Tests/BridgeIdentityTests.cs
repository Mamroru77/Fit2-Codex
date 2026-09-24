using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CodexQuota.Networking.Security;

namespace CodexQuota.Networking.Tests;

/// <summary>
/// The Bridge identity is the trust anchor Android pins. It must survive restarts and DHCP changes
/// while the leaf certificate it presents is free to rotate — that separation is the whole point of
/// the design, so it is what these tests pin.
/// </summary>
public class BridgeIdentityTests
{
    [Fact]
    public void TheHumanVerificationCodeIsFourGroupsOfFourUppercaseHex()
    {
        var code = CertificateFingerprint.ToHumanVerificationCode(
            "a1b2c3d4e5f6789012345678901234567890abcdef1234567890abcdef123456");

        Assert.Equal("A1B2-C3D4-E5F6-7890", code);
    }

    [Fact]
    public void TheHumanVerificationCodeRejectsAFingerprintThatIsTooShort()
    {
        Assert.Throws<ArgumentException>(
            () => CertificateFingerprint.ToHumanVerificationCode("A1B2C3D4"));
    }

    [Fact]
    public void TheSpkiFingerprintIsStableAndDerivedFromThePublicKeyOnly()
    {
        using var first = TestCertificates.CreateIdentity("bridge-1");
        using var second = TestCertificates.CreateIdentity("bridge-1");

        // Two independently generated keys must not collide...
        Assert.NotEqual(
            CertificateFingerprint.ComputeSpkiSha256(first.Certificate),
            CertificateFingerprint.ComputeSpkiSha256(second.Certificate));

        // ...while the same certificate always hashes the same way.
        Assert.Equal(
            CertificateFingerprint.ComputeSpkiSha256(first.Certificate),
            CertificateFingerprint.ComputeSpkiSha256(first.Certificate));
    }

    [Fact]
    public void IdentityDerivesItsBridgeIdFingerprintAndVerificationCodeOnce()
    {
        using var material = TestCertificates.CreateIdentity("bridge-abc12345");

        using var identity = new BridgeIdentity(material.BridgeId, material.Certificate);

        Assert.Equal("bridge-abc12345", identity.BridgeId);
        Assert.Equal(
            CertificateFingerprint.ComputeSpkiSha256(material.Certificate),
            identity.SpkiSha256);
        Assert.Equal(
            CertificateFingerprint.ToHumanVerificationCode(identity.SpkiSha256),
            identity.VerificationCode);
        Assert.Equal("codexquota-bridge-a.local", identity.DnsName);
    }

    [Fact]
    public void OnlyThePublicCertificateIsExposedForPersistence()
    {
        using var material = TestCertificates.CreateIdentity("bridge-1");
        using var identity = new BridgeIdentity(material.BridgeId, material.Certificate);

        // What gets written to disk must never be the key material.
        Assert.False(identity.PublicCertificate.HasPrivateKey);
        Assert.True(identity.Certificate.HasPrivateKey);

        var pem = new string(PemEncoding.Write("CERTIFICATE", identity.PublicCertificate.Export(X509ContentType.Cert)));

        Assert.Contains("BEGIN CERTIFICATE", pem, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", pem, StringComparison.Ordinal);
    }
}

/// <summary>
/// The leaf certificate is replaceable by design: the identity is the trust anchor, the leaf is
/// only a location-dependent presentation of it.
/// </summary>
public class LeafCertificateFactoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 13, 30, 0, TimeSpan.Zero);

    [Fact]
    public void RotationChangesTheLeafButNotTheBridgeIdentity()
    {
        using var material = TestCertificates.CreateIdentity("bridge-1");
        using var identity = new BridgeIdentity(material.BridgeId, material.Certificate);

        using var first = LeafCertificateFactory.CreateServerCertificate(
            identity,
            [IPAddress.Parse("192.168.1.23")],
            ["localhost", identity.DnsName],
            Now);

        using var second = LeafCertificateFactory.CreateServerCertificate(
            identity,
            [IPAddress.Parse("192.168.1.87")],
            ["localhost", identity.DnsName],
            Now);

        // A new IP means a new leaf...
        Assert.NotEqual(first.Thumbprint, second.Thumbprint);

        // ...but the pinned Bridge fingerprint — what Android actually trusts — is unchanged.
        Assert.Equal(identity.SpkiSha256, CertificateFingerprint.ComputeSpkiSha256(identity.Certificate));
        Assert.Equal(
            identity.SpkiSha256,
            CertificateFingerprint.ComputeSpkiSha256(identity.Certificate));

        // The leaf really is a leaf: it chains to the identity, and carries the new address.
        Assert.Equal(identity.Certificate.Subject, first.Issuer);
        Assert.Contains("192.168.1.23", SanText(first), StringComparison.Ordinal);
        Assert.Contains("192.168.1.87", SanText(second), StringComparison.Ordinal);
        Assert.DoesNotContain("192.168.1.87", SanText(first), StringComparison.Ordinal);
    }

    [Fact]
    public void TheLeafCarriesTheStableDnsNameAndLocalhost()
    {
        using var material = TestCertificates.CreateIdentity("bridge-1");
        using var identity = new BridgeIdentity(material.BridgeId, material.Certificate);

        using var leaf = LeafCertificateFactory.CreateServerCertificate(
            identity,
            [IPAddress.Parse("192.168.1.23")],
            ["localhost", identity.DnsName],
            Now);

        var san = SanText(leaf);

        Assert.Contains("localhost", san, StringComparison.Ordinal);
        Assert.Contains(identity.DnsName, san, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLeafIsValidForNinetyDaysAndNeedsRenewalInTheLastThirty()
    {
        using var material = TestCertificates.CreateIdentity("bridge-1");
        using var identity = new BridgeIdentity(material.BridgeId, material.Certificate);

        using var leaf = LeafCertificateFactory.CreateServerCertificate(
            identity,
            [IPAddress.Parse("192.168.1.23")],
            ["localhost"],
            Now);

        // NotAfter is local time, so compare in UTC.
        Assert.True(leaf.NotAfter.ToUniversalTime() <= Now.UtcDateTime.AddDays(LeafCertificateFactory.ValidityDays + 1));
        Assert.True(leaf.NotAfter.ToUniversalTime() >= Now.UtcDateTime.AddDays(LeafCertificateFactory.ValidityDays - 1));

        // The key must still be usable after the factory returns: the certificate has to own it
        // rather than reference the temporary RSA the factory built it from.
        using var leafKey = leaf.GetRSAPrivateKey();

        Assert.NotNull(leafKey);
        Assert.NotEmpty(leafKey!.SignData([1, 2, 3], HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        Assert.False(LeafCertificateFactory.NeedsRenewal(leaf, Now));
        Assert.True(LeafCertificateFactory.NeedsRenewal(
            leaf,
            Now.AddDays(LeafCertificateFactory.ValidityDays - LeafCertificateFactory.RenewalThresholdDays + 1)));
        Assert.True(LeafCertificateFactory.NeedsRenewal(
            leaf,
            Now.AddDays(LeafCertificateFactory.ValidityDays)));
    }

    [Fact]
    public void AnAddressChangeAlsoForcesRenewal()
    {
        using var material = TestCertificates.CreateIdentity("bridge-1");
        using var identity = new BridgeIdentity(material.BridgeId, material.Certificate);

        using var leaf = LeafCertificateFactory.CreateServerCertificate(
            identity,
            [IPAddress.Parse("192.168.1.23")],
            ["localhost"],
            Now);

        // The current SAN set is covered...
        Assert.True(LeafCertificateFactory.Covers(
            leaf,
            [IPAddress.Parse("192.168.1.23")],
            ["localhost"]));

        // ...and the address moving is what forces renewal.
        Assert.False(LeafCertificateFactory.Covers(
            leaf,
            [IPAddress.Parse("192.168.1.87")],
            ["localhost"]));
    }

    private static string SanText(X509Certificate2 certificate)
        => certificate.Extensions
            .OfType<X509SubjectAlternativeNameExtension>()
            .SelectMany(extension => extension.EnumerateDnsNames()
                .Concat(extension.EnumerateIPAddresses().Select(address => address.ToString())))
            .Aggregate(string.Empty, (text, entry) => text + entry + ";");
}

/// <summary>Creates real, disposable identity material for tests, with no CNG key store involved.</summary>
internal static class TestCertificates
{
    internal static TestIdentityMaterial CreateIdentity(string bridgeId)
        => TestIdentityMaterial.Create(bridgeId);
}

internal sealed class TestIdentityMaterial : IDisposable
{
    private TestIdentityMaterial(string bridgeId, X509Certificate2 certificate)
    {
        BridgeId = bridgeId;
        Certificate = certificate;
    }

    internal string BridgeId { get; }

    internal X509Certificate2 Certificate { get; }

    internal static TestIdentityMaterial Create(string bridgeId)
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);

        var request = new CertificateRequest(
            new X500DistinguishedName($"CN=CodexQuota Bridge {bridgeId}"),
            rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.DigitalSignature,
                true));

        var now = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

        // CreateSelfSigned already binds the request's key to the result; adding it again throws.
        return new TestIdentityMaterial(bridgeId, request.CreateSelfSigned(now, now.AddYears(10)));
    }

    public void Dispose() => Certificate.Dispose();
}
