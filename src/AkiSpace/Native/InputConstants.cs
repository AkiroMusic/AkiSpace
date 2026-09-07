namespace AkiSpace.Native;

/// <summary>
/// Win32 constants for input simulation, raw input, and cursor management.
/// </summary>
internal static class InputConstants
{
    // ---- SendInput INPUT types ----
    public const uint INPUT_MOUSE = 0;
    public const uint INPUT_KEYBOARD = 1;
    public const uint INPUT_HARDWARE = 2;

    // ---- MOUSEINPUT flags ----
    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    public const uint MOUSEEVENTF_XDOWN = 0x0080;
    public const uint MOUSEEVENTF_XUP = 0x0100;
    public const uint MOUSEEVENTF_WHEEL = 0x0800;
    public const uint MOUSEEVENTF_HWHEEL = 0x1000;
    public const uint MOUSEEVENTF_MOVE_NOCOALESCE = 0x2000;
    public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    // ---- KEYBDINPUT flags ----
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_SCANCODE = 0x0008;
    public const uint KEYEVENTF_UNICODE = 0x0004;

    // ---- Raw Input ----
    public const uint RID_INPUT = 0x10000003;
    public const uint RIDEV_REMOVE = 0x00000001;
    public const uint RIDEV_INPUTSINK = 0x00000100;
    public const uint RIDEV_CAPTUREMOUSE = 0x00000200;

    public const uint RIM_TYPEMOUSE = 0;
    public const uint RIM_TYPEKEYBOARD = 1;
    public const uint RIM_TYPEHID = 2;

    public const ushort MOUSE_MOVE_RELATIVE = 0;
    public const ushort MOUSE_MOVE_ABSOLUTE = 1;
    public const ushort MOUSE_VIRTUAL_DESKTOP = 0x02;
    public const ushort MOUSE_ATTRIBUTES_CHANGED = 0x04;
    public const ushort MOUSE_MOVE_NOCOALESCE = 0x08;

    // ---- Virtual keys ----
    public const ushort VK_LMENU = 0xA4;
    public const ushort VK_RMENU = 0xA5;
    public const ushort VK_LWIN = 0x5B;
    public const ushort VK_RWIN = 0x5C;
    public const ushort VK_TAB = 0x09;
    public const ushort VK_D = 0x44;

    // ---- Windows messages ----
    public const int WM_INPUT = 0x00FF;
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int WM_SYSCOMMAND = 0x0112;
    public const int SC_MINIMIZE = 0xF020;

    // ---- ShowWindow / GetAsyncKeyState ----
    public const short KEY_PRESSED = unchecked((short)0x8000);

    // ---- SetWinEventHook / GUI thread ----
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    // ---- Scan codes (for IMsRdpClientNonScriptable.SendKeys) ----
    public const int ScanCodeExtendedFlag = 0x0100;
    public const int ScanCodeLeftWin = 0x5B;   // extended
    public const int ScanCodeTab = 0x0F;
    public const int ScanCodeD = 0x20;
    public const int ScanCodeLeftCtrl = 0x1D;
    public const int ScanCodeLeftAlt = 0x38;
    public const int ScanCodeDelete = 0x53;
}