using System.Net.Http.Headers;
using CodexQuota.Networking.Contracts.V1;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace CodexQuota.Networking.Auth;

/// <summary>
/// Enforces the device credential on every route that is not explicitly anonymous.
/// </summary>
/// <remarks>
/// <para>
/// The default is deny: a route has to opt out of authentication with <c>AllowAnonymous()</c>, and
/// only the pairing bootstrap and <c>/health</c> do. Everything the Bridge knows about the Codex
/// account — quota, history, events and the WebSocket — therefore sits behind a credential by
/// construction rather than by remembering to add an attribute.
/// </para>
/// <para>
/// Nothing here logs. An <c>Authorization</c> header is a long-lived credential, so it is read,
/// hashed and discarded; it is never formatted into a message, a scope or a diagnostic.
/// </para>
/// </remarks>
public sealed class DeviceAuthenticationMiddleware
{
    /// <summary>Key under which the authenticated <see cref="DevicePrincipal"/> is stored.</summary>
    public const string PrincipalItemKey = "codexquota.device-principal";

    private const string BearerScheme = "Bearer";

    private readonly RequestDelegate _next;
    private readonly DeviceTokenService _tokens;

    public DeviceAuthenticationMiddleware(RequestDelegate next, DeviceTokenService tokens)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(tokens);

        _next = next;
        _tokens = tokens;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (IsAnonymous(context))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var principal = await AuthenticateAsync(context).ConfigureAwait(false);

        if (principal is null)
        {
            await WriteUnauthorizedAsync(context).ConfigureAwait(false);
            return;
        }

        context.Items[PrincipalItemKey] = principal;
        await _next(context).ConfigureAwait(false);
    }

    private async Task<DevicePrincipal?> AuthenticateAsync(HttpContext context)
    {
        if (!TryReadBearerToken(context.Request, out var token))
        {
            return null;
        }

        return await _tokens.ValidateAsync(token, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// A route is anonymous when it asked to be, or when no route matched at all — in which case the
    /// request will 404, which discloses less than a 401 would.
    /// </summary>
    private static bool IsAnonymous(HttpContext context)
    {
        var endpoint = context.GetEndpoint();

        return endpoint is null || endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
    }

    private static bool TryReadBearerToken(HttpRequest request, out string token)
    {
        token = string.Empty;

        if (!request.Headers.TryGetValue(HeaderNames.Authorization, out var values))
        {
            return false;
        }

        if (!AuthenticationHeaderValue.TryParse(values.ToString(), out var header))
        {
            return false;
        }

        if (!string.Equals(header.Scheme, BearerScheme, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(header.Parameter))
        {
            return false;
        }

        token = header.Parameter;
        return true;
    }

    private static Task WriteUnauthorizedAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;

        return context.Response.WriteAsJsonAsync(
            ApiErrorResponse.Create(
                ApiErrorCodes.DeviceUnauthorized,
                "This device is not paired with the Bridge.",
                retryable: false),
            V1Json.Options);
    }
}

/// <summary>Wiring helpers for the device authentication boundary.</summary>
public static class DeviceAuthExtensions
{
    /// <summary>
    /// Adds the device authentication middleware. It must run after routing, so that each request
    /// already knows whether the endpoint it matched is anonymous.
    /// </summary>
    public static IApplicationBuilder UseDeviceAuthentication(this IApplicationBuilder application)
    {
        ArgumentNullException.ThrowIfNull(application);

        return application.UseMiddleware<DeviceAuthenticationMiddleware>();
    }

    /// <summary>The authenticated device for this request, or <c>null</c> when it is anonymous.</summary>
    public static DevicePrincipal? DevicePrincipal(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Items.TryGetValue(DeviceAuthenticationMiddleware.PrincipalItemKey, out var value)
            ? value as DevicePrincipal
            : null;
    }

    /// <summary>
    /// The authenticated device for this request. Throws when called from a route that is not
    /// behind the middleware, which is a programming error rather than a request problem.
    /// </summary>
    public static DevicePrincipal RequireDevicePrincipal(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.DevicePrincipal()
               ?? throw new InvalidOperationException(
                   "This route is not behind device authentication, so it has no device principal.");
    }
}
