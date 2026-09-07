namespace AkiSpace.Common;

/// <summary>
/// Centralized dark-theme color palette used by every form/dialog in AkiSpace.
/// Previously duplicated in MainForm, SetupDialog, SettingsDialog.
/// </summary>
internal static class Theme
{
    public static readonly Color Bg = Color.FromArgb(30, 30, 30);
    public static readonly Color Surface = Color.FromArgb(40, 40, 40);
    public static readonly Color Control = Color.FromArgb(50, 50, 50);
    public static readonly Color Border = Color.FromArgb(70, 70, 70);
    public static readonly Color Text = Color.FromArgb(220, 220, 220);
    public static readonly Color TextDim = Color.FromArgb(150, 150, 150);
    public static readonly Color AccentBlue = Color.FromArgb(0, 120, 215);
    public static readonly Color AccentGreen = Color.FromArgb(0, 180, 80);
    public static readonly Color AccentRed = Color.FromArgb(220, 50, 50);
    public static readonly Color AccentAmber = Color.FromArgb(200, 150, 30);
}
