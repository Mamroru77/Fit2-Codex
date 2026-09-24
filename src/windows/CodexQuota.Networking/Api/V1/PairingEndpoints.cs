using CodexQuota.Networking.Contracts.V1;
using CodexQuota.Networking.Pairing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace CodexQuota.Networking.Api.V1;

/// <summary>The body of a discovery pairing request.</summary>
public sealed record PairingRequest(string? DisplayName);

/// <summary>The body of a QR pairing claim.</summary>
public sealed record PairingClaimRequest(string PairingId, string DisplayName);

/// <summary>The body of a pairing completion.</summary>
public sealed record PairingCompleteRequest(string PairingId);

/// <summary>The public view of a pairing session.</summary>
public sealed record PairingSessionResponse(
    string PairingId,
    string Status,
    string VerificationCode,
    string? DisplayName,
    DateTimeOffset ExpiresAt);

/// <summary>The credential a paired device receives, once.</summary>
public sealed record DeviceCredentialResponse(string DeviceId, string Token);

/// <summary>
/// The pairing bootstrap.
/// </summary>
/// <remarks>
/// Every route here is anonymous, because a device cannot present a credential before it has one.
/// That makes the absence of any approving route a security property rather than a detail: a network
/// caller can create a session, claim a QR session and ask whether its session is ready, and it can
/// never move a session into <see cref="PairingState.Approved"/>. Only
/// <see cref="PairingService.ApproveLocally"/>, reached from the Windows tray, can do that.
/// </remarks>
public static class PairingEndpoints
{
    /// <summary>Maps the pairing bootstrap routes.</summary>
    public static void MapPairingEndpoints(this WebApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);

        application.MapPost(
                "/api/v1/pairing/request",
                (PairingRequest request, PairingService pairing) => HandleRequest(request, pairing))
            .AllowAnonymous();

        application.MapPost(
                "/api/v1/pairing/claim",
                (PairingClaimRequest request, PairingService pairing) => HandleClaim(request, pairing))
            .AllowAnonymous();

        application.MapGet(
                "/api/v1/pairing/status/{pairingId}",
                (string pairingId, PairingService pairing) => HandleStatus(pairingId, pairing))
            .AllowAnonymous();

        application.MapPost(
                "/api/v1/pairing/complete",
                (PairingCompleteRequest request, PairingService pairing) => HandleCompleteAsync(request, pairing))
            .AllowAnonymous();
    }

    /// <summary>
    /// A phone asking to be paired after discovery. Discovery never establishes trust, so this can
    /// only ever create a session that is waiting for the user to say yes on Windows.
    /// </summary>
    private static IResult HandleRequest(PairingRequest request, PairingService pairing)
    {
        var session = pairing.CreateSession(PairingOrigin.Discovery, request?.DisplayName, DateTimeOffset.UtcNow);

        return Results.Ok(ToResponse(session));
    }

    /// <summary>Attaches a phone to a QR session the desktop created.</summary>
    private static IResult HandleClaim(PairingClaimRequest request, PairingService pairing)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.PairingId))
        {
            return ApiResults.Invalid("pairingId is required.");
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return ApiResults.Invalid("displayName is required.");
        }

        var existing = pairing.Find(request.PairingId, DateTimeOffset.UtcNow);

        if (existing is null)
        {
            return ApiResults.InvalidPairing();
        }

        if (existing.State == PairingState.Expired)
        {
            return ApiResults.ExpiredPairing();
        }

        try
        {
            return Results.Ok(ToResponse(pairing.Claim(request.PairingId, request.DisplayName, DateTimeOffset.UtcNow)));
        }
        catch (InvalidOperationException)
        {
            // Already claimed, already rejected, or no longer claimable. All of these are one
            // answer from the caller's point of view.
            return ApiResults.InvalidPairing();
        }
    }

    /// <summary>Lets the phone poll whether its session has been approved yet.</summary>
    private static IResult HandleStatus(string pairingId, PairingService pairing)
    {
        if (string.IsNullOrWhiteSpace(pairingId))
        {
            return ApiResults.InvalidPairing();
        }

        var session = pairing.Find(pairingId, DateTimeOffset.UtcNow);

        return session is null ? ApiResults.InvalidPairing() : Results.Ok(ToResponse(session));
    }

    /// <summary>
    /// Issues the device credential. It succeeds only for a session the local Windows user approved,
    /// and only once.
    /// </summary>
    private static async Task<IResult> HandleCompleteAsync(PairingCompleteRequest request, PairingService pairing)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.PairingId))
        {
            return ApiResults.Invalid("pairingId is required.");
        }

        var result = await pairing
            .CompleteAsync(request.PairingId, DateTimeOffset.UtcNow, CancellationToken.None)
            .ConfigureAwait(false);

        if (!result.Succeeded || result.Credential is null)
        {
            return result.ErrorCode == ApiErrorCodes.PairingExpired
                ? ApiResults.ExpiredPairing()
                : ApiResults.InvalidPairing();
        }

        return Results.Ok(new DeviceCredentialResponse(result.Credential.DeviceId, result.Credential.Token));
    }

    private static PairingSessionResponse ToResponse(PairingSession session)
        => new(
            session.PairingId,
            ToWireState(session.State),
            session.VerificationCode,
            session.RequestedDisplayName,
            session.ExpiresAt);

    /// <summary>The wire name of a pairing state.</summary>
    private static string ToWireState(PairingState state) => state switch
    {
        PairingState.AwaitingClient => "awaiting_client",
        PairingState.AwaitingLocalApproval => "awaiting_local_approval",
        PairingState.Approved => "approved",
        PairingState.Rejected => "rejected",
        PairingState.Consumed => "consumed",
        PairingState.Expired => "expired",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown pairing state."),
    };
}
