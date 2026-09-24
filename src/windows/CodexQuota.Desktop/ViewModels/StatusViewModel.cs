using System.ComponentModel;
using System.Runtime.CompilerServices;
using CodexQuota.Core.Quota;
using CodexQuota.Core.Runtime;

namespace CodexQuota.Desktop.ViewModels;

/// <summary>
/// Everything the status window shows. It observes the runtime state and the quota store, and
/// it never talks to Codex itself.
/// </summary>
/// <remarks>
/// Scalars only, so a change raised from a background thread is marshalled by the WPF binding
/// engine. Refreshing is therefore safe from the runtime-state callback.
/// </remarks>
public sealed class StatusViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IQuotaStateStore _store;
    private readonly BridgeRuntimeState _runtime;

    public StatusViewModel(IQuotaStateStore store, BridgeRuntimeState runtime)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(runtime);

        _store = store;
        _runtime = runtime;
        _runtime.Changed += OnRuntimeChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string PhaseText => _runtime.Current.Phase.ToString();

    public string SourceStatusText => _runtime.Current.SourceStatus.ToString();

    public string LastSyncText => _runtime.Current.LastSuccessfulSyncAt is { } syncedAt
        ? syncedAt.ToLocalTime().ToString("HH:mm:ss")
        : "never";

    public string ShortWindowText => Describe(_store.Current?.ShortWindow);

    public string WeeklyWindowText => Describe(_store.Current?.Weekly);

    /// <summary>Re-reads every bound value. Called on startup and on each runtime change.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(PhaseText));
        OnPropertyChanged(nameof(SourceStatusText));
        OnPropertyChanged(nameof(LastSyncText));
        OnPropertyChanged(nameof(ShortWindowText));
        OnPropertyChanged(nameof(WeeklyWindowText));
    }

    public void Dispose() => _runtime.Changed -= OnRuntimeChanged;

    private void OnRuntimeChanged(BridgeRuntimeSnapshot snapshot) => Refresh();

    private static string Describe(QuotaWindow? window)
        => window is null
            ? "unknown"
            : $"{window.RemainingPercent:0.#}% left, resets {window.ResetsAt.ToLocalTime():ddd HH:mm}";

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
