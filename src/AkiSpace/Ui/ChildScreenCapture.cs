using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using AkiSpace.Native;

namespace AkiSpace.Ui;

/// <summary>
/// One-click clone-desktop screenshot: renders the RDP viewer window (the MSTSC
/// ActiveX surface, DWM-composited child content included) into a 32-bit DIB and
/// copies it to the HOST clipboard as Bitmap+DIB plus a PNG stream. Entirely
/// view-side — no IPC, no agent, no core-layer involvement; identical behaviour in
/// child-session and standard-RDP modes.
/// </summary>
internal static class ChildScreenCapture
{
    private const uint PW_RENDERFULLCONTENT = 0x00000002;
    private const int SRCCOPY = 0x00CC0020;

    /// <summary>
    /// Renders <paramref name="hwnd"/>'s current surface. Returns false when the
    /// window is gone, hung, or GDI refuses — the caller degrades to a status hint.
    /// </summary>
    public static bool TryCapture(IntPtr hwnd, out BitmapSource? bitmap)
    {
        bitmap = null;
        try
        {
            if (hwnd == IntPtr.Zero || !User32.IsWindow(hwnd)) return false;
            if (!User32.GetWindowRect(hwnd, out var rect)) return false;
            var width = rect.Width;
            var height = rect.Height;
            if (width <= 0 || height <= 0) return false;

            var windowDc = GetWindowDC(hwnd);
            if (windowDc == IntPtr.Zero) return false;
            try
            {
                var memDc = CreateCompatibleDC(windowDc);
                if (memDc == IntPtr.Zero) return false;
                try
                {
                    var bmi = new BITMAPINFO
                    {
                        bmiHeader = new BITMAPINFOHEADER
                        {
                            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                            biWidth = width,
                            biHeight = -height, // negative: top-down rows
                            biPlanes = 1,
                            biBitCount = 32,
                            biCompression = 0, // BI_RGB
                        },
                    };
                    var bits = IntPtr.Zero;
                    var dib = CreateDIBSection(memDc, ref bmi, 0 /* DIB_RGB_COLORS */, out bits, IntPtr.Zero, 0);
                    if (dib == IntPtr.Zero || bits == IntPtr.Zero) return false;
                    try
                    {
                        var previous = SelectObject(memDc, dib);
                        try
                        {
                            // PW_RENDERFULLCONTENT pulls DWM-composited surfaces — required
                            // for the MSTSC control, which renders into a DirectComposition
                            // child; a plain BitBlt of the window DC is the fallback.
                            var ok = PrintWindow(hwnd, memDc, PW_RENDERFULLCONTENT);
                            if (!ok)
                                ok = BitBlt(memDc, 0, 0, width, height, windowDc, 0, 0, SRCCOPY);
                            if (!ok) return false;

                            var stride = width * 4;
                            var pixels = new byte[stride * height];
                            Marshal.Copy(bits, pixels, 0, pixels.Length);
                            // GDI leaves the alpha byte undefined; clipboard consumers
                            // treat it as transparency — force fully opaque.
                            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 0xFF;

                            var source = BitmapSource.Create(
                                width, height, 96, 96,
                                System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride);
                            source.Freeze();
                            bitmap = source;
                            return true;
                        }
                        finally
                        {
                            SelectObject(memDc, previous);
                        }
                    }
                    finally
                    {
                        DeleteObject(dib);
                    }
                }
                finally
                {
                    DeleteDC(memDc);
                }
            }
            finally
            {
                ReleaseDC(hwnd, windowDc);
            }
        }
        catch (Exception)
        {
            bitmap = null;
            return false;
        }
    }

    /// <summary>
    /// Puts the capture on the clipboard in two formats at once: Bitmap+DIB
    /// (Paint/Office/Explorer paste) and the "PNG" registered stream (chat tools
    /// that prefer it). copy:true detaches ownership so the data outlives this
    /// process even after exit.
    /// </summary>
    public static bool CopyToClipboard(BitmapSource bitmap)
    {
        try
        {
            var data = new System.Windows.DataObject();
            data.SetImage(bitmap);

            // Intentionally not disposed: the clipboard owns the stream once
            // SetDataObject(copy:true) has flushed it into the OS store.
            var png = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(png);
            png.Position = 0;
            data.SetData("PNG", png);

            System.Windows.Clipboard.SetDataObject(data, copy: true);
            return true;
        }
        catch (Exception)
        {
            // The clipboard may be held open by another process; the caller shows a
            // failure hint and the user can simply retry.
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors; // single RGBQUAD placeholder — unused at 32bpp
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight,
        IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);
}
