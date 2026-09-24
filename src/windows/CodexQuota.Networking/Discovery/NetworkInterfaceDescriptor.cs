using System.Net;

namespace CodexQuota.Networking.Discovery;

/// <summary>The Windows network profile a connection is classified as.</summary>
public enum NetworkProfileKind
{
    Unknown,
    Private,
    Public,
    DomainAuthenticated,
}

/// <summary>
/// What kind of adapter an interface is. The distinction matters because several of these look like
/// ordinary Ethernet adapters to the operating system but must never become the API endpoint.
/// </summary>
public enum NetworkInterfaceKind
{
    Ethernet,
    Wireless,
    Loopback,

    /// <summary>A VPN or other tunnel adapter.</summary>
    Tunnel,

    /// <summary>A Hyper-V, Docker, WSL or similar virtual adapter.</summary>
    Virtual,
}

/// <summary>
/// A plain description of one network interface.
/// </summary>
/// <remarks>
/// The selector works on these rather than on live <c>NetworkInterface</c> objects so its policy can
/// be tested against adapters this machine does not have — which is the only way to prove that a
/// Docker bridge or a public Wi-Fi profile is refused.
/// </remarks>
public sealed record NetworkInterfaceDescriptor(
    string Id,
    string Name,
    NetworkInterfaceKind Kind,
    NetworkProfileKind Profile,
    bool IsOperational,
    IReadOnlyList<IPAddress> Addresses,
    string? Description = null);

/// <summary>A condition the Bridge cannot fix on its own and must tell the user about.</summary>
/// <param name="Code">Stable machine-readable code.</param>
/// <param name="Message">Text shown to the user. It must name the remedy, not just the symptom.</param>
public sealed record BridgeDiagnostic(string Code, string Message);

/// <summary>The stable diagnostic codes the Bridge surfaces.</summary>
public static class BridgeDiagnosticCodes
{
    /// <summary>No interface is both private and usable as a LAN endpoint.</summary>
    public const string NoEligibleLanInterface = "NO_ELIGIBLE_LAN_INTERFACE";

    /// <summary>The interface the user chose is not eligible under the exposure policy.</summary>
    public const string PreferredInterfaceNotEligible = "PREFERRED_INTERFACE_NOT_ELIGIBLE";

    /// <summary>The interface the user chose no longer exists.</summary>
    public const string PreferredInterfaceMissing = "PREFERRED_INTERFACE_MISSING";

    /// <summary>
    /// The endpoint is healthy locally but unreachable over the LAN, which in practice means the
    /// firewall.
    /// </summary>
    public const string FirewallBlockedOrUnreachable = "FIREWALL_BLOCKED_OR_UNREACHABLE";
}

/// <summary>Builds the diagnostics the Bridge shows the user.</summary>
public static class BridgeDiagnostics
{
    /// <summary>Nothing on this machine can serve as a private LAN endpoint.</summary>
    public static BridgeDiagnostic NoEligibleLanInterface()
        => new(
            BridgeDiagnosticCodes.NoEligibleLanInterface,
            "The Bridge is not listening on the LAN because no interface has been chosen. Open "
            + "Settings, choose the Ethernet or Wi-Fi adapter this PC uses on your home network, and "
            + "the Bridge will listen there. VPN, virtual, public and loopback adapters are refused.");

    /// <summary>The chosen interface is not eligible under the exposure policy.</summary>
    public static BridgeDiagnostic PreferredInterfaceNotEligible(NetworkInterfaceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        return new(
            BridgeDiagnosticCodes.PreferredInterfaceNotEligible,
            $"'{descriptor.Name}' cannot be used: the Bridge only listens on an operational "
            + "Ethernet or Wi-Fi interface whose Windows network profile is Private, with a private "
            + "IPv4 address. Public, VPN, loopback and virtual adapters are refused on purpose.");
    }

    /// <summary>The chosen interface is gone.</summary>
    public static BridgeDiagnostic PreferredInterfaceMissing(string interfaceId)
        => new(
            BridgeDiagnosticCodes.PreferredInterfaceMissing,
            $"The selected interface '{interfaceId}' no longer exists. Choose another one in Settings.");

    /// <summary>
    /// The endpoint is bound and healthy locally but a LAN peer could not reach it.
    /// </summary>
    /// <remarks>
    /// The remedy is deliberately narrow. The Bridge never asks anyone to turn the firewall off: a
    /// rule scoped to this executable, this port and the Private profile is both sufficient and
    /// safe.
    /// </remarks>
    public static BridgeDiagnostic FirewallBlockedOrUnreachable(int port)
        => new(
            BridgeDiagnosticCodes.FirewallBlockedOrUnreachable,
            $"The Bridge is listening locally but could not be reached over the LAN. Allow the "
            + $"Bridge executable inbound on TCP port {port} for the Windows Private profile. "
            + "Do not disable Windows Firewall.");
}
