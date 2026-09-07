using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AkiSpace.Common;

namespace AkiSpace.Controls.Styled;

/// <summary>
/// Noise overlay - SVG fractal noise pattern as background image.
/// Opacity 0.035, covers entire form above ambient background.
/// </summary>
public sealed class NoiseOverlay : PictureBox
{
    private Bitmap? _noiseBitmap;

    public NoiseOverlay()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        Dock = DockStyle.Fill;
        BackColor = Color.Transparent;
        SizeMode = PictureBoxSizeMode.Normal;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        GenerateNoiseBitmap();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_noiseBitmap != null)
        {
            // Tile the noise bitmap across the client area
            var g = e.Graphics;
            var rect = ClientRectangle;
            var tileSize = _noiseBitmap.Width;
            for (int y = 0; y < rect.Height; y += tileSize)
            {
                for (int x = 0; x < rect.Width; x += tileSize)
                {
                    var drawRect = new Rectangle(x, y, Math.Min(tileSize, rect.Width - x), Math.Min(tileSize, rect.Height - y));
                    g.DrawImage(_noiseBitmap, drawRect, 0, 0, drawRect.Width, drawRect.Height, GraphicsUnit.Pixel);
                }
            }
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // Regenerate if size changed significantly
        if (_noiseBitmap == null || _noiseBitmap.Width != 160 || _noiseBitmap.Height != 160)
        {
            GenerateNoiseBitmap();
        }
    }

    private void GenerateNoiseBitmap()
    {
        const int size = 160;
        _noiseBitmap?.Dispose();
        _noiseBitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        using var g = Graphics.FromImage(_noiseBitmap);
        g.Clear(Color.Transparent);

        // Generate fractal noise using Perlin-like approach
        var random = new Random(42); // Fixed seed for consistent pattern
        var pixels = new byte[size * size * 4];

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                // Multi-octave fractal noise
                float noise = 0f;
                float amplitude = 1f;
                float frequency = 0.05f;
                float maxAmplitude = 0f;

                for (int octave = 0; octave < 2; octave++)
                {
                    float sampleX = x * frequency;
                    float sampleY = y * frequency;
                    float value = PerlinNoise(sampleX, sampleY, random);
                    noise += value * amplitude;
                    maxAmplitude += amplitude;
                    amplitude *= 0.5f;
                    frequency *= 2f;
                }

                noise = noise / maxAmplitude;
                noise = (noise + 1f) / 2f; // Normalize to 0-1

                var alpha = (byte)(noise * 255 * ThemeTokens.NoiseOpacity);
                var idx = (y * size + x) * 4;
                pixels[idx] = 255;     // B
                pixels[idx + 1] = 255; // G
                pixels[idx + 2] = 255; // R
                pixels[idx + 3] = alpha; // A
            }
        }

        var rect = new Rectangle(0, 0, size, size);
        var bmpData = _noiseBitmap.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, _noiseBitmap.PixelFormat);
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bmpData.Scan0, pixels.Length);
        _noiseBitmap.UnlockBits(bmpData);
    }

    private static float PerlinNoise(float x, float y, Random random)
    {
        // Simple value noise
        int xi = (int)Math.Floor(x) & 255;
        int yi = (int)Math.Floor(y) & 255;
        float xf = x - (int)Math.Floor(x);
        float yf = y - (int)Math.Floor(y);

        float u = Fade(xf);
        float v = Fade(yf);

        // Use random for gradient directions
        int aa = random.Next(256);
        int ab = random.Next(256);
        int ba = random.Next(256);
        int bb = random.Next(256);

        float x1 = Lerp(Grad(aa, xf, yf), Grad(ba, xf - 1, yf), u);
        float x2 = Lerp(Grad(ab, xf, yf - 1), Grad(bb, xf - 1, yf - 1), u);

        return Lerp(x1, x2, v);
    }

    private static float Fade(float t) => t * t * t * (t * (t * 6 - 15) + 10);
    private static float Lerp(float a, float b, float t) => a + t * (b - a);
    private static float Grad(int hash, float x, float y)
    {
        var h = hash & 3;
        var u = h < 2 ? x : y;
        var v = h < 2 ? y : x;
        return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _noiseBitmap?.Dispose();
            _noiseBitmap = null;
        }
        base.Dispose(disposing);
    }
}