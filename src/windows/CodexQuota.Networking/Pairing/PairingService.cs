using System.Globalization;
using System.Security.Cryptography;
using CodexQuota.Networking.Auth;
using CodexQuota.Networking.Contracts.V1;

namespace CodexQuota.Networking.Pairing;

/// <summary>
/// Owns the one-time pairing handshake.
/// </summary>
/// <remarks>
/// <para>
/// The whole point of this service is that a network caller can <em>ask</em> to be paired and can
/// never <em>grant</em> it. A phone can create a session (through discovery) and claim a QR session
/// created on Windows, but only <see cref="ApproveLocally"/> — reachable from the Windows tray and
/// nowhere else — moves a session into a state that can produce a credential.
/// </para>
/// <para>
/// Live sessions are in memory only. They expire after five minutes and are consumed by their first
/// successful completion, so a captured pairing id is worthless after either.
/// </para>
/// </remarks>
public sealed class PairingService
{
    /// <summary>How long a session stays usable.</summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(5);

    private const int PairingIdBytes = 16;
    private const int VerificationCodeModulus = 1_000_000;

    private readonly DeviceTokenService _tokens;
    private readonly Dictionary<string, PairingSession> _sessions = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    private bool _openForNewSessions = true;

    public PairingService(DeviceTokenService tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        _tokens = tokens;
    }

    /// <summary>
    /// Whether new pairing sessions may still be created. It is set to <c>false</c> as the first step
    /// of an orderly shutdown, so a phone cannot start a pairing that the Bridge is about to stop
    /// serving.
    /// </summary>
    public bool IsOpenForNewSessions
    {
        get
        {
            lock (_gate)
            {
                return _openForNewSessions;
            }
        }
    }

    /// <summary>
    /// Stops accepting new pairing sessions. Sessions already in flight keep their state so a user
    /// who is mid-pairing sees an accurate answer rather than a session that vanished.
    /// </summary>
    public void CloseForNewSessions()
    {
        lock (_gate)
        {
            _openForNewSessions = false;
        }
    }

    /// <summary>
    /// Starts a pairing session. A QR session waits for the phone; a discovery request already has
    /// a phone waiting and so goes straight to local approval.
    /// </summary>
    /// <exception cref="InvalidOperationException">The Bridge is shutting down.</exception>
    public PairingSession CreateSession(PairingOrigin origin, string? requestedDisplayName, DateTimeOffset now)
    {
        var session = new PairingSession(
            NewPairingId(),
            origin,
            RandomNumberGenerator.GetInt32(0, VerificationCodeModulus).ToString("D6", CultureInfo.InvariantCulture),
            origin == PairingOrigin.Discovery ? PairingState.AwaitingLocalApproval : PairingState.AwaitingClient,
            requestedDisplayName,
            now,
            now + SessionLifetime);

        lock (_gate)
        {
            if (!_openForNewSessions)
            {
                throw new InvalidOperationException("The Bridge is shutting down and is no longer pairing.");
            }

            _sessions[session.PairingId] = session;
        }

        return session;
    }

    /// <summary>Returns the current view of a session, or <c>null</c> when the id is unknown.</summary>
    public PairingSession? Find(string pairingId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pairingId);

        lock (_gate)
        {
            return _sessions.TryGetValue(pairingId, out var session) ? ExpireIfDue(session, now) : null;
        }
    }

    /// <summary>
    /// Attaches a phone to a QR session it scanned. A session can be claimed once: the second claim
    /// would mean a second phone is holding the same one-time pairing id.
    /// </summary>
    public PairingSession Claim(string pairingId, string displayName, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pairingId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        lock (_gate)
        {
            var session = Require(pairingId, now);

            if (session.State != PairingState.AwaitingClient)
            {
                throw new InvalidOperationException(
                    $"Pairing session '{pairingId}' cannot be claimed from state {session.State}.");
            }

            return Store(session with
            {
                State = PairingState.AwaitingLocalApproval,
                RequestedDisplayName = displayName,
            });
        }
    }

    /// <summary>
    /// The Windows tray's Allow action. This is the only transition that can lead to a credential,
    /// and it is deliberately not reachable from any network route.
    /// </summary>
    public PairingSession ApproveLocally(string pairingId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pairingId);

        lock (_gate)
        {
            var session = Require(pairingId, now);

            if (session.State != PairingState.AwaitingLocalApproval)
            {
                throw new InvalidOperationException(
                    $"Pairing session '{pairingId}' cannot be approved from state {session.State}.");
            }

            return Store(session with { State = PairingState.Approved });
        }
    }

    /// <summary>The Windows tray's Reject action.</summary>
    public PairingSession RejectLocally(string pairingId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pairingId);

        lock (_gate)
        {
            var session = Require(pairingId, now);

            if (session.State is not (PairingState.AwaitingClient or PairingState.AwaitingLocalApproval))
            {
                throw new InvalidOperationException(
                    $"Pairing session '{pairingId}' cannot be rejected from state {session.State}.");
            }

            return Store(session with { State = PairingState.Rejected });
        }
    }

    /// <summary>
    /// Completes an approved session and issues the device credential.
    /// </summary>
    /// <remarks>
    /// The session is marked consumed before anything is issued, so two concurrent completions
    /// cannot both succeed: whoever loses the race sees a session that is no longer approved.
    /// </remarks>
    public async Task<PairingCompleteResult> CompleteAsync(
        string pairingId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pairingId);

        PairingSession session;

        lock (_gate)
        {
            if (!_sessions.TryGetValue(pairingId, out var found))
            {
                return PairingCompleteResult.Failure(ApiErrorCodes.PairingInvalid);
            }

            session = ExpireIfDue(found, now);

            if (session.State == PairingState.Expired)
            {
                return PairingCompleteResult.Failure(ApiErrorCodes.PairingExpired);
            }

            if (session.State != PairingState.Approved)
            {
                return PairingCompleteResult.Failure(ApiErrorCodes.PairingInvalid);
            }

            _sessions[pairingId] = session with { State = PairingState.Consumed };
        }

        var credential = await _tokens
            .IssueAsync(session.RequestedDisplayName ?? "Paired device", now, cancellationToken)
            .ConfigureAwait(false);

        return PairingCompleteResult.Success(credential);
    }

    private PairingSession Require(string pairingId, DateTimeOffset now)
        => _sessions.TryGetValue(pairingId, out var session)
            ? ExpireIfDue(session, now)
            : throw new InvalidOperationException($"Pairing session '{pairingId}' is not known.");

    private PairingSession Store(PairingSession session)
    {
        _sessions[session.PairingId] = session;
        return session;
    }

    /// <summary>
    /// Moves a session that has run out of time into <see cref="PairingState.Expired"/>. The
    /// boundary is inclusive: at the expiry instant the session is already unusable.
    /// </summary>
    private PairingSession ExpireIfDue(PairingSession session, DateTimeOffset now)
    {
        if (session.State is PairingState.AwaitingClient or PairingState.AwaitingLocalApproval or PairingState.Approved
            && now >= session.ExpiresAt)
        {
            return Store(session with { State = PairingState.Expired });
        }

        return session;
    }

    private static string NewPairingId()
        => Base64Url.Encode(RandomNumberGenerator.GetBytes(PairingIdBytes));
}
