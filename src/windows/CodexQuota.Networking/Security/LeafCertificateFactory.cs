using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CodexQuota.Networking.Security;

/// <summary>
/// Issues the replaceable TLS leaf certificate that Kestrel presents.
/// </summary>
/// <remarks>
/// The leaf is deliberately disposable. It exists only to bind the stable Bridge identity to the
/// addresses the Bridge is currently reachable at, so it is reissued whenever those change and it
/// expires quickly. Clients pin the identity, never the leaf.
/// </remarks>
public static class LeafCertificateFactory
{
    /// <summary>How long an issued leaf is valid for.</summary>
    public const int ValidityDays = 90;

    /// <summary>How much validity must remain before the leaf is considered due for renewal.</summary>
    public const int RenewalThresholdDays = 30;

    private const string LeafCommonName = "CodexQuota Bridge";

    /// <summary>Clock skew allowance, so a slightly fast client does not reject a fresh leaf.</summary>
    private static readonly TimeSpan NotBeforeBackdate = TimeSpan.FromHours(1);

    /// <summary>Server-authentication EKU: <c>1.3.6.1.5.5.7.3.1</c>.</summary>
    private static readonly Oid ServerAuthenticationOid = new("1.3.6.1.5.5.7.3.1");

    /// <summary>
    /// Issues a leaf signed by <paramref name="identity"/>, valid from shortly before
    /// <paramref name="now"/> and carrying exactly the supplied DNS names and addresses.
    /// </summary>
    public static X509Certificate2 CreateServerCertificate(
        BridgeIdentity identity,
        IReadOnlyCollection<IPAddress> addresses,
        IReadOnlyCollection<string> dnsNames,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentNullException.ThrowIfNull(dnsNames);

        using var key = RSA.Create(2048);

        var request = new CertificateRequest(
            new X500DistinguishedName($"CN={LeafCommonName}"),
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        // A leaf must never be usable as a CA.
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ServerAuthenticationOid], false));

        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();

        foreach (var dnsName in dnsNames)
        {
            subjectAlternativeNames.AddDnsName(dnsName);
        }

        foreach (var address in addresses)
        {
            subjectAlternativeNames.AddIpAddress(address);
        }

        request.CertificateExtensions.Add(subjectAlternativeNames.Build());

        var notBefore = now.UtcDateTime - NotBeforeBackdate;
        var notAfter = now.UtcDateTime.AddDays(ValidityDays);

        using var issued = request.Create(identity.Certificate, notBefore, notAfter, RandomNumberGenerator.GetBytes(16));
        using var withKey = issued.CopyWithPrivateKey(key);

        // Materialise the private key into the returned certificate. Without this the certificate
        // would keep a reference to the temporary RSA instance above, and Kestrel would be holding a
        // disposed key by the time it serves a handshake.
        return new X509Certificate2(withKey.Export(X509ContentType.Pfx));
    }

    /// <summary>
    /// True when the leaf can no longer be presented: it is past its validity, or close enough to
    /// expiry that renewal is due.
    /// </summary>
    public static bool NeedsRenewal(X509Certificate2 leaf, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(leaf);

        // X509Certificate2 exposes NotBefore/NotAfter in local time, so they must be converted
        // before they can be compared with a UTC instant.
        var notAfter = new DateTimeOffset(leaf.NotAfter.ToUniversalTime());
        var notBefore = new DateTimeOffset(leaf.NotBefore.ToUniversalTime());

        return now >= notAfter
               || now < notBefore
               || now >= notAfter.AddDays(-RenewalThresholdDays);
    }

    /// <summary>
    /// True when the leaf already carries exactly the required DNS names and addresses, so
    /// reissuing it would change nothing.
    /// </summary>
    public static bool Covers(
        X509Certificate2 leaf,
        IReadOnlyCollection<IPAddress> addresses,
        IReadOnlyCollection<string> dnsNames)
    {
        ArgumentNullException.ThrowIfNull(leaf);
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentNullException.ThrowIfNull(dnsNames);

        var sanExtension = leaf.Extensions
            .OfType<X509SubjectAlternativeNameExtension>()
            .FirstOrDefault();

        if (sanExtension is null)
        {
            return false;
        }

        var leafDnsNames = sanExtension.EnumerateDnsNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var leafAddresses = sanExtension.EnumerateIPAddresses().ToHashSet();

        return leafDnsNames.SetEquals(dnsNames) && leafAddresses.SetEquals(addresses);
    }
}
