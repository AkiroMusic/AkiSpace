using System.Drawing;
using System.Drawing.Drawing2D;

namespace AkiSpace.Controls.Styled;

/// <summary>
/// Shared drawing helpers for the Ethereal Glass design system.
/// </summary>
internal static class DrawHelpers
{
    /// <summary>
    /// Creates a rounded-rectangle GraphicsPath. Clamps the diameter to
    /// min(width, height) to prevent self-intersecting paths.
    /// </summary>
    public static GraphicsPath GetRoundedRect(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        if (d > rect.Width) d = rect.Width;
        if (d > rect.Height) d = rect.Height;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Linearly interpolates between two colors. t is clamped to [0, 1].
    /// </summary>
    public static Color InterpolateColor(Color from, Color to, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (int)(from.A + (to.A - from.A) * t),
            (int)(from.R + (to.R - from.R) * t),
            (int)(from.G + (to.G - from.G) * t),
            (int)(from.B + (to.B - from.B) * t)
        );
    }

    /// <summary>
    /// Interpolates an array of shadow colors (tight + diffuse) between
    /// two sets. Extends the shorter array by repeating its last element.
    /// </summary>
    public static Color[] InterpolateShadows(Color[] from, Color[] to, float t)
    {
        var result = new Color[Math.Max(from.Length, to.Length)];
        for (int i = 0; i < result.Length; i++)
        {
            var f = i < from.Length ? from[i] : from[^1];
            var tt = i < to.Length ? to[i] : to[^1];
            result[i] = InterpolateColor(f, tt, t);
        }
        return result;
    }

    /// <summary>
    /// Draws a dual-layer (tight + diffuse) shadow by simulating blur with
    /// multiple offset layers of the same rounded-rect path.
    /// </summary>
    public static void DrawShadow(Graphics g, Rectangle rect, Color[] colors, int radius, int translateY = 0)
    {
        if (colors.Length < 2) return;

        // Tight shadow (small offset)
        var shadowRect = new Rectangle(
            rect.X,
            rect.Y + translateY + 1,
            rect.Width,
            rect.Height
        );
        using var tightBrush = new SolidBrush(Color.FromArgb(colors[0].A, colors[0]));
        for (int i = 1; i <= 2; i++)
        {
            var offsetRect = new Rectangle(shadowRect.X, shadowRect.Y + i, shadowRect.Width, shadowRect.Height);
            using var p = GetRoundedRect(offsetRect, radius);
            g.FillPath(tightBrush, p);
        }

        // Diffuse shadow (large offset, larger expansion)
        var diffuseRect = new Rectangle(
            rect.X - 4,
            rect.Y + translateY + 4,
            rect.Width + 8,
            rect.Height + 8
        );
        using var diffuseBrush = new SolidBrush(Color.FromArgb(colors[1].A, colors[1]));
        for (int i = 4; i <= 16; i += 4)
        {
            var offsetRect = new Rectangle(diffuseRect.X, diffuseRect.Y + i, diffuseRect.Width, diffuseRect.Height);
            using var p = GetRoundedRect(offsetRect, radius);
            g.FillPath(diffuseBrush, p);
        }
    }
}
