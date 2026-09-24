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
using CodexQuota.Storage.Devices;
using CodexQuota.Storage.History;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CodexQuota.Networking.Tests;

/// <summary>
/// The REST surface. Two properties matter most: <c>/quota</c> reads only the in-memory snapshot,
/// and the bounds on the history window are enforced rather than assumed.
/// </summary>
public class RestApiTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 13, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task QuotaReturnsTheCurrentInMemorySnapshot()
    {
        await using var api = await RestApiHarness.StartAsync();
        var credential = await api.IssueCredentialAsync();

        api.Store.Replace(TestSnapshots.Snapshot(shortRemaining: 72, weeklyRemaining: 54));

        var quota = await api.GetAsync<QuotaResponse>("/api/v1/quota", credential.Token);

        Assert.Equal(1, quota.SchemaVersion);
        Assert.Equal("codex_app_server", quota.Source);
        Assert.Equal("online", quota.Status);
        Assert.Equal(72d, quota.Windows.ShortWindow.RemainingPercent, 0.0001);
        Assert.Equal(300, quota.Windows.ShortWindow.WindowMinutes);
        Assert.Equal(54d, quota.Windows.Weekly.RemainingPercent, 0.0001);
        Assert.Equal(10080, quota.Windows.Weekly.WindowMinutes);
    }

    [Fact]
    public async Task QuotaNeverTouchesTheHistoryRepository()
    {
        await using var api = await RestApiHarness.StartAsync();
        var credential = await api.IssueCredentialAsync();
        api.Store.Replace(TestSnapshots.Snapshot(72, 54));

        await api.GetAsync<QuotaResponse>("/api/v1/quota", credential.Token);

        // /quota reads the current snapshot and nothing else. Reaching for history here would put
        // SQLite on the critical path of the one endpoint that must always answer.
        Assert.Equal(0, api.History.ReadCalls);
    }

    [Fact]
    public async Task AuthRequiredIsReportedAsBridgeStateNotAsATransportFailure()
    {
        await using var api = await RestApiHarness.StartAsync();
        var credential = await api.IssueCredentialAsync();

        api.Runtime.SetPhase(BridgeRuntimePhase.AuthRequired, QuotaSourceStatus.AuthRequired);

        using var response = await api.Client.SendAsync(api.Authorized(HttpMethod.Get, "/api/v1/quota", credential.Token));

        // A reachable Bridge that needs a login is a structured answer, never a connection error.
        var error = await RestApiHarness.ReadErrorAsync(response);

        Assert.Equal(ApiErrorCodes.CodexAuthRequired, error.Error.Code);
        Assert.False(error.Error.Retryable);
    }

    [Fact]
    public async Task AnUnavailableSourceIsReportedWithItsOwnCode()
    {
        await using var api = await RestApiHarness.StartAsync();
        var credential = await api.IssueCredentialAsync();

        api.Runtime.SetPhase(BridgeRuntimePhase.Ready, QuotaSourceStatus.SourceSchemaUnsupported);

        using var response = await api.Client.SendAsync(api.Authorized(HttpMethod.Get, "/api/v1/quota", credential.Token));

        Assert.Equal(ApiErrorCodes.SourceSchemaUnsupported, (await RestApiHarness.ReadErrorAsync(response)).Error.Code);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("25")]
    [InlineData("100")]
    [InlineData("abc")]
    [InlineData("")]
    public async Task AnOutOfBoundsHoursParameterIsAStableValidationError(string hours)
    {
        await using var api = await RestApiHarness.StartAsync();
        var credential = await api.IssueCredentialAsync();

        using var response = await api.Client.SendAsync(
            api.Authorized(HttpMethod.Get, $"/api/v1/history?hours={hours}", credential.Token));

        Assert.Equal(ApiErrorCodes.InvalidRequest, (await RestApiHarness.ReadErrorAsync(response)).Error.Code);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(24)]
    public async Task AnInBoundsHoursParameterIsAccepted(int hours)
    {
        await using var api = await RestApiHarness.StartAsync();
        var credential = await api.IssueCredentialAsync();
        api.History.Points = [new HistoryPoint(Now.AddHours(-1), 80, 60)];

        var history = await api.GetAsync<HistoryResponse>($"/api/v1/history?hours={hours}", credential.Token);

        Assert.Equal(hours, history.Hours);
        Assert.Single(history.Points);
    }

    [Fact]
    public async Task TheHistoryWindowDefaultsToTwentyFourHours()
    {
        await using var api = await RestApiHarness.StartAsync();
        var credential = await api.IssueCredentialAsync();

        var history = await api.GetAsync<HistoryResponse>("/api/v1/history", credential.Token);

        Assert.Equal(24, history.Hours);
    }

    [Fact]
    public async Task TheHistoryWindowIsClampedToWhatTheBridgeActuallyStores()
    {
        await using var api = await RestApiHarness.StartAsync();
        var credential = await api.IssueCredentialAsync();

        await api.GetAsync<HistoryResponse>("/api/v1/history?hours=6", credential.Token);

        // The requested window really is what gets queried, and it is measured back from now.
        var (from, to) = api.History.LastRange;

        Assert.True(to > from);
        Assert.InRange((to - from).TotalHours, 5.9, 6.1);
    }

    [Fact]
    public async Task AFailingHistoryRepositoryIsStructuredAndLeavesQuotaWorking()
    {
        await using var api = await RestApiHarness.StartAsync();
        var credential = await api.IssueCredentialAsync();
        api.Store.Replace(TestSnapshots.Snapshot(72, 54));
        api.History.ThrowOnRead = true;

        using var historyResponse = await api.Client.SendAsync(
            api.Authorized(HttpMethod.Get, "/api/v1/history?hours=24", credential.Token));

        Assert.Equal(ApiErrorCodes.HistoryUnavailable, (await RestApiHarness.ReadErrorAsync(historyResponse)).Error.Code);

        // The whole point of the isolation guarantee: history failing must not take quota down.
        var quota = await api.GetAsync<QuotaResponse>("/api/v1/quota", credential.Token);

        Assert.Equal(72d, quota.Windows.ShortWindow.RemainingPercent, 0.0001);
    }

    [Fact]
    public async Task EventsAreReturnedInTheDocumentedShape()
    {
        await using var api = await RestApiHarness.StartAsync();
        var credential = await api.IssueCredentialAsync();
        api.History.Events = [new QuotaEvent(QuotaEventType.QuotaChanged, Now.AddMinutes(-5), "short window 81% -> 74%")];

        var events = await api.GetAsync<EventsResponse>("/api/v1/events?hours=24", credential.Token);

        Assert.Equal(24, events.Hours);
        var entry = Assert.Single(events.Events);
        Assert.Equal("quota_changed", entry.Type);
        Assert.Equal("short window 81% -> 74%", entry.Detail);
    }

    [Fact]
    public async Task InfoReportsTheThreeIndependentVersions()
    {
        await using var api = await RestApiHarness.StartAsync();
        var credential = await api.IssueCredentialAsync();

        var info = await api.GetAsync<JsonElement>("/api/v1/info", credential.Token);

        Assert.Equal("v1", info.GetProperty("apiVersion").GetString());
        Assert.Equal("1.0.0-test", info.GetProperty("bridgeVersion").GetString());
        Assert.Equal(1, info.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public async Task InfoRequiresACredential()
    {
        await using var api = await RestApiHarness.StartAsync();

        using var response = await api.Client.GetAsync("/api/v1/info");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task NoNetworkRouteCanApproveAPairingSession()
    {
        await using var api = await RestApiHarness.StartAsync();

        // Structural guard, not a behavioural one: if a future route ever lets the network approve
        // a pairing, this fails regardless of how it is spelled.
        var routes = api.Endpoints.Select(route => route.DisplayName ?? string.Empty).ToArray();

        Assert.Contains(routes, route => route.Contains("/api/v1/pairing/", StringComparison.Ordinal));
        Assert.DoesNotContain(routes, route => route.Contains("approve", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(routes, route => route.Contains("reject", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ADiscoveryRequestWaitsForLocalApproval()
    {
        await using var api = await RestApiHarness.StartAsync();

        var session = await api.PostAsync<PairingSessionResponse>(
            "/api/v1/pairing/request",
            new { displayName = "Find X8" },
            token: null);

        Assert.Equal("awaiting_local_approval", session.Status);
        Assert.Equal(6, session.VerificationCode.Length);

        // And it cannot be completed from the network in that state.
        using var completion = await api.Client.PostAsJsonAsync(
            "/api/v1/pairing/complete",
            new { pairingId = session.PairingId });

        Assert.Equal(ApiErrorCodes.PairingInvalid, (await RestApiHarness.ReadErrorAsync(completion)).Error.Code);
    }

    [Fact]
    public async Task AQrSessionCanBeClaimedAndThenApprovedLocally()
    {
        await using var api = await RestApiHarness.StartAsync();

        // The desktop creates the QR session; the phone claims it over the network.
        var qr = api.Pairing.CreateSession(PairingOrigin.QrCode, null, DateTimeOffset.UtcNow);

        var claimed = await api.PostAsync<PairingSessionResponse>(
            "/api/v1/pairing/claim",
            new { pairingId = qr.PairingId, displayName = "Find X8" },
            token: null);

        Assert.Equal("awaiting_local_approval", claimed.Status);
        Assert.Equal("Find X8", claimed.DisplayName);

        // Only the local Windows action can move it forward.
        api.Pairing.ApproveLocally(qr.PairingId, DateTimeOffset.UtcNow);

        var completion = await api.PostAsync<DeviceCredentialResponse>(
            "/api/v1/pairing/complete",
            new { pairingId = qr.PairingId },
            token: null);

        Assert.Equal(48, Base64UrlDecode(completion.Token).Length);
        Assert.False(string.IsNullOrWhiteSpace(completion.DeviceId));

        // The issued credential works immediately.
        var quota = await api.GetAsync<JsonElement>("/api/v1/info", completion.Token);

        Assert.Equal("v1", quota.GetProperty("apiVersion").GetString());
    }

    [Fact]
    public async Task PairingStatusIsReadableWithoutACredential()
    {
        await using var api = await RestApiHarness.StartAsync();

        var session = api.Pairing.CreateSession(PairingOrigin.QrCode, null, DateTimeOffset.UtcNow);

        var status = await api.GetAsync<PairingSessionResponse>(
            $"/api/v1/pairing/status/{session.PairingId}",
            token: null);

        Assert.Equal("awaiting_client", status.Status);
    }

    [Fact]
    public async Task AnUnknownPairingIdIsRejected()
    {
        await using var api = await RestApiHarness.StartAsync();

        using var response = await api.Client.GetAsync("/api/v1/pairing/status/not-a-real-session");

        Assert.Equal(ApiErrorCodes.PairingInvalid, (await RestApiHarness.ReadErrorAsync(response)).Error.Code);
    }

    private static byte[] Base64UrlDecode(string value)
    {
        Assert.True(Base64Url.TryDecode(value, out var bytes));
        return bytes;
    }
}

/// <summary>A Bridge API with every v1 endpoint mapped, on an ephemeral loopback port.</summary>
internal sealed class RestApiHarness : IAsyncDisposable
{
    private readonly TestIdentityMaterial _identityMaterial;
    private readonly BridgeIdentity _identity;
    private readonly X509Certificate2 _leaf;
    private readonly BridgeApiHost _host;
    private readonly HttpClientHandler _handler;
    private readonly DeviceTokenService _tokens;
    private readonly InMemoryPairedDeviceRepository _devices;

    private RestApiHarness(
        TestIdentityMaterial identityMaterial,
        BridgeIdentity identity,
        X509Certificate2 leaf,
        BridgeApiHost host,
        HttpClient client,
        HttpClientHandler handler,
        InMemoryQuotaStateStore store,
        BridgeRuntimeState runtime,
        RecordingHistoryRepository history,
        PairingService pairing,
        DeviceTokenService tokens,
        InMemoryPairedDeviceRepository devices)
    {
        _identityMaterial = identityMaterial;
        _identity = identity;
        _leaf = leaf;
        _host = host;
        _handler = handler;
        _tokens = tokens;
        _devices = devices;

        Client = client;
        Store = store;
        Runtime = runtime;
        History = history;
        Pairing = pairing;
    }

    internal HttpClient Client { get; }

    internal InMemoryQuotaStateStore Store { get; }

    internal BridgeRuntimeState Runtime { get; }

    internal RecordingHistoryRepository History { get; }

    internal PairingService Pairing { get; }

    internal IReadOnlyList<Endpoint> Endpoints { get; private set; } = [];

    internal static async Task<RestApiHarness> StartAsync()
    {
        var identityMaterial = TestCertificates.CreateIdentity("bridge-1");
        var identity = new BridgeIdentity(identityMaterial.BridgeId, identityMaterial.Certificate);

        var leaf = LeafCertificateFactory.CreateServerCertificate(
            identity,
            [System.Net.IPAddress.Loopback],
            ["localhost"],
            DateTimeOffset.UtcNow);

        var store = new InMemoryQuotaStateStore();
        var runtime = new BridgeRuntimeState();
        var history = new RecordingHistoryRepository();
        var devices = new InMemoryPairedDeviceRepository();
        var tokens = new DeviceTokenService(devices);
        var pairing = new PairingService(tokens);

        var options = BridgeEndpointOptions.Loopback("1.0.0-test");

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
                services.AddSingleton(pairing);
            },
            endpoints =>
            {
                endpoints.MapHealthEndpoints();
                endpoints.MapQuotaEndpoints();
                endpoints.MapHistoryEndpoints();
                endpoints.MapPairingEndpoints();
            });

        await host.StartAsync(CancellationToken.None);

        var expectedSpki = CertificateFingerprint.ComputeSpkiSha256(leaf);

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null
                && CertificateFingerprint.ComputeSpkiSha256(certificate) == expectedSpki,
        };

        var client = new HttpClient(handler) { BaseAddress = new Uri(host.Addresses[0]) };

        var harness = new RestApiHarness(
            identityMaterial,
            identity,
            leaf,
            host,
            client,
            handler,
            store,
            runtime,
            history,
            pairing,
            tokens,
            devices);

        harness.Endpoints = host.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .ToArray();

        return harness;
    }

    internal HttpRequestMessage Authorized(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    internal Task<DeviceCredential> IssueCredentialAsync()
        => _tokens.IssueAsync("Find X8", DateTimeOffset.UtcNow, CancellationToken.None);

    internal async Task<T> GetAsync<T>(string path, string? token)
    {
        using var response = await Client.SendAsync(Request(HttpMethod.Get, path, token));
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<T>(V1Json.Options))!;
    }

    internal async Task<T> PostAsync<T>(string path, object body, string? token)
    {
        using var response = await Client.SendAsync(Request(HttpMethod.Post, path, token, body));
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<T>(V1Json.Options))!;
    }

    internal static async Task<ApiErrorResponse> ReadErrorAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<ApiErrorResponse>(json, V1Json.Options)!;
    }

    private HttpRequestMessage Request(HttpMethod method, string path, string? token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: V1Json.Options);
        }

        return request;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        _handler.Dispose();

        await _host.DisposeAsync();

        _leaf.Dispose();
        _identity.Dispose();
        _identityMaterial.Dispose();
    }
}

