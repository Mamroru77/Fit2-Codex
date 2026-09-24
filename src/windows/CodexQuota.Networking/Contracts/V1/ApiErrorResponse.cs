namespace CodexQuota.Networking.Contracts.V1;

/// <summary>The stable machine-readable part of an API error.</summary>
/// <param name="Code">One of the stable v1 codes. Clients branch on this, never on the message.</param>
/// <param name="Message">Human-readable text. Never parsed by a client.</param>
/// <param name="Retryable">Whether retrying the same request could plausibly succeed.</param>
public sealed record ApiError(string Code, string Message, bool Retryable);

/// <summary>
/// The single error envelope for every v1 failure, REST and pairing alike.
/// </summary>
public sealed record ApiErrorResponse(ApiError Error)
{
    /// <summary>Creates the envelope for a stable error code.</summary>
    public static ApiErrorResponse Create(string code, string message, bool retryable)
        => new(new ApiError(code, message, retryable));
}

/// <summary>
/// The stable machine-readable v1 error codes.
/// </summary>
/// <remarks>
/// The list is closed in the sense that clients may rely on these exact strings; adding a new code
/// within v1 is allowed, deleting or renaming one is not.
/// </remarks>
public static class ApiErrorCodes
{
    public const string PairingInvalid = "PAIRING_INVALID";
    public const string PairingExpired = "PAIRING_EXPIRED";
    public const string DeviceUnauthorized = "DEVICE_UNAUTHORIZED";
    public const string CodexAuthRequired = "CODEX_AUTH_REQUIRED";
    public const string CodexUnavailable = "CODEX_UNAVAILABLE";
    public const string RateLimitDataUnavailable = "RATE_LIMIT_DATA_UNAVAILABLE";
    public const string SourceSchemaUnsupported = "SOURCE_SCHEMA_UNSUPPORTED";
    public const string SecurityIdentityMismatch = "SECURITY_IDENTITY_MISMATCH";
    public const string ApiVersionUnsupported = "API_VERSION_UNSUPPORTED";
    public const string BridgeInternalError = "BRIDGE_INTERNAL_ERROR";

    /// <summary>
    /// A payload did not satisfy the v1 contract. Used by the explicit contract validator so a
    /// malformed document is reported rather than silently defaulted.
    /// </summary>
    public const string DataProtocolError = "DATA_PROTOCOL_ERROR";
}
