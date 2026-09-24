using System.Net.NetworkInformation;
using CodexQuota.Core.Quota;
using CodexQuota.Networking.Api.V1;
using CodexQuota.Networking.Auth;
using CodexQuota.Networking.Discovery;
using CodexQuota.Networking.Hosting;
using CodexQuota.Networking.Pairing;
using CodexQuota.Networking.Security;
using CodexQuota.Networking.WebSockets;
using CodexQuota.Storage.Devices;
using CodexQuota.Storage.History;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodexQuota.Desktop.Runtime;

/// <summary>
/// Owns the LAN API for the lifetime of the desktop process: it picks the endpoint, issues the leaf,
/// starts Kestrel, advertises over mDNS and follows the machine's network as it changes.
/// </summary>
/// <remarks>
/// <para>
/// The Bridge is only reachable while this service is running, and it is only ever reachable on an
/// interface the exposure policy accepts. If nothing is eligible, the Bridge still runs locally and
/// reports a diagnostic instead of listening somewhere it should not.
/// </para>
/// <para>
/// Shutdown is ordered, because the order is what makes a stop look like a stop rather than a
/// failure: stop pairing, tell the clients, withdraw discovery, stop the listener.
/// </para>
/// </remarks>
public sealed class LanApiHostedService : IHostedService, IAsyncDisposable
{
    /// <summary>How long network changes are coalesced before the endpoint is re-evaluated.</summary>
    public static readonly TimeSpan NetworkChangeDebounce = TimeSpan.FromMilliseconds(500);

    private readonly BridgeIdentity _identity;
    private readonly PairingService _pairing;
    private readonly DeviceTokenService _deviceTokens;
    private readonly IPairedDeviceRepository _pairedDevices;
    private readonly IHistoryRepository _history;
    private readonly IQuotaStateStore _store;
    private readonly string _bridgeVersion;
    private readonly Func<IReadOnlySet<string>, IReadOnlyList<NetworkInterfaceDescriptor>> _interfaceReader;
    private readonly IMdnsPublisher _publisher;
    private readonly ILogger<LanApiHostedService> _logger;
    private readonly PrivateLanInterfaceSelector _selector = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _trustedInterfaces = new(StringComparer.Ordinal);

    private EndpointLifecycleCoordinator? _coordinator;
    private BridgeApiHost? _api;
    private QuotaWebSocketHub? _hub;
    private string? _preferredInterfaceId;
    private bool _started;

    public LanApiHostedService(
        BridgeIdentity identity,
        PairingService pairing,
        DeviceTokenService deviceTokens,
        IPairedDeviceRepository pairedDevices,
        IHistoryRepository history,
        IQuotaStateStore store,
        string bridgeVersion,
        ILogger<LanApiHostedService> logger,
        Func<IReadOnlySet<string>, IReadOnlyList<NetworkInterfaceDescriptor>>? interfaceReader = null,
        IMdnsPublisher? publisher = null,
        string? trustedInterfaceId = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(pairing);
        ArgumentNullException.ThrowIfNull(deviceTokens);
        ArgumentNullException.ThrowIfNull(pairedDevices);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        _identity = identity;
        _pairing = pairing;
        _deviceTokens = deviceTokens;
        _pairedDevices = pairedDevices;
        _history = history;
        _store = store;
        _bridgeVersion = bridgeVersion;
        _logger = logger;
        _interfaceReader = interfaceReader ?? WindowsNetworkInterfaceReader.Read;
        _publisher = publisher ?? new MdnsPublisher();

        if (!string.IsNullOrWhiteSpace(trustedInterfaceId))
        {
            _trustedInterfaces.Add(trustedInterfaceId);
            _preferredInterfaceId = trustedInterfaceId;
        }
    }

    /// <summary>The address a paired phone should connect to, or <c>null</c> when there is none.</summary>
    public string? EndpointHost { get; private set; }

    /// <summary>The port the API is listening on, or <c>0</c> when it is not.</summary>
    public int EndpointPort { get; private set; }

    /// <summary>
    /// The WebSocket hub, or <c>null</c> while no endpoint is being served. The quota publisher
    /// streams updates through it.
    /// </summary>
    public QuotaWebSocketHub? Hub => _hub;

    /// <summary>The diagnostic to show when the Bridge is not reachable over the LAN.</summary>
    public BridgeDiagnostic? Diagnostic { get; private set; }

