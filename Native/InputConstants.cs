namespace AkiSpace.Native;

/// <summary>
/// Win32 constants for input simulation, raw input, and cursor management.
/// </summary>
internal static class InputConstants
{
    // ---- SendInput INPUT types ----
    public const uint INPUT_MOUSE = 0;

    // ---- MOUSEINPUT flags ----
    public const uint MOUSEEVENTF_MOVE = 0x0001;

    // ---- Raw Input ----
    public const uint RID_INPUT = 0x10000003;
    public const uint RIDEV_REMOVE = 0x00000001;
    public const uint RIDEV_INPUTSINK = 0x00000100;

    public const uint RIM_TYPEMOUSE = 0;
    public const uint RIM_TYPEKEYBOARD = 1;
    public const uint RIM_TYPEHID = 2;

    public const ushort MOUSE_MOVE_RELATIVE = 0;
    public const ushort MOUSE_MOVE_ABSOLUTE = 1;

    // ---- Virtual keys ----
    public const ushort VK_LMENU = 0xA4;
    public const ushort VK_RMENU = 0xA5;

    // ---- Windows messages ----
    public const int WM_INPUT = 0x00FF;
    public const int WM_MOUSEACTIVATE = 0x0021;

    // ---- GetAsyncKeyState ----
    public const short KEY_PRESSED = unchecked((short)0x8000);

    // ---- Scan codes (for IMsRdpClientNonScriptable.SendKeys) ----
    public const int ScanCodeExtendedFlag = 0x0100;
}
