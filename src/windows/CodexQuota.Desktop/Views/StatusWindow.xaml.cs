using System.ComponentModel;
using System.Windows;
using CodexQuota.Desktop.ViewModels;

namespace CodexQuota.Desktop.Views;

/// <summary>
/// The status window. Closing it only hides it: the Bridge keeps running until the tray says
/// <c>Exit</c>, which is what owning a child process demands.
/// </summary>
public partial class StatusWindow : Window
{
    private bool _allowClose;

    public StatusWindow(StatusViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        DataContext = viewModel;
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
}