    /// <summary>Raised after every re-evaluation, so the UI can refresh the pairing QR code.</summary>
    public event Action? EndpointChanged;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            throw new InvalidOperationException("The LAN API host has already been started.");
        }

        _started = true;

        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;

        await EvaluateAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;

        // 1. Stop new pairings first: a phone that starts pairing now would be pairing with a Bridge
        //    that is about to disappear.
        _pairing.CloseForNewSessions();

        // 2. Tell connected clients, with a bounded wait, so a drop is not mistaken for a fault.
        if (_hub is not null)
        {
            await _hub.BroadcastShutdownAsync(cancellationToken).ConfigureAwait(false);
        }

        // 3. Withdraw discovery before the listener goes away, so nobody is sent to a dead address.
        if (_coordinator is not null)
        {
            await _coordinator.DisposeAsync().ConfigureAwait(false);
            _coordinator = null;
        }

        // 4. Stop the listener.
        var api = Interlocked.Exchange(ref _api, null);

        if (api is not null)
        {
            await api.StopAsync(cancellationToken).ConfigureAwait(false);
            await api.DisposeAsync().ConfigureAwait(false);
        }

        if (_hub is not null)
        {
            await _hub.DisposeAsync().ConfigureAwait(false);
            _hub = null;
        }

        EndpointHost = null;
        EndpointPort = 0;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _gate.Dispose();
    }

    /// <summary>
    /// Chooses the interface to use. The choice is also what marks it trusted, because the Windows
    /// network profile cannot be read safely and the user asserting which of their networks the Bridge
    /// belongs on is the next best authority. The adapter's <em>kind</em> is still enforced.
    /// </summary>
    public void SelectInterface(string? interfaceId)
    {
        lock (_trustedInterfaces)
        {
            _preferredInterfaceId = interfaceId;

            if (!string.IsNullOrWhiteSpace(interfaceId))
            {
                _trustedInterfaces.Add(interfaceId);
            }
        }

        _ = Task.Run(() => EvaluateAsync(CancellationToken.None));
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        // Coalesce: a single cable move produces a burst of address changes, and re-evaluating on each
        // one would reissue the leaf repeatedly for no reason.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(NetworkChangeDebounce).ConfigureAwait(false);
                await EvaluateAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Re-evaluating the LAN endpoint after a network change failed.");
            }
        });
    }

    private async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            IReadOnlySet<string> trusted;

            lock (_trustedInterfaces)
            {
                trusted = new HashSet<string>(_trustedInterfaces, StringComparer.Ordinal);
            }

            var selection = _selector.Select(_interfaceReader(trusted), _preferredInterfaceId);

            _coordinator ??= new EndpointLifecycleCoordinator(_identity, _publisher, _bridgeVersion);

            // The port is only known once Kestrel is listening, so the first evaluation advertises the
            // configured port and every later one advertises the real one.
            var port = _api is null ? BridgeEndpointOptions.DefaultPort : EndpointPort;

            var result = await _coordinator
                .ApplyAsync(selection, port, DateTimeOffset.UtcNow, cancellationToken)
                .ConfigureAwait(false);

            Diagnostic = result.Diagnostic;

            if (!selection.HasEndpoint)
            {
                // Nothing eligible: stop serving rather than keep a listener on an address the policy
                // no longer allows.
                await StopApiAsync(cancellationToken).ConfigureAwait(false);
                EndpointHost = null;
                EndpointPort = 0;
                EndpointChanged?.Invoke();
                return;
            }

            if (_api is null)
            {
                await StartApiAsync(selection, cancellationToken).ConfigureAwait(false);
            }

            EndpointHost = selection.Address!.ToString();
            EndpointChanged?.Invoke();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartApiAsync(LanSelection selection, CancellationToken cancellationToken)
    {
        var options = new BridgeEndpointOptions(
            selection.Address!,
            BridgeEndpointOptions.DefaultPort,
            _bridgeVersion);

        var hub = new QuotaWebSocketHub(_store, options);

        var api = BridgeApiHost.Create(
            options,
            _coordinator!.CurrentLeaf!,
            services =>
            {
                services.AddSingleton(_store);
                services.AddSingleton(_history);
                services.AddSingleton(_deviceTokens);
                services.AddSingleton(_pairedDevices);
                services.AddSingleton(_pairing);
                services.AddSingleton(hub);
            },
            endpoints =>
            {
                endpoints.MapHealthEndpoints();
                endpoints.MapQuotaEndpoints();
                endpoints.MapHistoryEndpoints();
                endpoints.MapPairingEndpoints();
                QuotaWebSocketHub.MapQuotaWebSocket(endpoints);
            });

        await api.StartAsync(cancellationToken).ConfigureAwait(false);

        _api = api;
        _hub = hub;

        // The listener may have been given a different port than the one that was asked for.
        EndpointPort = ResolvePort(api.Addresses) ?? options.Port;

        _logger.LogInformation(
            "Bridge API listening on {Host}:{Port}.",
            selection.Address,
            EndpointPort);
    }

    private async Task StopApiAsync(CancellationToken cancellationToken)
    {
        var api = Interlocked.Exchange(ref _api, null);

        if (api is null)
        {
            return;
        }

        await api.StopAsync(cancellationToken).ConfigureAwait(false);
        await api.DisposeAsync().ConfigureAwait(false);

        if (_hub is not null)
        {
            await _hub.DisposeAsync().ConfigureAwait(false);
            _hub = null;
        }
    }

    private static int? ResolvePort(IReadOnlyList<string> addresses)
    {
        foreach (var address in addresses)
        {
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Port > 0)
            {
                return uri.Port;
            }
        }

        return null;
    }
}
