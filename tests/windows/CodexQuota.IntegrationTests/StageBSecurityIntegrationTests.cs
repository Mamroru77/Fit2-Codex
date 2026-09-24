using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using CodexQuota.Core.Quota;
using CodexQuota.Core.Runtime;
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
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace CodexQuota.IntegrationTests;

/// <summary>
/// The Stage B security gate: the whole path from "no credential" to "credential works", plus the one
/// property everything else rests on — the device credential is never sent to a Bridge whose identity
/// has changed.
/// </summary>
public class StageBSecurityIntegrationTests
{
    [Fact]
    public async Task AnonymousAccessToQuotaIsRejected()
    {
        await using var bridge = await BridgeSession.StartAsync();

        using var response = await bridge.GetAsync("/api/v1/quota", token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(ApiErrorCodes.DeviceUnauthorized, (await ReadErrorAsync(response)).Error.Code);
    }

    [Fact]
    public async Task EveryAccountBearingRouteIsClosedToAnUnpairedDevice()
    {
        await using var bridge = await BridgeSession.StartAsync();

        foreach (var path in new[] { "/api/v1/quota", "/api/v1/info", "/api/v1/history", "/api/v1/events" })
        {
            using var response = await bridge.GetAsync(path, token: null);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task DiscoveryPairingCannotCompleteBeforeLocalApproval()
    {
        await using var bridge = await BridgeSession.StartAsync();

        var session = await bridge.PostAsync<PairingSessionResponse>(
            "/api/v1/pairing/request",
            new { displayName = "Find X8" },
            token: null);

        Assert.Equal("awaiting_local_approval", session.Status);

        using var response = await bridge.PostAsync("/api/v1/pairing/complete", new { pairingId = session.PairingId }, null);

        Assert.Equal(ApiErrorCodes.PairingInvalid, (await ReadErrorAsync(response)).Error.Code);
        Assert.Empty(bridge.Devices.Devices);
    }

    [Fact]
    public async Task QrClaimCannotCompleteBeforeLocalApproval()
    {
        await using var bridge = await BridgeSession.StartAsync();

        var qr = bridge.Pairing.CreateSession(PairingOrigin.QrCode, null, DateTimeOffset.UtcNow);

        var claimed = await bridge.PostAsync<PairingSessionResponse>(
            "/api/v1/pairing/claim",
            new { pairingId = qr.PairingId, displayName = "Find X8" },
            token: null);

        Assert.Equal("awaiting_local_approval", claimed.Status);

        using var response = await bridge.PostAsync("/api/v1/pairing/complete", new { pairingId = qr.PairingId }, null);

        Assert.Equal(ApiErrorCodes.PairingInvalid, (await ReadErrorAsync(response)).Error.Code);
    }

    [Fact]
    public async Task ARejectedPairingCannotComplete()
    {
        await using var bridge = await BridgeSession.StartAsync();

        var session = await bridge.PostAsync<PairingSessionResponse>(
            "/api/v1/pairing/request",
            new { displayName = "Find X8" },
            token: null);

        bridge.Pairing.RejectLocally(session.PairingId, DateTimeOffset.UtcNow);

        using var response = await bridge.PostAsync("/api/v1/pairing/complete", new { pairingId = session.PairingId }, null);

        Assert.Equal(ApiErrorCodes.PairingInvalid, (await ReadErrorAsync(response)).Error.Code);
        Assert.Empty(bridge.Devices.Devices);
    }

    [Fact]
    public async Task AnApprovedPairingIssuesACredentialThatWorksEverywhere()
    {
        await using var bridge = await BridgeSession.StartAsync();

        var session = await bridge.PostAsync<PairingSessionResponse>(
            "/api/v1/pairing/request",
            new { displayName = "Find X8" },
            token: null);

        bridge.Pairing.ApproveLocally(session.PairingId, DateTimeOffset.UtcNow);

        var credential = await bridge.PostAsync<DeviceCredentialResponse>(
            "/api/v1/pairing/complete",
            new { pairingId = session.PairingId },
            token: null);

        Assert.False(string.IsNullOrWhiteSpace(credential.DeviceId));
        Assert.True(credential.Token.Length >= 64, "A 48-byte credential is at least 64 base64url characters.");

        bridge.Store.Replace(Snapshot(72, 54));

        foreach (var path in new[] { "/api/v1/quota", "/api/v1/info", "/api/v1/history?hours=24", "/api/v1/events?hours=24" })
        {
            using var response = await bridge.GetAsync(path, credential.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // The stored record holds a hash, never the credential itself.
        var stored = Assert.Single(bridge.Devices.Devices);
        Assert.NotEqual(credential.Token, stored.TokenHash);
        Assert.DoesNotContain(credential.Token, stored.TokenHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReusedPairingIsRejected()
    {
        await using var bridge = await BridgeSession.StartAsync();

        var session = await bridge.PostAsync<PairingSessionResponse>(
            "/api/v1/pairing/request",
            new { displayName = "Find X8" },
            token: null);

        bridge.Pairing.ApproveLocally(session.PairingId, DateTimeOffset.UtcNow);

        var first = await bridge.PostAsync<DeviceCredentialResponse>(
            "/api/v1/pairing/complete",
            new { pairingId = session.PairingId },
            token: null);

        Assert.False(string.IsNullOrWhiteSpace(first.Token));

        using var second = await bridge.PostAsync("/api/v1/pairing/complete", new { pairingId = session.PairingId }, null);

        Assert.Equal(ApiErrorCodes.PairingInvalid, (await ReadErrorAsync(second)).Error.Code);

        // One pairing, one device.
        Assert.Single(bridge.Devices.Devices);
    }

    [Fact]
    public async Task AnExpiredPairingIsRejected()
    {
        await using var bridge = await BridgeSession.StartAsync();

        // Created five minutes in the past, so it is already out of time.
        var expiredAt = DateTimeOffset.UtcNow - PairingService.SessionLifetime;
        var session = bridge.Pairing.CreateSession(PairingOrigin.Discovery, "Find X8", expiredAt);
        bridge.Pairing.ApproveLocally(session.PairingId, expiredAt);

        using var response = await bridge.PostAsync("/api/v1/pairing/complete", new { pairingId = session.PairingId }, null);

        Assert.Equal(ApiErrorCodes.PairingExpired, (await ReadErrorAsync(response)).Error.Code);
        Assert.Empty(bridge.Devices.Devices);
    }

    [Fact]
    public async Task ARevokedCredentialIsRejectedOnEverySurface()
    {
        await using var bridge = await BridgeSession.StartAsync();
        var credential = await bridge.IssueCredentialAsync();

        // Seed a snapshot so the pre-revocation 200 is about authentication, not about data.
        bridge.Store.Replace(Snapshot(72, 54));

        Assert.Equal(HttpStatusCode.OK, (await bridge.GetAsync("/api/v1/quota", credential.Token)).StatusCode);

        await bridge.RevokeAsync(credential.DeviceId);

        foreach (var path in new[] { "/api/v1/quota", "/api/v1/info", "/api/v1/history", "/api/v1/events" })
        {
            using var response = await bridge.GetAsync(path, credential.Token);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task TheWebSocketRequiresACredentialAndAcceptsAValidOne()
    {
        await using var bridge = await BridgeSession.StartAsync();
        var credential = await bridge.IssueCredentialAsync();

        using var anonymous = bridge.CreateSocket(token: null);

        var rejected = await Assert.ThrowsAnyAsync<Exception>(
            () => anonymous.ConnectAsync(bridge.WebSocketUri, CancellationToken.None));

        Assert.Contains("401", rejected.Message, StringComparison.Ordinal);

        using var authorized = bridge.CreateSocket(credential.Token);
        await authorized.ConnectAsync(bridge.WebSocketUri, CancellationToken.None);

        var hello = await bridge.ReadAsync(authorized);

        Assert.Equal("hello", hello.GetProperty("type").GetString());
    }

    [Fact]
    public async Task ARotatedLeafKeepsTheSamePairedClientWorking()
    {
        var material = IntegrationCertificates.CreateIdentity("bridge-1");
        using var identity = new BridgeIdentity(material.BridgeId, material.Certificate);

        var devices = new InMemoryPairedDeviceRepository();
        var tokens = new DeviceTokenService(devices);
        var pairing = new PairingService(tokens);

        DeviceCredential credential;
        string firstLeafSpki;

        // First run: the leaf is issued for 192.168.1.23.
        await using (var first = await BridgeSession.StartAsync(identity, devices, tokens, pairing, "192.168.1.23"))
        {
            credential = await first.IssueCredentialAsync();
            firstLeafSpki = CertificateFingerprint.ComputeSpkiSha256(first.Leaf);

            using var response = await first.GetAsync("/api/v1/info", credential.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // The PC moved, so a different leaf was issued. The identity did not change.
        await using var second = await BridgeSession.StartAsync(identity, devices, tokens, pairing, "192.168.1.87");

        Assert.NotEqual(firstLeafSpki, CertificateFingerprint.ComputeSpkiSha256(second.Leaf));

        // The client pins the identity, so it still works: no re-pairing after an address change.
        using var afterMove = await second.GetAsync("/api/v1/info", credential.Token);
        Assert.Equal(HttpStatusCode.OK, afterMove.StatusCode);

        // And the pin the client holds is unchanged.
        Assert.Equal(
            identity.SpkiSha256,
            CertificateFingerprint.ComputeSpkiSha256(identity.Certificate));

        material.Dispose();
    }

    [Fact]
    public async Task AChangedBridgeIdentityIsRefusedBeforeAnyCredentialIsSent()
    {
        var materialA = IntegrationCertificates.CreateIdentity("bridge-a");
        var materialB = IntegrationCertificates.CreateIdentity("bridge-b");

        using var identityA = new BridgeIdentity(materialA.BridgeId, materialA.Certificate);
        using var identityB = new BridgeIdentity(materialB.BridgeId, materialB.Certificate);

        var devices = new InMemoryPairedDeviceRepository();
        var tokens = new DeviceTokenService(devices);
        var pairing = new PairingService(tokens);

        DeviceCredential credential;

        await using (var a = await BridgeSession.StartAsync(identityA, devices, tokens, pairing, "192.168.1.23"))
        {
            credential = await a.IssueCredentialAsync();

            using var ok = await a.GetAsync("/api/v1/probe", credential.Token);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

            // The probe is anonymous, so reaching it proves the credential really was transmitted.
            // Without this the next assertion would pass for the wrong reason.
            Assert.Contains(a.ObservedAuthorizationHeaders, header => header.Contains(credential.Token, StringComparison.Ordinal));
        }

        // The same phone now meets a Bridge whose identity has been replaced.
        await using var b = await BridgeSession.StartAsync(identityB, devices, tokens, pairing, "192.168.1.23");

        var refused = await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => b.GetAsync("/api/v1/probe", credential.Token, pinnedIdentity: identityA.Certificate));

        Assert.NotNull(refused);

        // The decisive assertion. The pin failed during the TLS handshake, so the credential never
        // left the client. A client that sent first and verified afterwards would already have handed
        // the credential to an impostor.
        Assert.Empty(b.ObservedAuthorizationHeaders);

        materialA.Dispose();
        materialB.Dispose();
    }

    private static QuotaSnapshot Snapshot(double shortRemaining, double weeklyRemaining)
    {
        var generatedAt = DateTimeOffset.UtcNow;
        var resetsAt = generatedAt.AddHours(5);

        return new QuotaSnapshot(
            SchemaVersion: 1,
            GeneratedAt: generatedAt,
            Source: V1ContractMapper.CodexAppServerSource,
            Status: QuotaSourceStatus.Online,
            LastSuccessfulSyncAt: generatedAt,
            ShortWindow: new QuotaWindow(100d - shortRemaining, shortRemaining, 300, resetsAt),
            Weekly: new QuotaWindow(100d - weeklyRemaining, weeklyRemaining, 10080, resetsAt.AddDays(6)));
    }

    private static async Task<ApiErrorResponse> ReadErrorAsync(HttpResponseMessage response)
        => JsonSerializer.Deserialize<ApiErrorResponse>(await response.Content.ReadAsStringAsync(), V1Json.Options)!;
}

/// <summary>One running Bridge, plus clients that pin an identity rather than bypassing validation.</summary>
internal sealed class BridgeSession : IAsyncDisposable
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(20);

    private readonly BridgeApiHost _host;
    private readonly TestIdentityMaterial? _ownedMaterial;
    private readonly List<HttpClient> _clients = [];
    private readonly List<string> _observed;

    private BridgeSession(
        BridgeApiHost host,
        X509Certificate2 leaf,
        BridgeIdentity identity,
        TestIdentityMaterial? ownedMaterial,
        InMemoryPairedDeviceRepository devices,
        DeviceTokenService tokens,
        PairingService pairing,
        InMemoryQuotaStateStore store,
        string address,
        List<string> observed)
    {
        _host = host;
        _ownedMaterial = ownedMaterial;
        _observed = observed;

        Leaf = leaf;
        Identity = identity;
        Devices = devices;
        Tokens = tokens;
        Pairing = pairing;
        Store = store;

        WebSocketUri = new Uri($"{address.Replace("https", "wss")}/api/v1/ws");
        BaseAddress = new Uri(address);
    }

    internal Uri BaseAddress { get; }

    internal X509Certificate2 Leaf { get; }

    internal BridgeIdentity Identity { get; }

    internal InMemoryPairedDeviceRepository Devices { get; }

    internal DeviceTokenService Tokens { get; }

    internal PairingService Pairing { get; }

    internal InMemoryQuotaStateStore Store { get; }

    internal Uri WebSocketUri { get; }

    /// <summary>
    /// Authorization headers the server actually received, captured by an anonymous probe route.
    /// This is the instrument that proves a credential was — or was not — transmitted.
    /// </summary>
    internal IReadOnlyList<string> ObservedAuthorizationHeaders
    {
        get
        {
            lock (_observed)
            {
                return _observed.ToArray();
            }
        }
    }

    internal static Task<BridgeSession> StartAsync()
    {
        var material = IntegrationCertificates.CreateIdentity("bridge-1");
        var identity = new BridgeIdentity(material.BridgeId, material.Certificate);

        return StartCoreAsync(identity, material, null, null, null, "192.168.1.23");
    }

    internal static Task<BridgeSession> StartAsync(
        BridgeIdentity identity,
        InMemoryPairedDeviceRepository devices,
        DeviceTokenService tokens,
        PairingService pairing,
        string address)
        => StartCoreAsync(identity, null, devices, tokens, pairing, address);

    private static async Task<BridgeSession> StartCoreAsync(
        BridgeIdentity identity,
        TestIdentityMaterial? ownedMaterial,
        InMemoryPairedDeviceRepository? devices,
        DeviceTokenService? tokens,
        PairingService? pairing,
        string address)
    {
        var leaf = LeafCertificateFactory.CreateServerCertificate(
            identity,
            [IPAddress.Parse(address)],
            ["localhost", identity.DnsName],
            DateTimeOffset.UtcNow);

        var repository = devices ?? new InMemoryPairedDeviceRepository();
        var tokenService = tokens ?? new DeviceTokenService(repository);
        var pairingService = pairing ?? new PairingService(tokenService);
        var store = new InMemoryQuotaStateStore();
        var runtime = new BridgeRuntimeState();

        var options = new BridgeEndpointOptions(IPAddress.Loopback, 0, "1.0.0-test");
        var observed = new List<string>();

        var host = BridgeApiHost.Create(
            options,
            leaf,
            services =>
            {
                services.AddSingleton<IQuotaStateStore>(store);
                services.AddSingleton(runtime);
                services.AddSingleton<IHistoryRepository>(new EmptyHistoryRepository());
                services.AddSingleton(repository);
                services.AddSingleton(tokenService);
                services.AddSingleton(pairingService);
                services.AddSingleton(serviceProvider => new QuotaWebSocketHub(store, options));
            },
            endpoints =>
            {
                endpoints.MapHealthEndpoints();
                endpoints.MapQuotaEndpoints();
                endpoints.MapHistoryEndpoints();
                endpoints.MapPairingEndpoints();
                QuotaWebSocketHub.MapQuotaWebSocket(endpoints);

                // Anonymous, and used only to observe whether a credential reached the server.
                endpoints.MapGet("/api/v1/probe", (HttpContext context) =>
                {
                    lock (observed)
                    {
                        observed.Add(context.Request.Headers.Authorization.ToString());
                    }

                    return Results.Ok(new { observed = true });
                }).AllowAnonymous();
            });

        await host.StartAsync(CancellationToken.None);

        return new BridgeSession(
            host,
            leaf,
            identity,
            ownedMaterial,
            repository,
            tokenService,
            pairingService,
            store,
            host.Addresses[0],
            observed);
    }

    internal HttpClient CreateClient(X509Certificate2? pinnedIdentity = null)
    {
        var expected = pinnedIdentity ?? Identity.Certificate;

        var handler = new HttpClientHandler
        {
            // The leaf is verified as issued by the pinned identity, so a renewed leaf keeps working
            // while a replaced identity does not. This is a real check, not a bypass.
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is X509Certificate2 presented
                && BridgeIdentityPin.Verify(presented, expected),
        };

        var client = new HttpClient(handler) { BaseAddress = BaseAddress };
        _clients.Add(client);

        return client;
    }

    internal Task<DeviceCredential> IssueCredentialAsync()
        => Tokens.IssueAsync("Find X8", DateTimeOffset.UtcNow, CancellationToken.None);

    internal Task<bool> RevokeAsync(string deviceId)
        => Tokens.RevokeAsync(deviceId, DateTimeOffset.UtcNow, CancellationToken.None);

    internal Task<HttpResponseMessage> GetAsync(
        string path,
        string? token,
        X509Certificate2? pinnedIdentity = null)
        => CreateClient(pinnedIdentity).SendAsync(BridgeRequest.Get(path, token));

    internal async Task<T> PostAsync<T>(string path, object body, string? token)
    {
        using var response = await PostAsync(path, body, token);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<T>(V1Json.Options))!;
    }

    internal Task<HttpResponseMessage> PostAsync(string path, object body, string? token)
        => CreateClient().SendAsync(BridgeRequest.Post(path, body, token));

    internal System.Net.WebSockets.ClientWebSocket CreateSocket(string? token)
    {
        var socket = new System.Net.WebSockets.ClientWebSocket();

        socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            certificate is X509Certificate2 presented
            && BridgeIdentityPin.Verify(presented, Identity.Certificate);

        if (token is not null)
        {
            socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
        }

        return socket;
    }

    internal async Task<JsonElement> ReadAsync(System.Net.WebSockets.ClientWebSocket socket)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();

        while (true)
        {
            using var timeout = new CancellationTokenSource(ReceiveTimeout);
            var result = await socket.ReceiveAsync(buffer, timeout.Token);

            if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
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
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        _clients.Clear();

        await _host.DisposeAsync();

        Leaf.Dispose();

        if (_ownedMaterial is not null)
        {
            Identity.Dispose();
            _ownedMaterial.Dispose();
        }
    }
}

/// <summary>Builds the HTTP requests the tests send, with the credential attached.</summary>
internal static class BridgeRequest
{
    internal static HttpRequestMessage Get(string path, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    internal static HttpRequestMessage Post(string path, object body, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, options: V1Json.Options),
        };

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }
}

/// <summary>A history repository that always answers with nothing, so quota is never blocked by it.</summary>
internal sealed class EmptyHistoryRepository : IHistoryRepository
{
    public Task<IReadOnlyList<HistoryPoint>> ReadHistoryAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<HistoryPoint>>([]);

    public Task<IReadOnlyList<QuotaEvent>> ReadEventsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<QuotaEvent>>([]);

    public Task AppendSnapshotAsync(QuotaStateUpdate update, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task AppendEventAsync(QuotaEvent quotaEvent, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

/// <summary>An in-memory device store that records exactly what it was handed.</summary>
internal sealed class InMemoryPairedDeviceRepository : IPairedDeviceRepository
{
    private readonly List<PairedDevice> _devices = [];
    private readonly object _gate = new();

    internal IReadOnlyList<PairedDevice> Devices
    {
        get
        {
            lock (_gate)
            {
                return _devices.ToArray();
            }
        }
    }

    public Task AppendAsync(PairedDevice device, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _devices.Add(device);
        }

        return Task.CompletedTask;
    }

    public Task<PairedDevice?> FindByDeviceIdAsync(string deviceId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_devices.FirstOrDefault(device => device.DeviceId == deviceId));
        }
    }

    public Task<IReadOnlyList<PairedDevice>> ListAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<PairedDevice>>(_devices.ToArray());
        }
    }

    public Task<bool> RevokeAsync(string deviceId, DateTimeOffset revokedAt, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var index = _devices.FindIndex(device => device.DeviceId == deviceId);

            if (index < 0)
            {
                return Task.FromResult(false);
            }

            _devices[index] = _devices[index] with { RevokedAt = revokedAt };
            return Task.FromResult(true);
        }
    }
}

/// <summary>
/// Verifies a presented TLS certificate against a pinned Bridge identity.
/// </summary>
/// <remarks>
/// The Bridge presents a short-lived leaf, never the identity itself, so pinning cannot mean "the
/// presented certificate equals the pinned one". It means "the presented certificate was issued by
/// the pinned identity", which is what lets a leaf rotate freely while a replaced identity is
/// refused. This is the check the Android client performs.
/// </remarks>
internal static class BridgeIdentityPin
{
    internal static bool Verify(X509Certificate2 presented, X509Certificate2 pinnedIdentity)
    {
        ArgumentNullException.ThrowIfNull(presented);
        ArgumentNullException.ThrowIfNull(pinnedIdentity);

        using var chain = new X509Chain();

        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(pinnedIdentity);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;

        return chain.Build(presented);
    }
}

/// <summary>Creates real, disposable identity material for the gate tests.</summary>
internal static class IntegrationCertificates
{
    internal static TestIdentityMaterial CreateIdentity(string bridgeId)
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);

        var request = new CertificateRequest(
            new X500DistinguishedName($"CN=CodexQuota Bridge {bridgeId}"),
            rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.DigitalSignature,
                true));

        var now = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

        // CreateSelfSigned already binds the request's key to the result.
        return new TestIdentityMaterial(bridgeId, request.CreateSelfSigned(now, now.AddYears(10)));
    }
}

internal sealed class TestIdentityMaterial : IDisposable
{
    internal TestIdentityMaterial(string bridgeId, X509Certificate2 certificate)
    {
        BridgeId = bridgeId;
        Certificate = certificate;
    }

    internal string BridgeId { get; }

    internal X509Certificate2 Certificate { get; }

    public void Dispose() => Certificate.Dispose();
}
