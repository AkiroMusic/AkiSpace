using System.Windows.Controls;

// WinForms types are globally imported (UseWindowsForms); alias the WPF types.
using Control = System.Windows.Controls.Control;

namespace AkiSpace.Ui.Controls;

/// <summary>
/// Unified content card shell: liquid glass fill (translucent surface + top
/// specular line + bottom inner shade + diagonal light sweep) inside a
/// double bezel (outer border + recessed inner hairline), with a soft tinted
/// shadow. Padding defaults to the 24px card inset.
/// </summary>
public class GlassCard : ContentControl
{
    public GlassCard()
    {
        Focusable = false;
    }
}
