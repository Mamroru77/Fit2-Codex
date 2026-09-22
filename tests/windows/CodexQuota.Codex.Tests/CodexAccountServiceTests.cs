using System.Text.Json;
using CodexQuota.Codex.Account;
using CodexQuota.Codex.Protocol;
using CodexQuota.Codex.Tests.Fakes;
using Xunit;

namespace CodexQuota.Codex.Tests;

public class CodexAccountServiceTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task StartChatGptLoginSendsTheExactDocumentedParameters()
    {
        var transport = new FakeJsonRpcTransport();
        await using var client = new CodexRpcClient(transport);
        var service = new CodexAccountService(client);
        using var timeout = new CancellationTokenSource(TestTimeout);

        var login = service.StartChatGptLoginAsync(timeout.Token);

        await transport.WaitForWritesAsync(1, timeout.Token);
        var request = Parse(transport.WrittenLines[0]);

        Assert.Equal("account/login/start", request.GetProperty("method").GetString());

        // The ChatGPT-managed OAuth request must carry exactly these three fields and nothing else.
        AssertParametersMatch(
            request.GetProperty("params"),
            """{"type":"chatgpt","useHostedLoginSuccessPage":true,"appBrand":"chatgpt"}""");

        transport.EnqueueLine("""{"id":1,"result":{"authUrl":"https://auth.openai.com/authorize?session=abc"}}""");

        var authUrl = await login;

        Assert.Equal(new Uri("https://auth.openai.com/authorize?session=abc"), authUrl);
    }

    [Fact]
    public async Task StartChatGptLoginFailsWhenTheResponseCarriesNoAuthUrl()
    {
        var transport = new FakeJsonRpcTransport();
        await using var client = new CodexRpcClient(transport);
        var service = new CodexAccountService(client);
        using var timeout = new CancellationTokenSource(TestTimeout);

        var login = service.StartChatGptLoginAsync(timeout.Token);
        await transport.WaitForWritesAsync(1, timeout.Token);

        transport.EnqueueLine("""{"id":1,"result":{}}""");

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await login);
    }

    [Fact]
    public async Task StartChatGptLoginFailsWhenAuthUrlIsNotAnAbsoluteUrl()
    {
        var transport = new FakeJsonRpcTransport();
        await using var client = new CodexRpcClient(transport);
        var service = new CodexAccountService(client);
        using var timeout = new CancellationTokenSource(TestTimeout);

        var login = service.StartChatGptLoginAsync(timeout.Token);
        await transport.WaitForWritesAsync(1, timeout.Token);

        transport.EnqueueLine("""{"id":1,"result":{"authUrl":"not-a-url"}}""");

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await login);
    }

    [Fact]
    public async Task ReadAccountReportsAuthRequiredWhenAuthModeIsMissing()
    {
        var transport = new FakeJsonRpcTransport();
        await using var client = new CodexRpcClient(transport);
        var service = new CodexAccountService(client);
        using var timeout = new CancellationTokenSource(TestTimeout);

        var read = service.ReadAccountAsync(timeout.Token);
        await transport.WaitForWritesAsync(1, timeout.Token);

        Assert.Equal("account/read", Parse(transport.WrittenLines[0]).GetProperty("method").GetString());

        transport.EnqueueLine("""{"id":1,"result":{}}""");

        var account = await read;

        Assert.False(account.Authenticated);
        Assert.Null(account.AuthMode);
        Assert.Null(account.Email);
    }

    [Fact]
    public async Task ReadAccountReportsTheAuthenticatedAccount()
    {
        var transport = new FakeJsonRpcTransport();
        await using var client = new CodexRpcClient(transport);
        var service = new CodexAccountService(client);
        using var timeout = new CancellationTokenSource(TestTimeout);

        var read = service.ReadAccountAsync(timeout.Token);
        await transport.WaitForWritesAsync(1, timeout.Token);

        transport.EnqueueLine(
            """{"id":1,"result":{"authMode":"chatgpt","account":{"email":"dev@example.com"}}}""");

        var account = await read;

        Assert.True(account.Authenticated);
        Assert.Equal("chatgpt", account.AuthMode);
        Assert.Equal("dev@example.com", account.Email);
    }

    [Fact]
    public async Task LogoutSendsTheAccountLogoutRequest()
    {
        var transport = new FakeJsonRpcTransport();
        await using var client = new CodexRpcClient(transport);
        var service = new CodexAccountService(client);
        using var timeout = new CancellationTokenSource(TestTimeout);

        var logout = service.LogoutAsync(timeout.Token);

        await transport.WaitForWritesAsync(1, timeout.Token);
        Assert.Equal("account/logout", Parse(transport.WrittenLines[0]).GetProperty("method").GetString());

        transport.EnqueueLine("""{"id":1,"result":{}}""");

        await logout;
    }

    /// <summary>
    /// Asserts that the payload carries exactly the expected fields with exactly the expected
    /// values. Field order is irrelevant, but an extra or missing field is a failure.
    /// </summary>
    private static void AssertParametersMatch(JsonElement actual, string expectedJson)
    {
        using var document = JsonDocument.Parse(expectedJson);
        var expected = document.RootElement;

        Assert.Equal(expected.EnumerateObject().Count(), actual.EnumerateObject().Count());

        foreach (var property in expected.EnumerateObject())
        {
            Assert.True(actual.TryGetProperty(property.Name, out var value), $"Missing parameter '{property.Name}'.");
            Assert.Equal(property.Value.GetRawText(), value.GetRawText());
        }
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