/// <summary>A history repository the test drives, and that records how it was used.</summary>
internal sealed class RecordingHistoryRepository : IHistoryRepository
{
    private int _readCalls;

    internal IReadOnlyList<HistoryPoint> Points { get; set; } = [];

    internal IReadOnlyList<QuotaEvent> Events { get; set; } = [];

    internal bool ThrowOnRead { get; set; }

    internal int ReadCalls => Volatile.Read(ref _readCalls);

    internal (DateTimeOffset From, DateTimeOffset To) LastRange { get; private set; }

    public Task<IReadOnlyList<HistoryPoint>> ReadHistoryAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _readCalls);
        LastRange = (from, to);

        return ThrowOnRead
            ? Task.FromException<IReadOnlyList<HistoryPoint>>(new InvalidOperationException("Simulated disk failure."))
            : Task.FromResult(Points);
    }

    public Task<IReadOnlyList<QuotaEvent>> ReadEventsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _readCalls);

        return ThrowOnRead
            ? Task.FromException<IReadOnlyList<QuotaEvent>>(new InvalidOperationException("Simulated disk failure."))
            : Task.FromResult(Events);
    }

    public Task AppendSnapshotAsync(QuotaStateUpdate update, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task AppendEventAsync(QuotaEvent quotaEvent, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

/// <summary>Builds domain snapshots for the REST tests.</summary>
internal static class TestSnapshots
{
    internal static QuotaSnapshot Snapshot(double shortRemaining, double weeklyRemaining)
    {
        var generatedAt = new DateTimeOffset(2026, 9, 22, 13, 30, 0, TimeSpan.Zero);
        var resetsAt = new DateTimeOffset(2026, 9, 22, 15, 42, 0, TimeSpan.Zero);

        return new QuotaSnapshot(
            SchemaVersion: 1,
            GeneratedAt: generatedAt,
            Source: V1ContractMapper.CodexAppServerSource,
            Status: QuotaSourceStatus.Online,
            LastSuccessfulSyncAt: generatedAt,
            ShortWindow: new QuotaWindow(100d - shortRemaining, shortRemaining, 300, resetsAt),
            Weekly: new QuotaWindow(100d - weeklyRemaining, weeklyRemaining, 10080, resetsAt.AddDays(6)));
    }
}
