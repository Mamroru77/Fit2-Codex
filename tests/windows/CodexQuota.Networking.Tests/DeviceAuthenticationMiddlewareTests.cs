using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using CodexQuota.Networking.Auth;
using CodexQuota.Networking.Contracts.V1;
using CodexQuota.Networking.Hosting;
using CodexQuota.Networking.Security;
using CodexQuota.Storage.Devices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodexQuota.Networking.Tests;

/// <summary>
/// The authorization boundary. Everything the Bridge exposes about the Codex account must be
/// unreachable without a device credential, and the one route that must work before pairing is the
/// pairing route itself.
/// </summary>
/// <remarks>
/// These run against a real Kestrel listener over real TLS rather than an in-memory test server,
/// because the pinned-certificate client below is part of what is being verified: if the listener
/// presented anything other than the issued leaf, every request here would fail.
/// </remarks>
public class DeviceAuthenticationMiddlewareTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task AnonymousQuotaRequestIsRejectedWithTheStableErrorCode()
    {
        await using var bridge = await BridgeHarness.StartAsync();

        using var response = await bridge.Client.GetAsync("/api/v1/quota");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var error = await ReadErrorAsync(response);

        Assert.Equal(ApiErrorCodes.DeviceUnauthorized, error.Error.Code);
        Assert.False(error.Error.Retryable);
    }

    [Fact]
    public async Task AValidBearerReachesTheEndpoint()
    {
        await using var bridge = await BridgeHarness.StartAsync();
        var credential = await bridge.IssueCredentialAsync();

        using var response = await bridge.Client.SendAsync(Authorized(
            HttpMethod.Get,
            "/api/v1/quota",
            credential.Token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var principal = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(credential.DeviceId, principal.GetProperty("deviceId").GetString());
    }

    [Fact]
    public async Task ARevokedBearerIsRejectedImmediately()
    {
        await using var bridge = await BridgeHarness.StartAsync();
        var credential = await bridge.IssueCredentialAsync();

        Assert.Equal(HttpStatusCode.OK, (await bridge.Client.SendAsync(
            Authorized(HttpMethod.Get, "/api/v1/quota", credential.Token))).StatusCode);

        await bridge.RevokeAsync(credential.DeviceId);

        using var response = await bridge.Client.SendAsync(
            Authorized(HttpMethod.Get, "/api/v1/quota", credential.Token));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(ApiErrorCodes.DeviceUnauthorized, (await ReadErrorAsync(response)).Error.Code);
    }

    [Theory]
    [InlineData("not-a-token")]
    [InlineData("")]
    public async Task AWrongBearerIsRejected(string token)
    {
        await using var bridge = await BridgeHarness.StartAsync();

        using var response = await bridge.Client.SendAsync(Authorized(HttpMethod.Get, "/api/v1/quota", token));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ANonBearerAuthorizationSchemeIsNotAccepted()
    {
        await using var bridge = await BridgeHarness.StartAsync();
        var credential = await bridge.IssueCredentialAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/quota");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credential.Token);

        using var response = await bridge.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ThePairingRouteIsReachableWithoutABearer()
    {
        await using var bridge = await BridgeHarness.StartAsync();

        using var response = await bridge.Client.PostAsync("/api/v1/pairing/request", content: null);

        // Reachable, and nothing about the account is disclosed by reaching it.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HealthIsAnonymousAndDisclosesNoAccountOrQuotaData()
    {
        await using var bridge = await BridgeHarness.StartAsync();

        using var response = await bridge.Client.GetAsync("/api/v1/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();

        // A health endpoint must never be a side channel for quota, account or credential data.
        foreach (var forbidden in new[] { "usedPercent", "remainingPercent", "windows", "email", "token", "authMode" })
        {
            Assert.DoesNotContain(forbidden, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task AnUnknownPathIsNotTurnedIntoAnAuthorizationError()
    {
        await using var bridge = await BridgeHarness.StartAsync();

        using var response = await bridge.Client.GetAsync("/api/v1/does-not-exist");

        // 404 is both correct and less informative than a 401 would be.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TheAuthorizationHeaderIsNeverWrittenToTheLog()
    {
        await using var bridge = await BridgeHarness.StartAsync();
        var credential = await bridge.IssueCredentialAsync();

        using var response = await bridge.Client.SendAsync(
            Authorized(HttpMethod.Get, "/api/v1/quota", credential.Token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The credential must not appear in any log line, at any level.
        Assert.DoesNotContain(
            bridge.Logs.Lines,
            line => line.Contains(credential.Token, StringComparison.Ordinal));
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<ApiErrorResponse> ReadErrorAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<ApiErrorResponse>(json, V1Json.Options)!;
    }
}

/// <summary>
/// A running Bridge API on an ephemeral loopback port, with a client that pins the leaf it was
/// issued rather than trusting anything that answers.
/// </summary>
internal sealed class BridgeHarness : IAsyncDisposable
{
    private readonly TestIdentityMaterial _identity;
    private readonly BridgeIdentity _bridgeIdentity;
    private readonly X509Certificate2 _leaf;
    private readonly InMemoryPairedDeviceRepository _repository;
    private readonly DeviceTokenService _tokens;
    private readonly BridgeApiHost _host;
    private readonly HttpClient _client;
    private readonly HttpClientHandler _handler;

    private BridgeHarness(
        TestIdentityMaterial identity,
        BridgeIdentity bridgeIdentity,
        X509Certificate2 leaf,
        InMemoryPairedDeviceRepository repository,
        DeviceTokenService tokens,
        BridgeApiHost host,
        CapturingLoggerProvider logs)
    {
        _identity = identity;
        _bridgeIdentity = bridgeIdentity;
        _leaf = leaf;
        _repository = repository;
        _tokens = tokens;
        _host = host;
        Logs = logs;

        var expectedSpki = CertificateFingerprint.ComputeSpkiSha256(leaf);

        _handler = new HttpClientHandler
        {
            // Pin the leaf that was issued. This is a real check, not a bypass: any other
            // certificate — including another self-signed one — fails it.
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null
                && CertificateFingerprint.ComputeSpkiSha256(certificate) == expectedSpki,
        };

        _client = new HttpClient(_handler) { BaseAddress = new Uri(host.Addresses[0]) };
    }

    internal HttpClient Client => _client;

    internal CapturingLoggerProvider Logs { get; }

    internal static async Task<BridgeHarness> StartAsync()
    {
        var identity = TestCertificates.CreateIdentity("bridge-1");
        var bridgeIdentity = new BridgeIdentity(identity.BridgeId, identity.Certificate);

        var leaf = LeafCertificateFactory.CreateServerCertificate(
            bridgeIdentity,
            [IPAddress.Loopback],
            ["localhost"],
            DateTimeOffset.UtcNow);

        var repository = new InMemoryPairedDeviceRepository();
        var tokens = new DeviceTokenService(repository);
        var logs = new CapturingLoggerProvider();

        var host = BridgeApiHost.Create(
            new BridgeEndpointOptions(IPAddress.Loopback, Port: 0, BridgeVersion: "1.0.0-test"),
            leaf,
            services =>
            {
                services.AddSingleton<IPairedDeviceRepository>(repository);
                services.AddSingleton(tokens);
                services.AddLogging(logging => logging.AddProvider(logs));
            },
            endpoints =>
            {
                endpoints.MapGet("/api/v1/quota", (HttpContext context) =>
                    Results.Ok(new { deviceId = context.DevicePrincipal()!.DeviceId }));

                endpoints.MapGet("/api/v1/health", () => Results.Ok(new { status = "ok" }))
                    .AllowAnonymous();

                endpoints.MapPost("/api/v1/pairing/request", () => Results.Ok(new { claimed = false }))
                    .AllowAnonymous();
            });

        await host.StartAsync(CancellationToken.None);

        return new BridgeHarness(identity, bridgeIdentity, leaf, repository, tokens, host, logs);
    }

    internal Task<Pairing.DeviceCredential> IssueCredentialAsync()
        => _tokens.IssueAsync("Find X8", DateTimeOffset.UtcNow, CancellationToken.None);

    internal Task<bool> RevokeAsync(string deviceId)
        => _tokens.RevokeAsync(deviceId, DateTimeOffset.UtcNow, CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        _handler.Dispose();

        await _host.DisposeAsync();

        _leaf.Dispose();
        _bridgeIdentity.Dispose();
        _identity.Dispose();
    }
}

/// <summary>Captures every formatted log line so a leak can be asserted against.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<string> _lines = [];
    private readonly object _gate = new();

    internal IReadOnlyList<string> Lines
    {
        get
        {
            lock (_gate)
            {
                return _lines.ToArray();
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Add(string line)
    {
        lock (_gate)
        {
            _lines.Add(line);
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly CapturingLoggerProvider _owner;
        private readonly string _category;

        internal CapturingLogger(CapturingLoggerProvider owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => _owner.Add($"{_category}: {formatter(state, exception)}{exception}");
    }
}
