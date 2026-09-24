using System.Net;
using System.Text.Json;
using CodexQuota.Networking.Discovery;
using CodexQuota.Networking.Hosting;
using CodexQuota.Networking.Security;

namespace CodexQuota.Networking.Tests;

/// <summary>
/// The endpoint lifecycle: an address change renews the leaf and re-publishes discovery, and the
/// Bridge identity — the pin a paired phone holds — does not move.
/// </summary>
public class EndpointLifecycleCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 13, 30, 0, TimeSpan.Zero);
    private const int Port = 47821;

    /// <summary>An eligible selection for the given adapter and address.</summary>
    private static LanSelection Selection(string interfaceId, string address)
        => new(
            new NetworkInterfaceDescriptor(
                interfaceId,
                interfaceId,
                NetworkInterfaceKind.Ethernet,
                NetworkProfileKind.Private,
                IsOperational: true,
                [IPAddress.Parse(address)]),
            IPAddress.Parse(address),
            null);

    [Fact]
    public async Task TheFirstEvaluationIssuesALeafAndStartsAdvertising()
    {
        using var material = TestCertificates.CreateIdentity("bridge-1");
        using var identity = new BridgeIdentity(material.BridgeId, material.Certificate);
        var publisher = new RecordingMdnsPublisher();
        await using var coordinator = new EndpointLifecycleCoordinator(identity, publisher, "1.0.0-test");

        var result = await coordinator.ApplyAsync(Selection("eth0", "192.168.1.23"), Port, Now, CancellationToken.None);

        Assert.True(result.LeafRenewed);
        Assert.True(result.IsAdvertising);
        Assert.NotNull(coordinator.CurrentLeaf);
        Assert.Single(publisher.Published);
        Assert.Equal(Port, publisher.Published[0].Port);
        Assert.Equal("bridge-1", publisher.Published[0].BridgeId);
        Assert.True(publisher.Published[0].Tls);
    }

    [Fact]
    public async Task AnAddressChangeRenewsTheLeafButNotTheBridgeIdentity()
    {
        using var material = TestCertificates.CreateIdentity("bridge-1");
        using var identity = new BridgeIdentity(material.BridgeId, material.Certificate);
        var publisher = new RecordingMdnsPublisher();
        await using var coordinator = new EndpointLifecycleCoordinator(identity, publisher, "1.0.0-test");

        var pinBefore = identity.SpkiSha256;

        await coordinator.ApplyAsync(Selection("eth0", "192.168.1.23"), Port, Now, CancellationToken.None);
        var firstLeaf = coordinator.CurrentLeaf!;
        var firstThumbprint = firstLeaf.Thumbprint;

        // The PC moves to a new address.
        var moved = await coordinator.ApplyAsync(
            Selection("eth0", "192.168.1.87"),
            Port,
            Now.AddMinutes(5),
            CancellationToken.None);

        Assert.True(moved.LeafRenewed);
        Assert.NotEqual(firstThumbprint, coordinator.CurrentLeaf!.Thumbprint);

        // The pin Android checks is unchanged, which is why an address change does not need re-pairing.
        Assert.Equal(pinBefore, identity.SpkiSha256);
        Assert.Equal(
            identity.SpkiSha256,
            CertificateFingerprint.ComputeSpkiSha256(identity.Certificate));

        // And discovery was re-published for the new address. Publishing replaces rather than
        // accumulates, which is what keeps a stale address from staying visible.
        Assert.Equal(2, publisher.Published.Count);
        Assert.All(publisher.Published, advertisement => Assert.Equal("bridge-1", advertisement.BridgeId));
        Assert.True(coordinator.IsAdvertising);
        Assert.Equal(Port, coordinator.CurrentAdvertisement!.Port);
    }

    [Fact]
    public async Task TheLeafIsNotReissuedWhenNothingChanged()
    {
        using var material = TestCertificates.CreateIdentity("bridge-1");
        using var identity = new BridgeIdentity(material.BridgeId, material.Certificate);
        var publisher = new RecordingMdnsPublisher();
        await using var coordinator = new EndpointLifecycleCoordinator(identity, publisher, "1.0.0-test");

        await coordinator.ApplyAsync(Selection("eth0", "192.168.1.23"), Port, Now, CancellationToken.None);
        var first = coordinator.CurrentLeaf!;

        var again = await coordinator.ApplyAsync(
            Selection("eth0", "192.168.1.23"),
            Port,
            Now.AddMinutes(1),
            CancellationToken.None);

        // Churning the certificate on every network event would force every phone to re-verify for no
        // reason.
        Assert.False(again.LeafRenewed);
        Assert.Same(first, coordinator.CurrentLeaf);
    }

    [Fact]
    public async Task AnAgedLeafIsRenewedEvenWhenNothingElseChanged()
    {
        using var material = TestCertificates.CreateIdentity("bridge-1");
        using var identity = new BridgeIdentity(material.BridgeId, material.Certificate);
        var publisher = new RecordingMdnsPublisher();
        await using var coordinator = new EndpointLifecycleCoordinator(identity, publisher, "1.0.0-test");

        await coordinator.ApplyAsync(Selection("eth0", "192.168.1.23"), Port, Now, CancellationToken.None);

        var renewalDue = Now.AddDays(LeafCertificateFactory.ValidityDays - LeafCertificateFactory.RenewalThresholdDays + 1);

        var renewed = await coordinator.ApplyAsync(
            Selection("eth0", "192.168.1.23"),
            Port,
            renewalDue,
            CancellationToken.None);

        Assert.True(renewed.LeafRenewed);
    }

    [Fact]
    public async Task LosingEveryEligibleInterfaceStopsAdvertising()
    {
        using var material = TestCertificates.CreateIdentity("bridge-1");
        using var identity = new BridgeIdentity(material.BridgeId, material.Certificate);
        var publisher = new RecordingMdnsPublisher();
        await using var coordinator = new EndpointLifecycleCoordinator(identity, publisher, "1.0.0-test");

        await coordinator.ApplyAsync(Selection("eth0", "192.168.1.23"), Port, Now, CancellationToken.None);
        Assert.True(coordinator.IsAdvertising);

        var gone = await coordinator.ApplyAsync(
            new LanSelection(null, null, BridgeDiagnostics.NoEligibleLanInterface()),
            Port,
            Now.AddMinutes(1),
            CancellationToken.None);

        // Advertising an address the Bridge is not listening on is worse than advertising nothing.
        Assert.False(gone.IsAdvertising);
        Assert.False(coordinator.IsAdvertising);
        Assert.NotNull(gone.Diagnostic);
        Assert.Equal(1, publisher.Unpublished);
    }

    [Fact]
    public async Task AnUnpublishedBridgeIsNeverAdvertised()
    {
        using var material = TestCertificates.CreateIdentity("bridge-1");
        using var identity = new BridgeIdentity(material.BridgeId, material.Certificate);
        var publisher = new RecordingMdnsPublisher();
        await using var coordinator = new EndpointLifecycleCoordinator(identity, publisher, "1.0.0-test");

        await coordinator.ApplyAsync(
            new LanSelection(null, null, BridgeDiagnostics.NoEligibleLanInterface()),
            Port,
            Now,
            CancellationToken.None);

        Assert.Empty(publisher.Published);
        Assert.Null(coordinator.CurrentLeaf);
    }
}

