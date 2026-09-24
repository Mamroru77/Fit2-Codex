using CodexQuota.Networking.Contracts.V1;
using CodexQuota.Networking.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace CodexQuota.Networking.Api.V1;

/// <summary>
/// Liveness and version discovery.
/// </summary>
public static class HealthEndpoints
{
    /// <summary>
    /// Maps <c>/health</c> and <c>/info</c>.
    /// </summary>
    /// <remarks>
    /// <c>/health</c> is anonymous and answers with a constant: it must never become a side channel
    /// for quota, account or credential state. <c>/info</c> reports versions only but is still
    /// authenticated, because an unpaired client learns the API version from the QR payload or the
    /// mDNS record and has no other reason to call it.
    /// </remarks>
    public static void MapHealthEndpoints(this WebApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);

        application.MapGet("/api/v1/health", () => Results.Ok(new { status = "ok" }))
            .AllowAnonymous();

        application.MapGet("/api/v1/info", (BridgeEndpointOptions options) => Results.Ok(new
        {
            apiVersion = BridgeEndpointOptions.ApiVersion,
            bridgeVersion = options.BridgeVersion,
            schemaVersion = V1ContractValidator.SupportedSchemaVersion,
        }));
    }
}
