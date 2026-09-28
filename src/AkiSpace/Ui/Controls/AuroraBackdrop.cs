using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

// WinForms types are globally imported (UseWindowsForms); alias the WPF types.
using Control = System.Windows.Controls.Control;

namespace AkiSpace.Ui.Controls;

/// <summary>
/// Window backdrop: the four background layers of the design spec — the aurora
/// curtain (four ramp-colored glows over the base color), the slowly orbiting
/// aurora sweep, the star-dust tile and the grain overlay. Purely decorative —
/// never focusable, never hit-testable above its layers. The glows breathe and
/// the sweep orbits only while the user has not disabled system client-area
/// animation.
/// </summary>
public class AuroraBackdrop : Control
{
    public AuroraBackdrop()
    {
        Focusable = false;
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        BeginAuroraMotion();
    }

    private void BeginAuroraMotion()
    {
        // Honors the system "animate controls" setting (the prefers-reduced-motion
        // equivalent on Windows); without it the static aurora stays fully present.
        if (!SystemParameters.ClientAreaAnimation) return;

        // Breathing glows: each aurora blob fades between 72% and 100% of its pack
        // intensity on its own slow cycle, so the curtain drifts instead of pulsing.
        var durations = new[] { 11, 13, 17, 19 };
        var offsets = new[] { 0.0, 3.0, 6.0, 9.0 };
        for (var i = 0; i < 4; i++)
        {
            if (GetTemplateChild($"PART_Glow{i + 1}") is not FrameworkElement glow) continue;
            var breathe = new DoubleAnimation(0.72, 1.0, TimeSpan.FromSeconds(durations[i]))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromSeconds(offsets[i]),
                EasingFunction = new SineEase(),
            };
            glow.BeginAnimation(OpacityProperty, breathe);
        }

        // Aurora sweep: one full orbit every 90 s, constant speed — linear is the
        // only legal curve for a loop without start or end (design spec §7.3).
        if (GetTemplateChild("PART_SweepRotate") is RotateTransform rotate)
        {
            rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(90))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            });
        }
    }
}
