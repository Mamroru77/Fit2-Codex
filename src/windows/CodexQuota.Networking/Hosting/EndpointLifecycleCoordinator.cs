using System.Security.Cryptography.X509Certificates;
using CodexQuota.Networking.Discovery;
using CodexQuota.Networking.Security;

namespace CodexQuota.Networking.Hosting;

/// <summary>What one endpoint re-evaluation did.</summary>
/// <param name="LeafRenewed">Whether a new leaf certificate was issued.</param>
/// <param name="IsAdvertising">Whether the Bridge is currently advertised on the LAN.</param>
/// <param name="Diagnostic">Why nothing is being advertised, when nothing is.</param>
public sealed record EndpointLifecycleResult(bool LeafRenewed, bool IsAdvertising, BridgeDiagnostic? Diagnostic);

/// <summary>
/// Keeps the listening endpoint, the leaf certificate and the mDNS advertisement in step with the
/// machine's actual network.
/// </summary>
/// <remarks>
/// <para>
/// The three things move together but mean different things. The address is where the Bridge is; the
/// leaf is a short-lived certificate that binds the stable Bridge identity to that address; the mDNS
/// record is how a phone finds the address without being told.
/// </para>
/// <para>
/// What never moves is the identity. An IP change renews the leaf and re-publishes discovery, and the
/// pinned fingerprint Android checks is unchanged — which is precisely why an address change does not
/// require re-pairing.
/// </para>
/// </remarks>
public sealed class EndpointLifecycleCoordinator : IAsyncDisposable
{
    private const string LocalhostDnsName = "localhost";

    private readonly BridgeIdentity _identity;
    private readonly IMdnsPublisher _publisher;
    private readonly string _bridgeVersion;

    private X509Certificate2? _leaf;
    private MdnsAdvertisement? _advertisement;
    private bool _disposed;

    public EndpointLifecycleCoordinator(
        BridgeIdentity identity,
        IMdnsPublisher publisher,
        string bridgeVersion)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentException.ThrowIfNullOrWhiteSpace(bridgeVersion);

        _identity = identity;
        _publisher = publisher;
        _bridgeVersion = bridgeVersion;
    }

    /// <summary>The leaf currently presented, or <c>null</c> when there is no endpoint.</summary>
    public X509Certificate2? CurrentLeaf => _leaf;

    /// <summary>Whether the Bridge is currently advertised on the LAN.</summary>
    public bool IsAdvertising => _advertisement is not null;

    /// <summary>The advertisement currently published, or <c>null</c>.</summary>
    public MdnsAdvertisement? CurrentAdvertisement => _advertisement;

    /// <summary>
    /// Re-evaluates the endpoint. Call it at startup and on every network change.
    /// </summary>
    public async Task<EndpointLifecycleResult> ApplyAsync(
        LanSelection selection,
        int port,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!selection.HasEndpoint)
        {
            // No eligible endpoint means no advertisement either. Advertising an address the Bridge
            // is not listening on is worse than advertising nothing.
            if (_advertisement is not null)
            {
                await _publisher.UnpublishAsync(cancellationToken).ConfigureAwait(false);
                _advertisement = null;
            }

            return new EndpointLifecycleResult(LeafRenewed: false, IsAdvertising: false, selection.Diagnostic);
        }

        var addresses = new[] { selection.Address! };
        var dnsNames = new[] { LocalhostDnsName, _identity.DnsName };

        var renewed = false;

        if (_leaf is null
            || LeafCertificateFactory.NeedsRenewal(_leaf, now)
            || !LeafCertificateFactory.Covers(_leaf, addresses, dnsNames))
        {
            // The address moved, or the leaf is old: issue a new one. The identity is untouched, so
            // the pin a paired phone holds stays valid.
            _leaf?.Dispose();
            _leaf = LeafCertificateFactory.CreateServerCertificate(_identity, addresses, dnsNames, now);
            renewed = true;
        }

        var advertisement = new MdnsAdvertisement(_identity.BridgeId, _bridgeVersion, port, Tls: true);

        await _publisher.PublishAsync(advertisement, cancellationToken).ConfigureAwait(false);
        _advertisement = advertisement;

        return new EndpointLifecycleResult(renewed, IsAdvertising: true, Diagnostic: null);
    }

    /// <summary>
    /// The diagnostic to surface when the endpoint is healthy locally but a LAN peer cannot reach it.
    /// </summary>
    public static BridgeDiagnostic FirewallBlockedDiagnostic(int port)
        => BridgeDiagnostics.FirewallBlockedOrUnreachable(port);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _publisher.DisposeAsync().ConfigureAwait(false);

        _leaf?.Dispose();
        _leaf = null;
        _advertisement = null;
    }
}
