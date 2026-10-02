using System.Drawing;
using System.Windows.Forms;
using AkiSpace.Common;
using Microsoft.Extensions.Logging;

namespace AkiSpace.App;

/// <summary>
/// System tray presence (NotifyIcon + context menu). The shell subscribes to the
/// request events and calls <see cref="SetConnected"/> / <see cref="SetVisible"/>
/// as state changes. Menu texts localize live (rebuilt on language switch).
/// Dispose is idempotent (the shutdown path re-enters it via
/// Application.Exit / Shutdown → Closing).
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly ILogger<TrayIconService> _logger;
    private readonly ContextMenuStrip _trayMenu;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripItem _connectItem;
    private readonly ToolStripMenuItem _screenshotItem;
    private readonly ToolStripItem _showItem;
    private readonly ToolStripItem _exitItem;
    private bool _connected;
    private bool _disposed;

    public event Action? ConnectToggleRequested;
    public event Action? ScreenshotRequested;
    public event Action? ShowWindowRequested;
    public event Action? ExitRequested;

    public TrayIconService(ILogger<TrayIconService> logger, Icon? icon)
    {
        _logger = logger;
        _trayMenu = new ContextMenuStrip();
        _connectItem = _trayMenu.Items.Add(Loc.T("Tray_ToggleConnect"), null, (_, _) => ConnectToggleRequested?.Invoke());
        _screenshotItem = new ToolStripMenuItem(Loc.T("Tray_Screenshot"), null, (_, _) => ScreenshotRequested?.Invoke())
        {
            Enabled = false, // enabled once the clone session is connected
        };
        _trayMenu.Items.Add(_screenshotItem);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _showItem = _trayMenu.Items.Add(Loc.T("Tray_Show"), null, (_, _) => ShowWindowRequested?.Invoke());
        _trayMenu.Items.Add(new ToolStripSeparator());
        _exitItem = _trayMenu.Items.Add(Loc.T("Tray_Exit"), null, (_, _) => ExitRequested?.Invoke());

        _trayIcon = new NotifyIcon
        {
            Icon = icon,
            Text = Loc.T("Tray_TipDisconnected"),
            ContextMenuStrip = _trayMenu,
            Visible = false,
        };
        _trayIcon.DoubleClick += (_, _) => ShowWindowRequested?.Invoke();

        Loc.LanguageChanged += ApplyTexts;
    }

    private void ApplyTexts()
    {
        if (_disposed) return;
        _connectItem.Text = _connected ? Loc.T("Tray_ToggleDisconnect") : Loc.T("Tray_ToggleConnect");
        _screenshotItem.Text = Loc.T("Tray_Screenshot");
        _showItem.Text = Loc.T("Tray_Show");
        _exitItem.Text = Loc.T("Tray_Exit");
        _trayIcon.Text = _connected ? Loc.T("Tray_TipConnected") : Loc.T("Tray_TipDisconnected");
    }

    public void SetConnected(bool connected)
    {
        if (_disposed) return;
        _connected = connected;
        ApplyTexts();
        _screenshotItem.Enabled = connected; // needs a live clone session to capture
    }

    /// <summary>Shows or hides the tray icon (minimize-to-tray).</summary>
    public void SetVisible(bool visible)
    {
        if (_disposed) return;
        _trayIcon.Visible = visible;
    }

    public void Dispose()
    {
        // Idempotent: the exit path re-enters this handler via Application.Exit /
        // Shutdown → Closing, and touching a disposed NotifyIcon would throw.
        if (_disposed) return;
        _disposed = true;
        Loc.LanguageChanged -= ApplyTexts;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayMenu.Dispose();
        _logger.LogDebug("Tray icon disposed");
    }
}
