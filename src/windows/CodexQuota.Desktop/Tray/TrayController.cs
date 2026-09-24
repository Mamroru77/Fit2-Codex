using System.Drawing;
using System.Windows.Forms;

namespace CodexQuota.Desktop.Tray;

/// <summary>
/// The tray icon and its command menu. It owns no Bridge logic: every command is a callback the
/// host supplies.
/// </summary>
/// <remarks>
/// <see cref="System.Windows.Forms.NotifyIcon"/> is used deliberately, from the WPF host, so no
/// third-party tray framework is needed.
/// </remarks>
public sealed class TrayController : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayController(
        Action openStatus,
        Action refreshNow,
        Action login,
        Action logout,
        Action openLogs,
        Action exit)
    {
        ArgumentNullException.ThrowIfNull(openStatus);
        ArgumentNullException.ThrowIfNull(refreshNow);
        ArgumentNullException.ThrowIfNull(login);
        ArgumentNullException.ThrowIfNull(logout);
        ArgumentNullException.ThrowIfNull(openLogs);
        ArgumentNullException.ThrowIfNull(exit);

        var menu = new ContextMenuStrip();

        menu.Items.Add(CreateItem("Open Status", openStatus));
        menu.Items.Add(CreateItem("Refresh Now", refreshNow));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(CreateItem("Login", login));
        menu.Items.Add(CreateItem("Logout", logout));
        menu.Items.Add(CreateItem("Open Logs", openLogs));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(CreateItem("Exit", exit));

        _icon = new NotifyIcon
        {
            // No embedded icon resource: the shell supplies a standard application icon.
            Icon = SystemIcons.Application,
            Text = "Codex Quota Bridge",
            Visible = true,
            ContextMenuStrip = menu,
        };
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    private static ToolStripItem CreateItem(string text, Action onClick)
        => new ToolStripMenuItem(text, null, (_, _) => onClick());
}