/// <summary>
/// Discovery is unauthenticated, so what it publishes is public by definition.
/// </summary>
public class MdnsAdvertisementTests
{
    [Fact]
    public void TheTxtRecordContainsExactlyTheThreePublicFields()
    {
        var advertisement = new MdnsAdvertisement("bridge-1", "v1", 47821, Tls: true);

        var txt = advertisement.ToTxtRecords();

        Assert.Equal(new[] { "apiVersion", "bridgeId", "tls" }, txt.Keys.OrderBy(key => key, StringComparer.Ordinal));
        Assert.Equal("bridge-1", txt["bridgeId"]);
        Assert.Equal("v1", txt["apiVersion"]);
        Assert.Equal("1", txt["tls"]);
    }

    [Fact]
    public void TheTxtRecordLeaksNothingPersonal()
    {
        var advertisement = new MdnsAdvertisement("bridge-1", "v1", 47821, Tls: true);

        var rendered = string.Join(
            ';',
            advertisement.ToTxtRecords().Select(entry => $"{entry.Key}={entry.Value}"));

        foreach (var forbidden in new[] { "token", "secret", "key", "email", "user", "account", "password", "pairing" })
        {
            Assert.DoesNotContain(forbidden, rendered, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TheServiceTypeIsTheDocumentedOne()
    {
        Assert.Equal("_codexquota._tcp", MdnsAdvertisement.ServiceType);
        Assert.Equal("_codexquota._tcp.local.", MdnsAdvertisement.QualifiedServiceType);
    }
}

/// <summary>
/// The QR code is displayed, photographed and often kept, so it is treated as public.
/// </summary>
public class PairingQrPayloadTests
{
    [Fact]
    public void ThePayloadCarriesExactlyTheDocumentedFields()
    {
        var payload = PairingQrPayload.Create(
            host: "192.168.1.23",
            port: 47821,
            pairingId: "abc123",
            bridgeId: "bridge-1",
            identityFingerprint: "AABBCCDD");

        using var document = JsonDocument.Parse(payload.ToJson());

        var keys = document.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(
            name => name,
            StringComparer.Ordinal);

        Assert.Equal(PairingQrPayload.Keys.OrderBy(name => name, StringComparer.Ordinal), keys);

        Assert.Equal("v1", document.RootElement.GetProperty("v").GetString());
        Assert.Equal("192.168.1.23", document.RootElement.GetProperty("host").GetString());
        Assert.Equal(47821, document.RootElement.GetProperty("port").GetInt32());
    }

    [Fact]
    public void ThePayloadCarriesNoCredential()
    {
        var payload = PairingQrPayload.Create("192.168.1.23", 47821, "abc123", "bridge-1", "AABBCCDD");

        var json = payload.ToJson();

        // Whoever reads the QR code must not be able to authenticate as a paired device.
        foreach (var forbidden in new[] { "token", "credential", "secret", "password", "authorization" })
        {
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TheFingerprintIsPresentBecauseItIsTheTrustAnchor()
    {
        var payload = PairingQrPayload.Create("192.168.1.23", 47821, "abc123", "bridge-1", "AABBCCDD");

        Assert.Equal("AABBCCDD", payload.IdentityFingerprint);
    }
}

/// <summary>Records what the coordinator asked the publisher to do.</summary>
internal sealed class RecordingMdnsPublisher : IMdnsPublisher
{
    private readonly List<MdnsAdvertisement> _published = [];
    private readonly object _gate = new();

    internal IReadOnlyList<MdnsAdvertisement> Published
    {
        get
        {
            lock (_gate)
            {
                return _published.ToArray();
            }
        }
    }

    internal int Unpublished { get; private set; }

    public Task PublishAsync(MdnsAdvertisement advertisement, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _published.Add(advertisement);
        }

        return Task.CompletedTask;
    }

    public Task UnpublishAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Unpublished++;
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
