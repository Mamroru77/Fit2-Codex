namespace CodexQuota.Codex.Process;

/// <summary>
/// Bounded restart policy for the Codex App Server child process. Delays are pinned to
/// 1s, 2s, 5s, 10s and 30s, and the restart budget is exhausted once more than five crashes
/// have happened inside a rolling five-minute window.
/// </summary>
/// <remarks>
/// Instances are not thread-safe; the supervisor owns a single instance.
/// </remarks>
public sealed class RestartBackoff
{
    /// <summary>The documented restart delays, in order.</summary>
    public static readonly IReadOnlyList<TimeSpan> DefaultDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
    ];

    /// <summary>The window in which repeated crashes exhaust the restart budget.</summary>
    public static readonly TimeSpan DefaultCrashWindow = TimeSpan.FromMinutes(5);

    /// <summary>Number of crashes tolerated inside <see cref="DefaultCrashWindow"/>.</summary>
    public const int DefaultCrashThreshold = 5;

    private readonly IReadOnlyList<TimeSpan> _delays;
    private readonly TimeSpan _crashWindow;
    private readonly int _crashThreshold;
    private readonly List<DateTimeOffset> _crashes = [];

    public RestartBackoff()
        : this(DefaultDelays, DefaultCrashWindow, DefaultCrashThreshold)
    {
    }

    public RestartBackoff(IReadOnlyList<TimeSpan> delays, TimeSpan crashWindow, int crashThreshold)
    {
        ArgumentNullException.ThrowIfNull(delays);

        if (delays.Count == 0)
        {
            throw new ArgumentException("At least one restart delay is required.", nameof(delays));
        }

        if (crashThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(crashThreshold), crashThreshold, "The crash threshold must be at least one.");
        }

        _delays = delays;
        _crashWindow = crashWindow;
        _crashThreshold = crashThreshold;
    }

    /// <summary>Crashes that still count against the budget.</summary>
    public int CrashesInWindow => _crashes.Count;

    /// <summary>
    /// Records a crash and returns the delay to wait before the next automatic launch, or
    /// <c>null</c> when the restart budget is exhausted and the manager must fault instead of
    /// launching again.
    /// </summary>
    public TimeSpan? NextDelay(DateTimeOffset crashedAt)
    {
        _crashes.Add(crashedAt);

        // Crashes older than the rolling window no longer count against the budget, so a long
        // period of healthy operation restores the normal first delay.
        var cutoff = crashedAt - _crashWindow;
        _crashes.RemoveAll(crash => crash < cutoff);

        if (_crashes.Count > _crashThreshold)
        {
            return null;
        }

        return _delays[Math.Min(_crashes.Count - 1, _delays.Count - 1)];
    }
}
