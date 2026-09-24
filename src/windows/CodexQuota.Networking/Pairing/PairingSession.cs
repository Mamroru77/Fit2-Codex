namespace CodexQuota.Networking.Pairing;

/// <summary>How a pairing session was started.</summary>
public enum PairingOrigin
{
    /// <summary>Created on Windows and presented as a QR code the phone scans.</summary>
    QrCode,

    /// <summary>Requested by a phone that found the Bridge through mDNS discovery.</summary>
    Discovery,
}

/// <summary>
/// Lifecycle of a pairing session.
/// </summary>
/// <remarks>
/// Only <see cref="PairingState.Approved"/> can produce a device credential, and only the Windows
/// tray can move a session into it. A network caller can create a session and claim it; it can
/// never approve it.
/// </remarks>
public enum PairingState
{
    /// <summary>A QR session that no phone has claimed yet.</summary>
    AwaitingClient,

    /// <summary>Waiting for the person at the Windows machine to press Allow.</summary>
    AwaitingLocalApproval,

    /// <summary>Locally allowed; completion may issue a credential once.</summary>
    Approved,

    /// <summary>Locally rejected. Terminal.</summary>
    Rejected,

    /// <summary>Already completed. Terminal, and the reason a session is single-use.</summary>
    Consumed,

    /// <summary>Past its expiry. Terminal.</summary>
    Expired,
}

/// <summary>
/// One pairing attempt.
/// </summary>
/// <param name="PairingId">One-time identifier carried in the QR payload or discovery request.</param>
/// <param name="Origin">How the session was started.</param>
/// <param name="VerificationCode">
/// Six digits shown on both the phone and the Windows pairing window, for a person to compare. It is
/// not a credential and authenticates nothing.
/// </param>
/// <param name="State">Current lifecycle state.</param>
/// <param name="RequestedDisplayName">Name the phone asked to be known by, once it has claimed.</param>
/// <param name="CreatedAt">UTC instant the session was created.</param>
/// <param name="ExpiresAt">UTC instant the session stops being usable.</param>
public sealed record PairingSession(
    string PairingId,
    PairingOrigin Origin,
    string VerificationCode,
    PairingState State,
    string? RequestedDisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);
