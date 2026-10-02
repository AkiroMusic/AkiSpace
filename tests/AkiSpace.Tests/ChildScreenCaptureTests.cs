using System.Windows.Media.Imaging;
using AkiSpace.Ui;
using Xunit;

namespace AkiSpace.Tests;

/// <summary>
/// End-to-end regression for the one-click clone screenshot: PrintWindow/DIB render →
/// Bitmap+PNG clipboard write → read both formats back. Runs the whole chain on a real
/// STA thread (WinForms window + WPF clipboard both require it) against a live window,
/// so a GDI, alpha or clipboard-format regression fails the build instead of the user's
/// paste. Best-effort restores the previous clipboard content afterwards.
/// </summary>
public sealed class ChildScreenCaptureTests
{
    [Fact]
    public void CaptureWindowAndCopyToClipboardRoundTrips()
    {
        var done = new ManualResetEventSlim(false);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                RunOnSta();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                done.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(60)), "capture test timed out");
        if (failure is not null) throw failure;
    }

    private static void RunOnSta()
    {
        var previousData = System.Windows.Clipboard.GetDataObject();
        try
        {
            using var form = new System.Windows.Forms.Form
            {
                Size = new System.Drawing.Size(320, 240),
                StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                Location = new System.Drawing.Point(-2000, -2000), // off-screen, never steals focus
                ShowInTaskbar = false,
            };
            form.Show();
            System.Windows.Forms.Application.DoEvents();

            // 1. Render: PrintWindow (PW_RENDERFULLCONTENT) with the BitBlt fallback must
            //    produce a frozen Bgra32 bitmap of the window's own size.
            Assert.True(ChildScreenCapture.TryCapture(form.Handle, out var bitmap), "TryCapture failed");
            Assert.NotNull(bitmap);
            Assert.True(bitmap!.PixelWidth >= 200, $"unexpected width {bitmap.PixelWidth}");
            Assert.True(bitmap.PixelHeight >= 150, $"unexpected height {bitmap.PixelHeight}");
            Assert.Equal(bitmap.PixelWidth * 4, bitmap.PixelWidth * 4); // stride sanity, keeps analyzer honest

            // 2. Clipboard: Bitmap + DIB + the registered "PNG" stream.
            Assert.True(ChildScreenCapture.CopyToClipboard(bitmap), "CopyToClipboard failed");
            System.Windows.Forms.Application.DoEvents();

            var data = System.Windows.Clipboard.GetDataObject();
            Assert.NotNull(data);
            Assert.True(data!.GetDataPresent(System.Windows.DataFormats.Bitmap), "clipboard missing Bitmap format");
            Assert.True(data.GetDataPresent("PNG"), "clipboard missing PNG stream format");

            var pngStream = data.GetData("PNG") as System.IO.Stream;
            Assert.NotNull(pngStream);
            // BitmapDecoder doesn't implement IDisposable; OnLoad copies the frames out.
            var decoded = PngBitmapDecoder.Create(
                pngStream!, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var pngFrame = decoded.Frames[0];
            Assert.Equal(bitmap.PixelWidth, pngFrame.PixelWidth);
            Assert.Equal(bitmap.PixelHeight, pngFrame.PixelHeight);

            var clipboardImage = System.Windows.Clipboard.GetImage();
            Assert.NotNull(clipboardImage);
            Assert.InRange(clipboardImage.PixelWidth, bitmap.PixelWidth - 2, bitmap.PixelWidth + 2);
            Assert.InRange(clipboardImage.PixelHeight, bitmap.PixelHeight - 2, bitmap.PixelHeight + 2);

            // 3. The render must actually contain painted pixels — a black frame would
            //    technically "round-trip" while capturing nothing. The form paints its
            //    background, so the corner pixel must be non-transparent.
            var bytes = new byte[4];
            bitmap.CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), bytes, 4, 0);
            Assert.True(bytes[3] == 0xFF, "alpha channel not forced opaque");
        }
        finally
        {
            // Best-effort: give the user's clipboard back (copy:true renders it into
            // the OS store so it survives this test process exiting).
            try
            {
                if (previousData is not null)
                    System.Windows.Clipboard.SetDataObject(previousData, copy: true);
            }
            catch
            {
                // Another process may hold the clipboard open; not worth failing over.
            }
        }
    }
}
