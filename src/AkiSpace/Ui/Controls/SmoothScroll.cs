using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

// WinForms types are globally imported (UseWindowsForms); alias the WPF types.
using Control = System.Windows.Controls.Control;

namespace AkiSpace.Ui.Controls;

/// <summary>
/// Smooth mouse-wheel scrolling for ScrollViewer. WPF's default wheel handling
/// jumps a coarse fixed step per notch; this attached behavior intercepts the
/// wheel and eases the offset toward a wheel-proportional target. Fast wheels
/// re-anchor on the live offset, so bursts feel fluid instead of stacking
/// lagging animations.
/// </summary>
public static class SmoothScroll
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(SmoothScroll),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer viewer) return;
        if ((bool)e.NewValue)
        {
            viewer.PreviewMouseWheel += OnPreviewMouseWheel;
        }
        else
        {
            viewer.PreviewMouseWheel -= OnPreviewMouseWheel;
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var viewer = (ScrollViewer)sender;
        if (viewer.ScrollableHeight <= 0) return;

        e.Handled = true;

        // One notch (delta 120) eases ~100px —
        // animated, so the perceived speed matches without the snap.
        var target = Math.Max(0, Math.Min(viewer.ScrollableHeight, viewer.VerticalOffset - e.Delta * 0.85));
        if (Math.Abs(target - viewer.VerticalOffset) < 0.5) return;

        var easer = GetEaser(viewer);
        easer.BeginAnimation(ScrollEaser.OffsetProperty, new DoubleAnimation
        {
            From = viewer.VerticalOffset,
            To = target,
            Duration = TimeSpan.FromMilliseconds(280),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private static readonly DependencyProperty ScrollEaserProperty =
        DependencyProperty.RegisterAttached(
            "ScrollEaser", typeof(ScrollEaser), typeof(SmoothScroll), new PropertyMetadata(null));

    private static ScrollEaser GetEaser(ScrollViewer viewer)
    {
        if (viewer.GetValue(ScrollEaserProperty) is not ScrollEaser easer)
        {
            easer = new ScrollEaser(viewer);
            viewer.SetValue(ScrollEaserProperty, easer);
        }
        return easer;
    }

    /// <summary>
    /// Animatable offset shim: animating a plain double and forwarding each tick
    /// to ScrollToVerticalOffset keeps the wheel motion eased without fighting
    /// WPF's scroll-animation limitations. HoldEnd pins the final value until the
    /// next wheel event replaces the clock.
    /// </summary>
    private sealed class ScrollEaser : Animatable
    {
        public static readonly DependencyProperty OffsetProperty =
            DependencyProperty.Register(
                "Offset", typeof(double), typeof(ScrollEaser),
                new PropertyMetadata(0.0, (d, e) => ((ScrollEaser)d)._viewer.ScrollToVerticalOffset((double)e.NewValue)));

        private readonly ScrollViewer _viewer;

        public ScrollEaser(ScrollViewer viewer) => _viewer = viewer;

        protected override Freezable CreateInstanceCore() => new ScrollEaser(_viewer);
    }
}
