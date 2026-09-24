using CodexQuota.Networking.Contracts.V1;
using Microsoft.AspNetCore.Http;

namespace CodexQuota.Networking.Api.V1;

/// <summary>
/// Builds the single v1 error envelope every endpoint returns.
/// </summary>
/// <remarks>
/// Keeping this in one place is what makes the error contract stable: a client branches on
/// <see cref="ApiError.Code"/>, so the code and its HTTP status must always agree.
/// </remarks>
internal static class ApiResults
{
    /// <summary>An error envelope with an explicit status code.</summary>
    internal static IResult Problem(string code, string message, bool retryable, int statusCode)
        => Results.Json(
            ApiErrorResponse.Create(code, message, retryable),
            V1Json.Options,
            statusCode: statusCode);

    /// <summary>
    /// A resource the Bridge is running but cannot currently serve: the Bridge is reachable, so
    /// this must never look like a connection failure to the client.
    /// </summary>
    internal static IResult Unavailable(string code, string message, bool retryable = true)
        => Problem(code, message, retryable, StatusCodes.Status503ServiceUnavailable);

    /// <summary>A malformed or out-of-bounds request parameter.</summary>
    internal static IResult Invalid(string message)
        => Problem(ApiErrorCodes.InvalidRequest, message, retryable: false, StatusCodes.Status400BadRequest);

    /// <summary>
    /// A pairing session that is unknown, already used, rejected, or not yet approved. They are one
    /// answer from the caller's point of view: the client must re-pair, not retry.
    /// </summary>
    internal static IResult InvalidPairing()
        => Problem(
            ApiErrorCodes.PairingInvalid,
            "This pairing session is not valid. Start a new pairing.",
            retryable: false,
            StatusCodes.Status400BadRequest);

    /// <summary>A pairing session that ran out of its five minutes.</summary>
    internal static IResult ExpiredPairing()
        => Problem(
            ApiErrorCodes.PairingExpired,
            "This pairing session has expired. Start a new pairing.",
            retryable: false,
            StatusCodes.Status400BadRequest);
}
