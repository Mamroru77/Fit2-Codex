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
    public async Task CallAsyncBeforeInitializationIsRejected()
    {
        var transport = new FakeJsonRpcTransport();
        await using var client = new CodexRpcClient(transport);
        using var timeout = new CancellationTokenSource(TestTimeout);

        // CancellationToken.None on purpose: only an immediate rejection can end this call, and
        // WaitAsync bounds the RED state instead of letting it hang the whole test run.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CallAsync<JsonElement>("account/read", null, CancellationToken.None).WaitAsync(TestTimeout));

        // Rejection must happen before anything is written, or the wire order is already broken.
        Assert.Empty(transport.WrittenLines);
    }

    [Fact]
    public async Task ConcurrentInitializeAsyncCallsProduceExactlyOneHandshake()
    {
        var transport = new FakeJsonRpcTransport();
        await using var client = new CodexRpcClient(transport);
        using var timeout = new CancellationTokenSource(TestTimeout);

        var first = client.InitializeAsync(timeout.Token);
        var second = client.InitializeAsync(timeout.Token);

        await transport.WaitForWritesAsync(1, timeout.Token);

        // The second caller must not have emitted its own initialize request.
        Assert.Single(transport.WrittenLines);

        transport.EnqueueLine("""{"id":1,"result":{}}""");

        await Task.WhenAll(first, second).WaitAsync(TestTimeout);

        // Exactly one initialize request followed by exactly one initialized notification.
        Assert.Equal(2, transport.WrittenLines.Count);
        Assert.Equal("initialize", Parse(transport.WrittenLines[0]).GetProperty("method").GetString());
        Assert.Equal("initialized", Parse(transport.WrittenLines[1]).GetProperty("method").GetString());
    }

    [Fact]
    public async Task InitializeAsyncAfterSuccessDoesNotEmitASecondHandshake()
    {
        var transport = new FakeJsonRpcTransport();
        await using var client = new CodexRpcClient(transport);
        using var timeout = new CancellationTokenSource(TestTimeout);

        await CompleteHandshakeAsync(client, transport, timeout.Token);
        Assert.Equal(2, transport.WrittenLines.Count);

        await client.InitializeAsync(timeout.Token).WaitAsync(TestTimeout);

        Assert.Equal(2, transport.WrittenLines.Count);
    }

    [Fact]
    public async Task CorrelatesConcurrentRequestsWhoseResponsesArriveInReverseOrder()
    {
        var transport = new FakeJsonRpcTransport();
        await using var client = new CodexRpcClient(transport);
        using var timeout = new CancellationTokenSource(TestTimeout);

        await CompleteHandshakeAsync(client, transport, timeout.Token);

        var first = client.CallAsync<JsonElement>("first/method", null, timeout.Token);
        var second = client.CallAsync<JsonElement>("second/method", null, timeout.Token);

        await transport.WaitForWritesAsync(4, timeout.Token);

        // Writes: initialize request, initialized notification, first, second.
        Assert.Equal(2L, Parse(transport.WrittenLines[2]).GetProperty("id").GetInt64());
        Assert.Equal(3L, Parse(transport.WrittenLines[3]).GetProperty("id").GetInt64());

        // Responses arrive newest first.
        transport.EnqueueLine("""{"id":3,"result":{"value":"second"}}""");
        transport.EnqueueLine("""{"id":2,"result":{"value":"first"}}""");

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

        await CompleteHandshakeAsync(client, transport, timeout.Token);

        var pending = client.CallAsync<JsonElement>("account/read", null, timeout.Token);
        await transport.WaitForWritesAsync(3, timeout.Token);

        transport.EnqueueLine("not json at all");

        var exception = await Assert.ThrowsAnyAsync<Exception>(async () => await pending);
        Assert.IsAssignableFrom<JsonException>(exception);
    }

    [Fact]
    public async Task CallAsyncFailsImmediatelyAfterMalformedJsonFaultsTheClient()
    {
        var transport = new FakeJsonRpcTransport();
        await using var client = new CodexRpcClient(transport);
        using var timeout = new CancellationTokenSource(TestTimeout);

        await CompleteHandshakeAsync(client, transport, timeout.Token);

        transport.EnqueueLine("not json at all");

        // Wait until the read loop has observed the malformed line and faulted the connection.
        await Assert.ThrowsAnyAsync<JsonException>(async () =>
        {
            await foreach (var notification in client.Notifications(timeout.Token))
            {
            }
        });

        // CancellationToken.None on purpose: the terminal state alone must fail this call.
        await Assert.ThrowsAnyAsync<JsonException>(
            () => client.CallAsync<JsonElement>("account/read", null, CancellationToken.None).WaitAsync(TestTimeout));
    }

    /// <summary>
    /// A deterministic reproduction of the terminal/pending registration race would need a
    /// production hook between <c>ThrowIfTerminal()</c> and <c>_pending.TryAdd</c>, so the invariant
    /// is recorded here and enforced by the post-registration re-check in the client instead:
    /// <list type="bullet">
    /// <item>terminal established before registration -&gt; the re-check removes and fails the entry;</item>
    /// <item>terminal established after registration -&gt; <c>FaultPending</c> sees and fails it.</item>
    /// </list>
    /// No interleaving may leave a request pending after a permanent connection failure.
    /// </summary>
    [Fact]
    public async Task InitializeAsyncFailsAfterTheConnectionBecomesTerminal()
    {
        var transport = new FakeJsonRpcTransport();
        await using var client = new CodexRpcClient(transport);
        using var timeout = new CancellationTokenSource(TestTimeout);

        await CompleteHandshakeAsync(client, transport, timeout.Token);

        transport.EnqueueLine("not json at all");

        await Assert.ThrowsAnyAsync<JsonException>(async () =>
        {
            await foreach (var notification in client.Notifications(timeout.Token))
            {
            }
        });

        // The handshake succeeded once, but the connection is now permanently dead, so
        // InitializeAsync must not report success just because the flag is still set.
        await Assert.ThrowsAnyAsync<JsonException>(
            () => client.InitializeAsync(CancellationToken.None).WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task DisposingTheClientFailsPendingRequestsInsteadOfLeavingThemHanging()
    {
        var transport = new FakeJsonRpcTransport();
        var client = new CodexRpcClient(transport);
        using var timeout = new CancellationTokenSource(TestTimeout);

        await CompleteHandshakeAsync(client, transport, timeout.Token);

        // CancellationToken.None on purpose: only the client's own disposal can end this call.
        var pending = client.CallAsync<JsonElement>("account/read", null, CancellationToken.None);
        await transport.WaitForWritesAsync(3, timeout.Token);

        await client.DisposeAsync();

        // WaitAsync bounds the RED state: before the fix this call hangs, and the assertion fails
        // with a TimeoutException instead of hanging the whole test run.
        await Assert.ThrowsAnyAsync<ObjectDisposedException>(() => pending.WaitAsync(TestTimeout));
    }

    /// <summary>
    /// Completes the documented handshake (initialize request, initialize response, initialized
    /// notification) so that ordinary RPC methods are allowed.
    /// </summary>
    private static async Task CompleteHandshakeAsync(
        CodexRpcClient client,
        FakeJsonRpcTransport transport,
        CancellationToken cancellationToken)
    {
        var initialize = client.InitializeAsync(cancellationToken);

        await transport.WaitForWritesAsync(1, cancellationToken);
        transport.EnqueueLine("""{"id":1,"result":{}}""");

        await initialize;
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
