using CodexQuota.Core.Quota;
using CodexQuota.Core.Runtime;
using CodexQuota.Networking.Contracts.V1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace CodexQuota.Networking.Api.V1;

/// <summary>
/// The current quota snapshot.
/// </summary>
public static class QuotaEndpoints
{
    /// <summary>Maps <c>GET /api/v1/quota</c>.</summary>
    /// <remarks>
    /// The handler reads the in-memory state store and the runtime phase and nothing else. It never
    /// invokes Codex and never touches SQLite, so the one endpoint that must always answer cannot be
    /// taken down by a slow or failing child process or database.
    /// </remarks>
    public static void MapQuotaEndpoints(this WebApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);

        application.MapGet(
            "/api/v1/quota",
            (IQuotaStateStore store, BridgeRuntimeState runtime) => Handle(store, runtime));
    }

    private static IResult Handle(IQuotaStateStore store, BridgeRuntimeState runtime)
    {
        if (store.Current is { } snapshot)
        {
            // The snapshot's own status carries freshness, including `auth_required`: a Bridge that
            // is serving its last trusted data still answers 200.
            return Results.Ok(V1ContractMapper.ToQuotaResponse(snapshot));
        }

        // No trusted snapshot yet. Report why, as Bridge state, so the client never has to guess
        // whether the computer is off or Codex simply needs a login.
        return runtime.Current.SourceStatus switch
        {
            QuotaSourceStatus.AuthRequired => ApiResults.Unavailable(
                ApiErrorCodes.CodexAuthRequired,
                "Codex authentication is required before quota can be read.",
                retryable: false),

            QuotaSourceStatus.SourceSchemaUnsupported => ApiResults.Unavailable(
                ApiErrorCodes.SourceSchemaUnsupported,
                "The Codex rate-limit schema could not be interpreted safely.",
                retryable: false),

            QuotaSourceStatus.SourceError => ApiResults.Unavailable(
                ApiErrorCodes.CodexUnavailable,
                "The Codex quota source reported an error."),

            _ => ApiResults.Unavailable(
                ApiErrorCodes.RateLimitDataUnavailable,
                "No quota data has been read yet."),
        };
    }
}
