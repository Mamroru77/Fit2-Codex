using System.Net;
using CodexQuota.Networking.Discovery;

namespace CodexQuota.Networking.Tests;

/// <summary>
/// Endpoint selection is an exposure policy. These tests feed it adapters this machine does not have,
/// because "a Docker bridge must never become the API endpoint" cannot be proven any other way.
/// </summary>
public class PrivateLanInterfaceSelectorTests
{
    private readonly PrivateLanInterfaceSelector _selector = new();

    [Fact]
    public void OnlyAPrivateOperationalAdapterIsEligible()
    {
        var selection = _selector.Select([
            Descriptor("vpn", NetworkInterfaceKind.Tunnel, NetworkProfileKind.Private, "10.8.0.2"),
            Descriptor("docker", NetworkInterfaceKind.Virtual, NetworkProfileKind.Private, "172.17.0.1"),
            Descriptor("hyperv", NetworkInterfaceKind.Virtual, NetworkProfileKind.Private, "192.168.56.1"),
            Descriptor("public-wifi", NetworkInterfaceKind.Wireless, NetworkProfileKind.Public, "192.168.1.50"),
            Descriptor("loopback", NetworkInterfaceKind.Loopback, NetworkProfileKind.Private, "127.0.0.1"),
            Descriptor("ethernet", NetworkInterfaceKind.Ethernet, NetworkProfileKind.Private, "192.168.1.23"),
        ]);

        Assert.True(selection.HasEndpoint);
        Assert.Equal("ethernet", selection.Interface!.Id);
        Assert.Equal("192.168.1.23", selection.Address!.ToString());
        Assert.Null(selection.Diagnostic);
    }

    [Fact]
    public void EveryRefusedKindIsRefusedOnItsOwn()
    {
        var refused = new[]
        {
            Descriptor("vpn", NetworkInterfaceKind.Tunnel, NetworkProfileKind.Private, "10.8.0.2"),
            Descriptor("docker", NetworkInterfaceKind.Virtual, NetworkProfileKind.Private, "172.17.0.1"),
            Descriptor("hyperv", NetworkInterfaceKind.Virtual, NetworkProfileKind.Private, "192.168.56.1"),
            Descriptor("public-wifi", NetworkInterfaceKind.Wireless, NetworkProfileKind.Public, "192.168.1.50"),
            Descriptor("domain", NetworkInterfaceKind.Ethernet, NetworkProfileKind.DomainAuthenticated, "192.168.1.60"),
            Descriptor("loopback", NetworkInterfaceKind.Loopback, NetworkProfileKind.Private, "127.0.0.1"),
            Descriptor("down", NetworkInterfaceKind.Ethernet, NetworkProfileKind.Private, "192.168.1.70", isOperational: false),
            Descriptor("link-local", NetworkInterfaceKind.Ethernet, NetworkProfileKind.Private, "169.254.10.1"),
        };

        foreach (var descriptor in refused)
        {
            var selection = _selector.Select([descriptor]);

            Assert.False(selection.HasEndpoint);
            Assert.Equal(BridgeDiagnosticCodes.NoEligibleLanInterface, selection.Diagnostic!.Code);
        }
    }

    [Fact]
    public void WiredIsPreferredOverWirelessWhenBothAreEligible()
    {
        var selection = _selector.Select([
            Descriptor("wifi", NetworkInterfaceKind.Wireless, NetworkProfileKind.Private, "192.168.1.40"),
            Descriptor("ethernet", NetworkInterfaceKind.Ethernet, NetworkProfileKind.Private, "192.168.1.23"),
        ]);

        Assert.Equal("ethernet", selection.Interface!.Id);
    }

    [Fact]
    public void SelectionIsDeterministicAcrossRuns()
    {
        var interfaces = new[]
        {
            Descriptor("wifi-b", NetworkInterfaceKind.Wireless, NetworkProfileKind.Private, "192.168.1.41"),
            Descriptor("wifi-a", NetworkInterfaceKind.Wireless, NetworkProfileKind.Private, "192.168.1.42"),
        };

        Assert.Equal(_selector.Select(interfaces).Interface!.Id, _selector.Select(interfaces).Interface!.Id);
        Assert.Equal("wifi-a", _selector.Select(interfaces).Interface!.Id);
    }

    [Fact]
    public void AnExplicitChoiceWinsAmongEligibleCandidates()
    {
        var selection = _selector.Select(
            [
                Descriptor("ethernet", NetworkInterfaceKind.Ethernet, NetworkProfileKind.Private, "192.168.1.23"),
                Descriptor("wifi", NetworkInterfaceKind.Wireless, NetworkProfileKind.Private, "192.168.1.40"),
            ],
            preferredInterfaceId: "wifi");

        Assert.Equal("wifi", selection.Interface!.Id);
        Assert.Equal("192.168.1.40", selection.Address!.ToString());
    }

    [Fact]
    public void AnExplicitChoiceDoesNotOverrideTheExposurePolicy()
    {
        // A user selecting a public adapter must get a diagnostic, not an exposed API.
        var selection = _selector.Select(
            [Descriptor("public-wifi", NetworkInterfaceKind.Wireless, NetworkProfileKind.Public, "192.168.1.50")],
            preferredInterfaceId: "public-wifi");

        Assert.False(selection.HasEndpoint);
        Assert.Equal(BridgeDiagnosticCodes.PreferredInterfaceNotEligible, selection.Diagnostic!.Code);
    }

    [Fact]
    public void AChoiceThatNoLongerExistsIsReportedAsMissing()
    {
        var selection = _selector.Select(
            [Descriptor("ethernet", NetworkInterfaceKind.Ethernet, NetworkProfileKind.Private, "192.168.1.23")],
            preferredInterfaceId: "gone");

        Assert.False(selection.HasEndpoint);
        Assert.Equal(BridgeDiagnosticCodes.PreferredInterfaceMissing, selection.Diagnostic!.Code);
    }

    [Theory]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("172.15.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("127.0.0.1", false)]
    public void OnlyRfc1918AddressesCountAsPrivate(string address, bool expected)
        => Assert.Equal(expected, PrivateLanInterfaceSelector.IsPrivateIpv4(IPAddress.Parse(address)));

    [Fact]
    public void AnIpv6OnlyInterfaceIsNotEligible()
    {
        var selection = _selector.Select([
            new NetworkInterfaceDescriptor(
                "eth6",
                "Ethernet 6",
                NetworkInterfaceKind.Ethernet,
                NetworkProfileKind.Private,
                IsOperational: true,
                [IPAddress.Parse("fe80::1")]),
        ]);

        Assert.False(selection.HasEndpoint);
    }

    [Fact]
    public void TheFirewallDiagnosticNamesTheNarrowRemedy()
    {
        var diagnostic = BridgeDiagnostics.FirewallBlockedOrUnreachable(47821);

        Assert.Equal(BridgeDiagnosticCodes.FirewallBlockedOrUnreachable, diagnostic.Code);
        Assert.Contains("47821", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Private", diagnostic.Message, StringComparison.Ordinal);

        // The remedy is a scoped allow rule, and the message says so explicitly.
        Assert.Contains("Do not disable Windows Firewall", diagnostic.Message, StringComparison.Ordinal);
    }

    private static NetworkInterfaceDescriptor Descriptor(
        string id,
        NetworkInterfaceKind kind,
        NetworkProfileKind profile,
        string address,
        bool isOperational = true)
        => new(id, id, kind, profile, isOperational, [IPAddress.Parse(address)]);
}
