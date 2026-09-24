using System.ComponentModel;
using System.Windows;
using CodexQuota.Desktop.ViewModels;

namespace CodexQuota.Desktop.Views;

/// <summary>
/// The Pair Device window.
/// </summary>
/// <remarks>
/// It hides on close like the status window, because a pairing session lives for five minutes and
/// closing the window should not silently discard the session the phone is looking at.
/// </remarks>
public partial class PairingQrWindow : Window
{
    private readonly PairingViewModel _viewModel;
    private bool _allowClose;

    public PairingQrWindow(PairingViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;
    }

    /// <summary>Starts a fresh pairing session and shows it.</summary>
    internal void BeginPairing()
    {
        _viewModel.StartPairing();
        Show();
        Activate();
    }

    /// <summary>Lets the next close request actually close, used by the tray Exit command.</summary>
    internal void AllowClose() => _allowClose = true;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    private void OnAllow(object sender, RoutedEventArgs e)
    {
        // The one and only place a pairing is approved, and only because a human clicked.
        _viewModel.Allow();
    }

    private void OnReject(object sender, RoutedEventArgs e) => _viewModel.Reject();
}
