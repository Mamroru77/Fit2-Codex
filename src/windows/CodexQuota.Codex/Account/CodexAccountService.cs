using System.Text.Json;
using CodexQuota.Codex.Protocol;

namespace CodexQuota.Codex.Account;

/// <summary>
/// Owns the Codex App Server account surface: reading authentication state, starting
/// ChatGPT-managed OAuth, and logging out. The Bridge never receives or stores the user's
/// credentials; the user completes authentication in the system browser. Opening the returned
/// authorization URL is the desktop layer's responsibility.
/// </summary>
public sealed class CodexAccountService
{
    private const string ReadMethod = "account/read";
    private const string LoginStartMethod = "account/login/start";
    private const string LogoutMethod = "account/logout";

    /// <summary>The ChatGPT-managed login request. Serialized with web defaults, so the field
    /// names on the wire are exactly <c>type</c>, <c>useHostedLoginSuccessPage</c> and
    /// <c>appBrand</c>.</summary>
    private static readonly object ChatGptLoginParameters = new
    {
        type = "chatgpt",
        useHostedLoginSuccessPage = true,
        appBrand = "chatgpt",
    };

    private readonly CodexRpcClient _client;

    public CodexAccountService(CodexRpcClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        _client = client;
    }

    /// <summary>Reads the current account authentication state.</summary>
    public async Task<AccountState> ReadAccountAsync(CancellationToken cancellationToken)
    {
        var payload = await _client
            .CallAsync<JsonElement>(ReadMethod, null, cancellationToken)
            .ConfigureAwait(false);

        return AccountState.FromPayload(payload);
    }

    /// <summary>
    /// Starts ChatGPT-managed OAuth and returns the authorization URL the user must open.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The App Server did not return an absolute authorization URL.
    /// </exception>
    public async Task<Uri> StartChatGptLoginAsync(CancellationToken cancellationToken)
    {
        var payload = await _client
            .CallAsync<JsonElement>(LoginStartMethod, ChatGptLoginParameters, cancellationToken)
            .ConfigureAwait(false);

        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("authUrl", out var authUrlElement)
            || authUrlElement.ValueKind != JsonValueKind.String
            || !Uri.TryCreate(authUrlElement.GetString(), UriKind.Absolute, out var authUrl))
        {
            throw new InvalidOperationException(
                $"The Codex App Server did not return an absolute authUrl for {LoginStartMethod}.");
        }

        return authUrl;
    }

    /// <summary>Signs the Codex account out.</summary>
    public async Task LogoutAsync(CancellationToken cancellationToken)
        => await _client
            .CallAsync<JsonElement>(LogoutMethod, null, cancellationToken)
            .ConfigureAwait(false);
}
