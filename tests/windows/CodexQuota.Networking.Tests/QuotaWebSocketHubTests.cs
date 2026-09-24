using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using CodexQuota.Core.Quota;
using CodexQuota.Networking.Api.V1;
using CodexQuota.Networking.Auth;
using CodexQuota.Networking.Contracts.V1;
using CodexQuota.Networking.Hosting;
using CodexQuota.Networking.Pairing;
using CodexQuota.Networking.Security;
using CodexQuota.Networking.WebSockets;
using CodexQuota.Storage.Devices;
using CodexQuota.Storage.History;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace CodexQuota.Networking.Tests;

/// <summary>
/// The authenticated WebSocket. Two properties carry the design: the client is told the protocol
/// version before any state, and every update carries the whole snapshot with a monotonic sequence
/// so a gap is detectable.
/// </summary>
public class QuotaWebSocketHubTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task AnUnauthenticatedUpgradeIsRejected()
    {
        await using var api = await WebSocketHarness.StartAsync();

        using var socket = api.CreateSocket(token: null);

        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => socket.ConnectAsync(api.WebSocketUri, CancellationToken.None));

        Assert.Contains("401", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARevokedCredentialCannotUpgrade()
    {
        await using var api = await WebSocketHarness.StartAsync();
        var credential = await api.IssueCredentialAsync();
        await api.RevokeAsync(credential.DeviceId);

        using var socket = api.CreateSocket(credential.Token);

        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => socket.ConnectAsync(api.WebSocketUri, CancellationToken.None));

        Assert.Contains("401", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelloIsAlwaysTheFirstFrame()
    {
        await using var api = await WebSocketHarness.StartAsync();
        var credential = await api.IssueCredentialAsync();

        // A snapshot already exists, so a hello that were sent late would be preceded by it.
        api.Store.Replace(TestSnapshots.Snapshot(72, 54));

        using var socket = api.CreateSocket(credential.Token);
        await socket.ConnectAsync(api.WebSocketUri, CancellationToken.None);

        var hello = await api.ReadAsync(socket);

        Assert.Equal("hello", hello.GetProperty("type").GetString());
        Assert.Equal("v1", hello.GetProperty("apiVersion").GetString());
        Assert.Equal("1.0.0-test", hello.GetProperty("bridgeVersion").GetString());
    }

    [Fact]
    public async Task ConnectingMidStreamStartsWithTheCurrentSnapshot()
    {
        await using var api = await WebSocketHarness.StartAsync();
        var credential = await api.IssueCredentialAsync();
        api.Store.Replace(TestSnapshots.Snapshot(72, 54));

        using var socket = api.CreateSocket(credential.Token);
        await socket.ConnectAsync(api.WebSocketUri, CancellationToken.None);

        await api.ReadAsync(socket);
        var first = await api.ReadAsync(socket);

        Assert.Equal("quota.updated", first.GetProperty("type").GetString());
        Assert.Equal(1L, first.GetProperty("sequence").GetInt64());
    }

    [Fact]
    public async Task ThreeStateUpdatesCarrySequencesOneTwoThree()
    {
        await using var api = await WebSocketHarness.StartAsync();
        var credential = await api.IssueCredentialAsync();

        using var socket = api.CreateSocket(credential.Token);
        await socket.ConnectAsync(api.WebSocketUri, CancellationToken.None);
        await api.ReadAsync(socket);

        for (var index = 1; index <= 3; index++)
        {
            await api.Hub.BroadcastQuotaUpdatedAsync(api.Store.Replace(TestSnapshots.Snapshot(90 - index, 50)));
        }

        for (var expected = 1L; expected <= 3; expected++)
        {
            var message = await api.ReadAsync(socket);

            Assert.Equal("quota.updated", message.GetProperty("type").GetString());
            Assert.Equal(expected, message.GetProperty("sequence").GetInt64());
        }
    }

    [Fact]
    public async Task AnUpdateCarriesTheCompleteSnapshotRatherThanADelta()
    {
        await using var api = await WebSocketHarness.StartAsync();
        var credential = await api.IssueCredentialAsync();

        using var socket = api.CreateSocket(credential.Token);
        await socket.ConnectAsync(api.WebSocketUri, CancellationToken.None);
        await api.ReadAsync(socket);

        await api.Hub.BroadcastQuotaUpdatedAsync(api.Store.Replace(TestSnapshots.Snapshot(72, 54)));

        var payload = (await api.ReadAsync(socket)).GetProperty("payload");

        // A client that missed a frame must be able to resynchronise from any single one.
        Assert.Equal(1, payload.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("codex_app_server", payload.GetProperty("source").GetString());
        Assert.Equal("online", payload.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("generatedAt").GetString()));

        var shortWindow = payload.GetProperty("windows").GetProperty("shortWindow");

        Assert.Equal(72d, shortWindow.GetProperty("remainingPercent").GetDouble(), 0.0001);
        Assert.Equal(28d, shortWindow.GetProperty("usedPercent").GetDouble(), 0.0001);
        Assert.Equal(300, shortWindow.GetProperty("windowMinutes").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(shortWindow.GetProperty("resetsAt").GetString()));

        var weekly = payload.GetProperty("windows").GetProperty("weekly");

        Assert.Equal(54d, weekly.GetProperty("remainingPercent").GetDouble(), 0.0001);
        Assert.Equal(10080, weekly.GetProperty("windowMinutes").GetInt32());
    }

    [Fact]
    public async Task AClientFrameDoesNotEndTheConnection()
    {
        await using var api = await WebSocketHarness.StartAsync();
        var credential = await api.IssueCredentialAsync();

        using var socket = api.CreateSocket(credential.Token);
        await socket.ConnectAsync(api.WebSocketUri, CancellationToken.None);
        await api.ReadAsync(socket);

        // A heartbeat or any stray frame is read and discarded; it must not stall the stream.
        await socket.SendAsync(
            Encoding.UTF8.GetBytes("""{"type":"ping"}"""),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None);

        await api.Hub.BroadcastQuotaUpdatedAsync(api.Store.Replace(TestSnapshots.Snapshot(70, 50)));

        var message = await api.ReadAsync(socket);

        Assert.Equal("quota.updated", message.GetProperty("type").GetString());
        Assert.Equal(1L, message.GetProperty("sequence").GetInt64());
        Assert.Equal(WebSocketState.Open, socket.State);
    }

    [Fact]
    public async Task SeveralClientsAllReceiveTheSameUpdate()
    {
        await using var api = await WebSocketHarness.StartAsync();
        var credential = await api.IssueCredentialAsync();

        using var first = api.CreateSocket(credential.Token);
        using var second = api.CreateSocket(credential.Token);

        await first.ConnectAsync(api.WebSocketUri, CancellationToken.None);
        await second.ConnectAsync(api.WebSocketUri, CancellationToken.None);
        await api.ReadAsync(first);
        await api.ReadAsync(second);

        await api.Hub.BroadcastQuotaUpdatedAsync(api.Store.Replace(TestSnapshots.Snapshot(70, 50)));

        // One broadcast, one sequence number, delivered identically to every client.
        var firstMessage = await api.ReadAsync(first);
        var secondMessage = await api.ReadAsync(second);

        Assert.Equal(1L, firstMessage.GetProperty("sequence").GetInt64());
        Assert.Equal(1L, secondMessage.GetProperty("sequence").GetInt64());
        Assert.Equal(
            firstMessage.GetProperty("payload").GetRawText(),
            secondMessage.GetProperty("payload").GetRawText());
    }

    [Fact]
    public async Task AShutdownFrameIsDeliveredBeforeTheConnectionCloses()
    {
        await using var api = await WebSocketHarness.StartAsync();
        var credential = await api.IssueCredentialAsync();

        using var socket = api.CreateSocket(credential.Token);
        await socket.ConnectAsync(api.WebSocketUri, CancellationToken.None);
        await api.ReadAsync(socket);

        await api.Hub.BroadcastShutdownAsync(CancellationToken.None);

        var message = await api.ReadAsync(socket);

        Assert.Equal("bridge.shutdown", message.GetProperty("type").GetString());
    }

    [Fact]
    public async Task ADisconnectedClientIsRemovedFromTheHub()
    {
        await using var api = await WebSocketHarness.StartAsync();
        var credential = await api.IssueCredentialAsync();

        var socket = api.CreateSocket(credential.Token);
        await socket.ConnectAsync(api.WebSocketUri, CancellationToken.None);
        await api.ReadAsync(socket);

        await WaitUntilAsync(() => api.Hub.ConnectionCount == 1, Timeout);

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        socket.Dispose();

        // A leaked subscriber would keep receiving broadcasts for the life of the Bridge.
        await WaitUntilAsync(() => api.Hub.ConnectionCount == 0, Timeout);

        Assert.Equal(0, api.Hub.ConnectionCount);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);

        while (!condition())
        {
            await Task.Delay(10, deadline.Token);
        }
    }
}

