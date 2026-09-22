using CodexQuota.Core.Quota;

namespace CodexQuota.Core.Runtime;

/// <summary>
/// High-level phase of the Bridge runtime, mirroring the documented startup flow
/// STARTING → CODEX_INITIALIZING → CHECKING_AUTH → AUTH_REQUIRED or SYNCING → READY.
/// </summary>
public enum BridgeRuntimePhase
{
    /// <summary>Nothing has been started yet.</summary>
    Starting,

    /// <summary>The Codex App Server handshake is in progress.</summary>
    CodexInitializing,

    /// <summary>The Codex account state is being read.</summary>
    CheckingAuth,

    /// <summary>Codex authentication is required before quota can be read.</summary>
    AuthRequired,

    /// <summary>The first quota read for the current session is in progress.</summary>
    Syncing,

    /// <summary>The Bridge is running and quota state is being maintained.</summary>
    Ready,

    /// <summary>The Bridge has been shut down.</summary>
    Stopped,
}

/// <summary>
/// An immutable view of the Bridge runtime: which phase it is in, how the quota source is
/// behaving, and when quota was last read successfully.
/// </summary>
/// <param name="Phase">Current lifecycle phase.</param>
/// <param name="SourceStatus">Current availability of the quota source.</param>
/// <param name="LastSuccessfulSyncAt">UTC instant of the last successful quota read, if any.</param>
public sealed record BridgeRuntimeSnapshot(
    BridgeRuntimePhase Phase,
    QuotaSourceStatus SourceStatus,
    DateTimeOffset? LastSuccessfulSyncAt);

/// <summary>
/// Observable runtime state of the Bridge. Consumers read <see cref="Current"/>; interested
/// parties subscribe to <see cref="Changed"/>. Bridge reachability and quota source availability
/// are separate concerns, so the phase and the source status are tracked independently.
/// </summary>
public sealed class BridgeRuntimeState
{
    private readonly object _gate = new();

    private BridgeRuntimeSnapshot _current = new(
        BridgeRuntimePhase.Starting,
        QuotaSourceStatus.Unavailable,
        null);

    /// <summary>The current runtime snapshot.</summary>
    public BridgeRuntimeSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Raised after a change that actually altered the snapshot.</summary>
    public event Action<BridgeRuntimeSnapshot>? Changed;

    /// <summary>Moves the runtime to a new lifecycle phase.</summary>
    public void SetPhase(BridgeRuntimePhase phase)
        => Update(current => current with { Phase = phase });

    /// <summary>
    /// Moves the runtime to a new lifecycle phase and records the quota source status in a single
    /// transition, so consumers never observe a phase that contradicts the source status.
    /// </summary>
    public void SetPhase(BridgeRuntimePhase phase, QuotaSourceStatus sourceStatus)
        => Update(current => current with { Phase = phase, SourceStatus = sourceStatus });

    /// <summary>Records the availability of the quota source.</summary>
    public void SetSourceStatus(QuotaSourceStatus status)
        => Update(current => current with { SourceStatus = status });

    /// <summary>
    /// Records a successful quota read. Freshly read quota is by definition online, so this
    /// clears any previous source error in the same transition.
    /// </summary>
    public void RecordSuccessfulSync(DateTimeOffset syncedAt)
        => Update(current => current with
        {
            LastSuccessfulSyncAt = syncedAt,
            SourceStatus = QuotaSourceStatus.Online,
        });

    private void Update(Func<BridgeRuntimeSnapshot, BridgeRuntimeSnapshot> change)
    {
        BridgeRuntimeSnapshot updated;

        lock (_gate)
        {
            updated = change(_current);

            if (updated == _current)
            {
                return;
            }

            _current = updated;
        }

        Changed?.Invoke(updated);
    }
}
