using System.Security.Cryptography.X509Certificates;
using CodexQuota.Networking.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace CodexQuota.Networking.Hosting;

/// <summary>
/// The Bridge's embedded HTTPS API: Kestrel hosted inside the desktop process, bound to exactly one
/// address and presenting the current leaf certificate.
/// </summary>
/// <remarks>
/// <para>
/// There is deliberately no way to start this host without a certificate, and no <c>ListenAnyIP</c>
/// anywhere: the Bridge binds one explicitly chosen address, which is what keeps a public, VPN or
/// virtual adapter from silently becoming the API endpoint.
/// </para>
/// <para>
/// The default server URL is cleared before Kestrel is configured, so an environment variable or a
/// config file cannot add a second — possibly cleartext — listener behind the Bridge's back.
/// </para>
/// </remarks>
public sealed class BridgeApiHost : IAsyncDisposable
{
    private readonly WebApplication _application;

    private BridgeApiHost(WebApplication application)
    {
        _application = application;
    }

    /// <summary>The addresses Kestrel actually bound. Populated once the host has started.</summary>
    public IReadOnlyList<string> Addresses { get; private set; } = [];

    /// <summary>The host's service provider, for resolving endpoints and diagnostics.</summary>
    public IServiceProvider Services => _application.Services;

    /// <summary>
    /// Builds the host. Endpoint mapping is supplied by the caller so this type owns only the
    /// transport, the security boundary and the lifecycle.
    /// </summary>
    public static BridgeApiHost Create(
        BridgeEndpointOptions options,
        X509Certificate2 leafCertificate,
        Action<IServiceCollection> configureServices,
        Action<WebApplication> mapEndpoints)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(leafCertificate);
        ArgumentNullException.ThrowIfNull(configureServices);
        ArgumentNullException.ThrowIfNull(mapEndpoints);

        var builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(
                options.Address,
                options.Port,
                listen => listen.UseHttps(leafCertificate)));

        // The endpoint options are part of the API surface, not of the transport: /info and the
        // WebSocket hello both report the versions they carry.
        builder.Services.AddSingleton(options);

        configureServices(builder.Services);

        var application = builder.Build();

        // Enables the WebSocket feature. It only makes the upgrade available: the endpoint still has
        // to ask for it, and the authentication middleware still runs first, so an unauthenticated
        // client can never reach AcceptWebSocketAsync.
        application.UseWebSockets();

        application.UseRouting();
        application.UseDeviceAuthentication();

        mapEndpoints(application);

        return new BridgeApiHost(application);
    }

    /// <summary>Starts listening and records the bound addresses.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _application.StartAsync(cancellationToken).ConfigureAwait(false);

        Addresses = _application.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()?
            .Addresses
            .ToArray() ?? [];
    }

    /// <summary>Stops accepting new work. Existing connections are closed.</summary>
    public Task StopAsync(CancellationToken cancellationToken)
        => _application.StopAsync(cancellationToken);

    public async ValueTask DisposeAsync() => await _application.DisposeAsync().ConfigureAwait(false);
}
