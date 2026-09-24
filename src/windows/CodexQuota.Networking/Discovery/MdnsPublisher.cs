using Makaretu.Dns;

namespace CodexQuota.Networking.Discovery;

/// <summary>
/// Publishes the Bridge over mDNS/DNS-SD using the pinned <c>Systola.Makaretu.Dns.Multicast</c> stack.
/// </summary>
/// <remarks>
/// <para>
/// The advertisement is replaced, never accumulated: when the PC's address changes the old record is
/// withdrawn first, so a client can never discover a stale endpoint and connect to nothing.
/// </para>
/// <para>
/// The multicast socket is only started when something is actually published, so a Bridge with no
/// eligible interface never binds UDP 5353 at all.
/// </para>
/// </remarks>
public sealed class MdnsPublisher : IMdnsPublisher
{
    private readonly object _gate = new();

    private MulticastService? _multicast;
    private ServiceDiscovery? _discovery;
    private ServiceProfile? _profile;
    private bool _disposed;

    public Task PublishAsync(MdnsAdvertisement advertisement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(advertisement);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            EnsureStarted();

            if (_profile is not null)
            {
                // Withdraw the previous address before announcing the new one.
                _discovery!.Unadvertise(_profile);
                _profile = null;
            }

            var profile = new ServiceProfile(
                advertisement.BridgeId,
                MdnsAdvertisement.ServiceType,
                (ushort)advertisement.Port);

            foreach (var (key, value) in advertisement.ToTxtRecords())
            {
                profile.AddProperty(key, value);
            }

            if (_discovery!.Probe(profile))
            {
                // Another device already answers for this Bridge id. Advertising anyway would make
                // the name ambiguous, which is exactly the situation a client cannot resolve safely.
                throw new InvalidOperationException(
                    $"Another device on this LAN already advertises the Bridge id '{advertisement.BridgeId}'.");
            }

            _discovery.Advertise(profile);
            _discovery.Announce(profile);
            _profile = profile;
        }

        return Task.CompletedTask;
    }

    public Task UnpublishAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_profile is not null && _discovery is not null)
            {
                _discovery.Unadvertise(_profile);
                _profile = null;
            }
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;

            if (_profile is not null && _discovery is not null)
            {
                try
                {
                    _discovery.Unadvertise(_profile);
                }
                catch (Exception)
                {
                    // Shutting down anyway.
                }

                _profile = null;
            }

            _discovery?.Dispose();
            _multicast?.Dispose();
            _discovery = null;
            _multicast = null;
        }

        return ValueTask.CompletedTask;
    }

    private void EnsureStarted()
    {
        if (_multicast is not null)
        {
            return;
        }

        _multicast = new MulticastService();
        _discovery = new ServiceDiscovery(_multicast);

        _multicast.Start();
    }
}
