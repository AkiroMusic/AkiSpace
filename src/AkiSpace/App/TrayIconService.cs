using System.Drawing;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;

namespace AkiSpace.App;

/// <summary>
/// System tray presence (NotifyIcon + context menu), extracted from MainForm.
/// The shell subscribes to the request events and calls <see cref="SetConnected"/> /
/// <see cref="SetVisible"/> as state changes. Dispose is idempotent (the shutdown
/// path re-enters it via Application.Exit / Shutdown → Closing).
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly ILogger<TrayIconService> _logger;
    private readonly ContextMenuStrip _trayMenu;
    private readonly NotifyIcon _trayIcon;
    private bool _disposed;

    public event Action? ConnectToggleRequested;
    public event Action? ShowWindowRequested;
    public event Action? ExitRequested;

    public TrayIconService(ILogger<TrayIconService> logger, Icon? icon)
    {
        _logger = logger;
        _trayMenu = new ContextMenuStrip();
        _trayMenu.Items.Add("连接分身", null, (_, _) => ConnectToggleRequested?.Invoke());
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("显示主窗口", null, (_, _) => ShowWindowRequested?.Invoke());
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke());

        _trayIcon = new NotifyIcon
        {
            Icon = icon,
            Text = "AkiSpace - 未连接",
            ContextMenuStrip = _trayMenu,
            Visible = false,
        };
        _trayIcon.DoubleClick += (_, _) => ShowWindowRequested?.Invoke();
    }

    public void SetConnected(bool connected)
    {
        if (_disposed) return;
        _trayIcon.Text = connected ? "AkiSpace - 已连接" : "AkiSpace - 未连接";
        _trayMenu.Items[0].Text = connected ? "断开分身" : "连接分身";
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
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayMenu.Dispose();
        _logger.LogDebug("Tray icon disposed");
    }
}