/// <summary>A running Bridge with the WebSocket mapped, plus a pinning client factory.</summary>
internal sealed class WebSocketHarness : IAsyncDisposable
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(20);

    private readonly TestIdentityMaterial _identityMaterial;
    private readonly BridgeIdentity _identity;
    private readonly X509Certificate2 _leaf;
    private readonly BridgeApiHost _host;
    private readonly DeviceTokenService _tokens;

    private WebSocketHarness(
        TestIdentityMaterial identityMaterial,
        BridgeIdentity identity,
        X509Certificate2 leaf,
        BridgeApiHost host,
        DeviceTokenService tokens,
        InMemoryQuotaStateStore store,
        QuotaWebSocketHub hub,
        string spki)
    {
        _identityMaterial = identityMaterial;
        _identity = identity;
        _leaf = leaf;
        _host = host;
        _tokens = tokens;

        Store = store;
        Hub = hub;
        LeafSpki = spki;
        WebSocketUri = new Uri($"{host.Addresses[0].Replace("https", "wss")}/api/v1/ws");
    }

    internal InMemoryQuotaStateStore Store { get; }

    internal QuotaWebSocketHub Hub { get; }

    internal Uri WebSocketUri { get; }

    internal string LeafSpki { get; }

    internal static async Task<WebSocketHarness> StartAsync()
    {
        var identityMaterial = TestCertificates.CreateIdentity("bridge-1");
        var identity = new BridgeIdentity(identityMaterial.BridgeId, identityMaterial.Certificate);

        var leaf = LeafCertificateFactory.CreateServerCertificate(
            identity,
            [IPAddress.Loopback],
            ["localhost"],
            DateTimeOffset.UtcNow);

        var store = new InMemoryQuotaStateStore();
        var devices = new InMemoryPairedDeviceRepository();
        var tokens = new DeviceTokenService(devices);
        var history = new RecordingHistoryRepository();
        var runtime = new CodexQuota.Core.Runtime.BridgeRuntimeState();

        var options = BridgeEndpointOptions.Loopback("1.0.0-test");

        QuotaWebSocketHub? hub = null;

        var host = BridgeApiHost.Create(
            options,
            leaf,
            services =>
            {
                services.AddSingleton<IQuotaStateStore>(store);
                services.AddSingleton(runtime);
                services.AddSingleton<IHistoryRepository>(history);
                services.AddSingleton(devices);
                services.AddSingleton(tokens);
                services.AddSingleton(new PairingService(tokens));
                services.AddSingleton(serviceProvider => new QuotaWebSocketHub(store, options));
            },
            endpoints =>
            {
                endpoints.MapHealthEndpoints();
                endpoints.MapQuotaEndpoints();
                QuotaWebSocketHub.MapQuotaWebSocket(endpoints);
            });

        await host.StartAsync(CancellationToken.None);

        hub = host.Services.GetRequiredService<QuotaWebSocketHub>();

        return new WebSocketHarness(
            identityMaterial,
            identity,
            leaf,
            host,
            tokens,
            store,
            hub,
            CertificateFingerprint.ComputeSpkiSha256(leaf));
    }

    internal ClientWebSocket CreateSocket(string? token)
    {
        var socket = new ClientWebSocket();

        // Pin the leaf that was issued. This is a real check rather than a bypass: any other
        // certificate, self-signed or not, fails it.
        socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            certificate is X509Certificate2 presented
            && CertificateFingerprint.ComputeSpkiSha256(presented) == LeafSpki;

        if (token is not null)
        {
            socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
        }

        return socket;
    }

    internal Task<DeviceCredential> IssueCredentialAsync()
        => _tokens.IssueAsync("Find X8", DateTimeOffset.UtcNow, CancellationToken.None);

    internal Task<bool> RevokeAsync(string deviceId)
        => _tokens.RevokeAsync(deviceId, DateTimeOffset.UtcNow, CancellationToken.None);

    /// <summary>Reads exactly one complete text frame and parses it.</summary>
    internal async Task<JsonElement> ReadAsync(ClientWebSocket socket)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();

        while (true)
        {
            using var timeout = new CancellationTokenSource(ReceiveTimeout);

            var result = await socket
                .ReceiveAsync(buffer, timeout.Token);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("The server closed the connection before sending a frame.");
            }

            stream.Write(buffer, 0, result.Count);

            if (result.EndOfMessage)
            {
                break;
            }
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        await Hub.DisposeAsync();
        await _host.DisposeAsync();

        _leaf.Dispose();
        _identity.Dispose();
        _identityMaterial.Dispose();
    }
}
