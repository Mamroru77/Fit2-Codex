using System.Net;
using System.Net.Sockets;

namespace CodexQuota.Networking.Discovery;

/// <summary>The interface and address the Bridge should listen on, or why there is none.</summary>
/// <param name="Interface">The selected interface, or <c>null</c> when nothing is eligible.</param>
/// <param name="Address">The private IPv4 address to bind.</param>
/// <param name="Diagnostic">Why nothing was selected, when nothing was.</param>
public sealed record LanSelection(
    NetworkInterfaceDescriptor? Interface,
    IPAddress? Address,
    BridgeDiagnostic? Diagnostic)
{
    /// <summary>True when a usable endpoint was found.</summary>
    public bool HasEndpoint => Interface is not null && Address is not null;

    internal static LanSelection Of(NetworkInterfaceDescriptor descriptor, IPAddress address)
        => new(descriptor, address, null);

    internal static LanSelection None(BridgeDiagnostic diagnostic) => new(null, null, diagnostic);
}

/// <summary>
/// Chooses the one interface the Bridge may listen on.
/// </summary>
/// <remarks>
/// <para>
/// This is an exposure policy, not a preference. The Bridge is only ever reachable on an operational
/// Ethernet or Wi-Fi adapter whose Windows profile is <c>Private</c> and which has a private IPv4
/// address, so a public network, a VPN, a Hyper-V switch or a Docker bridge can never become the API
/// endpoint by merely existing.
/// </para>
/// <para>
/// An explicit user choice wins among eligible candidates. It does not override the policy: a user
/// selecting a public adapter gets a diagnostic, not an exposed API.
/// </para>
/// </remarks>
public sealed class PrivateLanInterfaceSelector
{
    /// <summary>Selects the endpoint, honouring an explicit interface choice when it is eligible.</summary>
    public LanSelection Select(
        IReadOnlyList<NetworkInterfaceDescriptor> interfaces,
        string? preferredInterfaceId = null)
    {
        ArgumentNullException.ThrowIfNull(interfaces);

        if (!string.IsNullOrWhiteSpace(preferredInterfaceId))
        {
            return SelectPreferred(interfaces, preferredInterfaceId);
        }

        var candidate = interfaces
            .Where(IsEligible)
            .OrderBy(Rank)
            .ThenBy(descriptor => descriptor.Id, StringComparer.Ordinal)
            .FirstOrDefault();

        return candidate is null
            ? LanSelection.None(BridgeDiagnostics.NoEligibleLanInterface())
            : LanSelection.Of(candidate, AddressOf(candidate)!);
    }

    /// <summary>
    /// True when <paramref name="address"/> is a private IPv4 address (RFC 1918). Link-local and
    /// loopback are not, so neither can be selected by accident.
    /// </summary>
    public static bool IsPrivateIpv4(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var octets = address.GetAddressBytes();

        return octets[0] == 10
               || (octets[0] == 172 && octets[1] >= 16 && octets[1] <= 31)
               || (octets[0] == 192 && octets[1] == 168);
    }

    private static LanSelection SelectPreferred(
        IReadOnlyList<NetworkInterfaceDescriptor> interfaces,
        string preferredInterfaceId)
    {
        var preferred = interfaces.FirstOrDefault(
            descriptor => string.Equals(descriptor.Id, preferredInterfaceId, StringComparison.Ordinal));

        if (preferred is null)
        {
            return LanSelection.None(BridgeDiagnostics.PreferredInterfaceMissing(preferredInterfaceId));
        }

        return IsEligible(preferred)
            ? LanSelection.Of(preferred, AddressOf(preferred)!)
            : LanSelection.None(BridgeDiagnostics.PreferredInterfaceNotEligible(preferred));
    }

    private static bool IsEligible(NetworkInterfaceDescriptor descriptor)
        => descriptor.IsOperational
           && descriptor.Kind is NetworkInterfaceKind.Ethernet or NetworkInterfaceKind.Wireless
           && descriptor.Profile == NetworkProfileKind.Private
           && AddressOf(descriptor) is not null;

    /// <summary>A physical wired adapter is preferred over Wi-Fi when both are eligible.</summary>
    private static int Rank(NetworkInterfaceDescriptor descriptor)
        => descriptor.Kind == NetworkInterfaceKind.Ethernet ? 0 : 1;

    private static IPAddress? AddressOf(NetworkInterfaceDescriptor descriptor)
        => descriptor.Addresses.FirstOrDefault(IsPrivateIpv4);
}
