using System.Runtime.InteropServices;
using AkiSpace.Services;
using Microsoft.Extensions.Logging;

namespace AkiSpace.App;

/// <summary>
/// Global hotkeys (Ctrl+Shift+D toggle connect, Ctrl+Alt+Space show window),
/// extracted from MainForm. Registers on its own hidden Win32 window instead of
/// the shell's window, so the behaviour is identical under the WinForms and WPF
/// message pumps and the hotkeys survive the shell being hidden to the tray.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_TOGGLE_CONNECT = 1;
    private const int HOTKEY_SHOW_WINDOW = 2;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_ALT = 0x0001;
    private const uint VK_D = 0x44;
    private const uint VK_SPACE = 0x20;

    private readonly ILogger<HotkeyManager> _logger;
    private readonly SettingsService _settingsService;
    private HotkeyWindow? _window;
    private bool _registered;

    public event Action? ToggleConnectRequested;
    public event Action? ShowWindowRequested;

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
            _window = new HotkeyWindow();
            _window.HotkeyPressed += HandleHotkey;

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

            _registered = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register global hotkeys");
        }
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
