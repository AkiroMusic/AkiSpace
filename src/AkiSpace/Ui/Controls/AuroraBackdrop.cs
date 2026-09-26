using System.Windows.Controls;

// WinForms types are globally imported (UseWindowsForms); alias the WPF types.
using Control = System.Windows.Controls.Control;

namespace AkiSpace.Ui.Controls;

/// <summary>
/// Window backdrop: the aurora curtain (four ramp-colored glows over the base
/// color) plus the global grain overlay. Purely decorative — never focusable,
/// never hit-testable above its layers.
/// </summary>
public class AuroraBackdrop : Control
{
    public AuroraBackdrop()
    {
        Focusable = false;
    }
}
