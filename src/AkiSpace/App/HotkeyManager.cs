using System.Runtime.InteropServices;
using AkiSpace.Services;
using Microsoft.Extensions.Logging;

namespace AkiSpace.App;

/// <summary>
/// Global hotkeys (Ctrl+Shift+D toggle connect, Ctrl+Alt+Space show window),
/// registered on a dedicated hidden Win32 window so they keep working while the
/// main window is hidden to the tray.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_TOGGLE_CONNECT = 1;
    private const int HOTKEY_SHOW_WINDOW = 2;
    private const int HOTKEY_SCREENSHOT = 3;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_ALT = 0x0001;
    private const uint VK_D = 0x44;
    private const uint VK_S = 0x53;
    private const uint VK_SPACE = 0x20;

    private readonly ILogger<HotkeyManager> _logger;
    private readonly SettingsService _settingsService;
    private HotkeyWindow? _window;
    private bool _registered;

    public event Action? ToggleConnectRequested;
    public event Action? ShowWindowRequested;
    public event Action? ScreenshotRequested;

    public HotkeyManager(ILogger<HotkeyManager> logger, SettingsService settingsService)
    {
        _logger = logger;
        _settingsService = settingsService;
    }

    public void Register()
    {
        if (!_settingsService.Current.EnableGlobalHotkey)
        {
            _logger.LogInformation("Global hotkeys disabled by settings");
            return;
        }
        try
        {
            // Reuse the existing hidden window on re-registration (enable → disable →
            // enable cycles must not stack orphaned message windows).
            _window ??= new HotkeyWindow();
            _window.HotkeyPressed -= HandleHotkey;
            _window.HotkeyPressed += HandleHotkey;
            UnregisterAllKeys();

            if (!_window.Register(HOTKEY_TOGGLE_CONNECT, MOD_CONTROL | MOD_SHIFT, VK_D))
            {
                var err = Marshal.GetLastWin32Error();
                _logger.LogWarning("Failed to register hotkey Ctrl+Shift+D (Win32 error {Error}); may be in use", err);
            }

            if (!_window.Register(HOTKEY_SHOW_WINDOW, MOD_CONTROL | MOD_ALT, VK_SPACE))
            {
                var err = Marshal.GetLastWin32Error();
                _logger.LogWarning("Failed to register hotkey Ctrl+Alt+Space (Win32 error {Error}); may be in use", err);
            }

            // Ctrl+Shift+S captures the clone screen to the HOST clipboard. Registration
            // failure is non-fatal (another app may own the combo) — the toolbar button
            // and tray menu still trigger the same capture.
            if (!_window.Register(HOTKEY_SCREENSHOT, MOD_CONTROL | MOD_SHIFT, VK_S))
            {
                var err = Marshal.GetLastWin32Error();
                _logger.LogWarning("Failed to register hotkey Ctrl+Shift+S (Win32 error {Error}); may be in use", err);
            }

            _registered = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register global hotkeys");
        }
    }

    /// <summary>Re-applies the persisted EnableGlobalHotkey setting immediately:
    /// registers when on, unregisters when off — no restart needed.</summary>
    public void ApplyEnabled()
    {
        if (_settingsService.Current.EnableGlobalHotkey)
        {
            if (!_registered) Register();
        }
        else if (_registered)
        {
            Unregister();
            _logger.LogInformation("Global hotkeys disabled by settings");
        }
    }

    private void UnregisterAllKeys()
    {
        try { _window?.UnregisterAll(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to unregister existing hotkeys"); }
    }

    public void Unregister()
    {
        if (!_registered) return;
        try
        {
            _window?.UnregisterAll();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to unregister global hotkeys");
        }
        _registered = false;
    }

    private void HandleHotkey(int hotkeyId)
    {
        switch (hotkeyId)
        {
            case HOTKEY_TOGGLE_CONNECT:
                ToggleConnectRequested?.Invoke();
                break;
            case HOTKEY_SHOW_WINDOW:
                ShowWindowRequested?.Invoke();
                break;
            case HOTKEY_SCREENSHOT:
                ScreenshotRequested?.Invoke();
                break;
        }
    }

    public void Dispose()
    {
        Unregister();
        _window?.Dispose();
        _window = null;
    }

    /// <summary>
    /// Hidden top-level window receiving WM_HOTKEY. A plain NativeWindow keeps this
    /// framework-neutral; the shell's message pump (WinForms or WPF with interop
    /// enabled) dispatches its messages because the window lives on the UI thread.
    /// </summary>
    private sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        private readonly List<int> _ids = [];

        public event Action<int>? HotkeyPressed;

        public HotkeyWindow()
        {
            CreateHandle(new CreateParams
            {
                Caption = "AkiSpaceHotkeys",
                Style = 0, // invisible overlapped window — enough to receive WM_HOTKEY
                ExStyle = 0,
                Parent = IntPtr.Zero,
                Width = 0,
                Height = 0,
            });
        }

        public bool Register(int id, uint modifiers, uint virtualKey)
        {
            if (!RegisterHotKey(Handle, id, modifiers, virtualKey)) return false;
            _ids.Add(id);
            return true;
        }

        public void UnregisterAll()
        {
            foreach (var id in _ids)
                UnregisterHotKey(Handle, id);
            _ids.Clear();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                HotkeyPressed?.Invoke(m.WParam.ToInt32());
                return;
            }
            base.WndProc(ref m);
        }

        public void Dispose() => DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
