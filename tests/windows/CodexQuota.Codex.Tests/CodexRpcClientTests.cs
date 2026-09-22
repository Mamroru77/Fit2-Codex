using System.Text.Json;
using CodexQuota.Codex.Protocol;
using CodexQuota.Codex.Tests.Fakes;
using Xunit;

namespace CodexQuota.Codex.Tests;

public class CodexRpcClientTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task InitializePerformsHandshakeInOrderBeforeAnyAccountCall()
    {
        var transport = new FakeJsonRpcTransport();
        await using var client = new CodexRpcClient(transport);
        using var timeout = new CancellationTokenSource(TestTimeout);

        var initialize = client.InitializeAsync(timeout.Token);
        await transport.WaitForWritesAsync(1, timeout.Token);

        // 1. initialize request, correlated with id 1 and carrying clientInfo.
        var initializeRequest = Parse(transport.WrittenLines[0]);
        Assert.Equal("initialize", initializeRequest.GetProperty("method").GetString());
        Assert.Equal(1L, initializeRequest.GetProperty("id").GetInt64());

        var clientInfo = initializeRequest.GetProperty("params").GetProperty("clientInfo");
        Assert.Equal("codex_quota_bridge", clientInfo.GetProperty("name").GetString());
        Assert.Equal("Codex Quota Bridge", clientInfo.GetProperty("title").GetString());
        Assert.Equal("1.0.0", clientInfo.GetProperty("version").GetString());

        // Nothing else may be sent before the initialize response arrives.
        Assert.Single(transport.WrittenLines);

        // 2. initialize response.
        transport.EnqueueLine("""{"id":1,"result":{"userAgent":"codex-app-server"}}""");
        await initialize;

        // 3. initialized notification, with no id.
        await transport.WaitForWritesAsync(2, timeout.Token);
        var initialized = Parse(transport.WrittenLines[1]);
        Assert.Equal("initialized", initialized.GetProperty("method").GetString());
        Assert.False(initialized.TryGetProperty("id", out _));

        // 4. the first account call only becomes possible after the handshake completed.
        var accountRead = client.CallAsync<JsonElement>("account/read", null, timeout.Token);
        await transport.WaitForWritesAsync(3, timeout.Token);

        var accountRequest = Parse(transport.WrittenLines[2]);
        Assert.Equal("account/read", accountRequest.GetProperty("method").GetString());
        Assert.Equal(2L, accountRequest.GetProperty("id").GetInt64());

        transport.EnqueueLine("""{"id":2,"result":{"authenticated":true}}""");

        var account = await accountRead;
        Assert.True(account.GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task CorrelatesConcurrentRequestsWhoseResponsesArriveInReverseOrder()
    {
        var transport = new FakeJsonRpcTransport();
        await using var client = new CodexRpcClient(transport);
        using var timeout = new CancellationTokenSource(TestTimeout);

        var first = client.CallAsync<JsonElement>("first/method", null, timeout.Token);
        var second = client.CallAsync<JsonElement>("second/method", null, timeout.Token);

        await transport.WaitForWritesAsync(2, timeout.Token);

        Assert.Equal(1L, Parse(transport.WrittenLines[0]).GetProperty("id").GetInt64());
        Assert.Equal(2L, Parse(transport.WrittenLines[1]).GetProperty("id").GetInt64());

        // Responses arrive newest first.
        transport.EnqueueLine("""{"id":2,"result":{"value":"second"}}""");
        transport.EnqueueLine("""{"id":1,"result":{"value":"first"}}""");

        var firstResult = await first;
        var secondResult = await second;

        Assert.Equal("first", firstResult.GetProperty("value").GetString());
        Assert.Equal("second", secondResult.GetProperty("value").GetString());
    }

    [Fact]
    public async Task DeliversNotificationsThatCarryMethodAndNoId()
    {
        var transport = new FakeJsonRpcTransport();
        await using var client = new CodexRpcClient(transport);
        using var timeout = new CancellationTokenSource(TestTimeout);

        transport.EnqueueLine(
            """{"method":"account/rateLimits/updated","params":{"primary":{"usedPercent":25}}}""");

        CodexNotification? delivered = null;

        await foreach (var notification in client.Notifications(timeout.Token))
        {
            delivered = notification;
            break;
        }

        Assert.NotNull(delivered);
        Assert.Equal("account/rateLimits/updated", delivered!.Method);

        Assert.NotNull(delivered.Params);
        var primary = delivered.Params!.Value.GetProperty("primary");
        Assert.Equal(25d, primary.GetProperty("usedPercent").GetDouble());
    }

    [Fact]
    public async Task MalformedLineFaultsNotificationsWithoutDeliveringANotification()
    {
        var transport = new FakeJsonRpcTransport();
        await using var client = new CodexRpcClient(transport);
        using var timeout = new CancellationTokenSource(TestTimeout);

        transport.EnqueueLine("{ this is not valid json");

        var delivered = new List<CodexNotification>();

        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var notification in client.Notifications(timeout.Token))
            {
                delivered.Add(notification);
            }
        });

        Assert.IsAssignableFrom<JsonException>(exception);
        Assert.Empty(delivered);
    }

    [Fact]
    public async Task MalformedLineFailsPendingRequestsInsteadOfLeavingThemHanging()
    {
        var transport = new FakeJsonRpcTransport();
        await using var client = new CodexRpcClient(transport);
        using var timeout = new CancellationTokenSource(TestTimeout);

        var pending = client.CallAsync<JsonElement>("account/read", null, timeout.Token);
        await transport.WaitForWritesAsync(1, timeout.Token);

        transport.EnqueueLine("not json at all");

        var exception = await Assert.ThrowsAnyAsync<Exception>(async () => await pending);
        Assert.IsAssignableFrom<JsonException>(exception);
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
