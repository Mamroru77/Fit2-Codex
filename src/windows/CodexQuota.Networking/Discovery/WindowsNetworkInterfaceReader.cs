using System.Net;
using System.Net.NetworkInformation;

namespace CodexQuota.Networking.Discovery;

/// <summary>
/// Reads this machine's network interfaces.
/// </summary>
/// <remarks>
/// <para>
/// Adapter <em>kind</em> is determined by code and is not negotiable: loopback, tunnel and
/// virtual adapters are classified as such from the adapter type and its own description, and the
/// selector refuses them outright. Hyper-V, Docker, WSL, VMware and VirtualBox all present themselves
/// to the operating system as plain Ethernet, so the description is the only reliable signal.
/// </para>
/// <para>
/// Network <em>profile</em> is deliberately not guessed. Windows exposes it through the Network List
/// Manager COM API, whose vtable would have to be declared in full to be reached safely; a declaration
/// that is one slot out would not fail, it would silently read the wrong field and could report a
/// public network as private — the one mistake this whole policy exists to prevent. WinRT does not
/// expose the profile at all. So an interface is <see cref="NetworkProfileKind.Unknown"/> unless the
/// user has explicitly chosen it, and <see cref="NetworkProfileKind.Unknown"/> is refused.
/// </para>
/// <para>
/// The result is that the Bridge does not listen until the user picks their LAN interface once, in
/// Settings. That is the conservative direction to fail in, and it is the same choice the spec already
/// asks the user to make.
/// </para>
/// </remarks>
public static class WindowsNetworkInterfaceReader
{
    private static readonly string[] VirtualAdapterMarkers =
    [
        "hyper-v",
        "vethernet",
        "virtualbox",
        "vmware",
        "docker",
        "wsl",
        "tap-",
        "tap0",
        "tun",
        "tailscale",
        "wireguard",
        "zerotier",
        "openvpn",
        "npcap",
    ];

    /// <summary>
    /// Describes every interface on this machine.
    /// </summary>
    /// <param name="trustedInterfaceIds">
    /// Interfaces the user has explicitly chosen. A chosen interface is treated as
    /// <see cref="NetworkProfileKind.Private"/>, because that is the user asserting which of their
    /// networks the Bridge belongs on. Their adapter kind is still enforced.
    /// </param>
    public static IReadOnlyList<NetworkInterfaceDescriptor> Read(IReadOnlySet<string>? trustedInterfaceIds = null)
    {
        var trusted = trustedInterfaceIds ?? new HashSet<string>(StringComparer.Ordinal);
        var descriptors = new List<NetworkInterfaceDescriptor>();

        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                descriptors.Add(Describe(networkInterface, trusted));
            }
            catch (Exception exception) when (exception is NetworkInformationException
                                                  or PlatformNotSupportedException
                                                  or InvalidOperationException)
            {
                // An adapter that cannot be read is an adapter that cannot be used.
            }
        }

        return descriptors;
    }

    private static NetworkInterfaceDescriptor Describe(
        NetworkInterface networkInterface,
        IReadOnlySet<string> trusted)
        => new(
            networkInterface.Id,
            networkInterface.Name,
            Classify(networkInterface),
            trusted.Contains(networkInterface.Id) ? NetworkProfileKind.Private : NetworkProfileKind.Unknown,
            networkInterface.OperationalStatus == OperationalStatus.Up,
            ReadAddresses(networkInterface),
            networkInterface.Description);

    private static IReadOnlyList<IPAddress> ReadAddresses(NetworkInterface networkInterface)
    {
        try
        {
            return networkInterface
                .GetIPProperties()
                .UnicastAddresses
                .Select(unicast => unicast.Address)
                .ToArray();
        }
        catch (Exception exception) when (exception is NetworkInformationException or PlatformNotSupportedException)
        {
            return [];
        }
    }

    private static NetworkInterfaceKind Classify(NetworkInterface networkInterface)
    {
        if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
        {
            return NetworkInterfaceKind.Loopback;
        }

        if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
        {
            return NetworkInterfaceKind.Tunnel;
        }

        if (LooksVirtual(networkInterface))
        {
            return NetworkInterfaceKind.Virtual;
        }

        return networkInterface.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
            ? NetworkInterfaceKind.Wireless
            : NetworkInterfaceKind.Ethernet;
    }

    private static bool LooksVirtual(NetworkInterface networkInterface)
    {
        var text = $"{networkInterface.Name} {networkInterface.Description}";

        return VirtualAdapterMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
