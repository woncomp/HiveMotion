using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace HiveMotion;

/// <summary>Captures a static snapshot of a window's contents via PrintWindow.</summary>
public static class PreviewCapturer
{
    private const int TargetWidth = 560;

    public static ImageSource? Capture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || NativeMethods.IsIconic(hwnd))
            return null;

        if (!NativeMethods.GetWindowRect(hwnd, out var windowRect))
            return null;

        int width = windowRect.Right - windowRect.Left;
        int height = windowRect.Bottom - windowRect.Top;
        if (width < 40 || height < 40)
            return null;

        // Crop the invisible resize borders: DWM's visible frame rect within the window rect.
        var crop = new Rectangle(0, 0, width, height);
        if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out NativeMethods.RECT visible, 16) == 0)
        {
            crop = new Rectangle(
                Math.Max(0, visible.Left - windowRect.Left),
                Math.Max(0, visible.Top - windowRect.Top),
                Math.Min(width, visible.Right - visible.Left),
                Math.Min(height, visible.Bottom - visible.Top));
            if (crop.Width < 40 || crop.Height < 40)
                crop = new Rectangle(0, 0, width, height);
        }

        Bitmap? full = null;
        Bitmap? small = null;
        IntPtr hBitmap = IntPtr.Zero;
        try
        {
            full = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(full))
            {
                IntPtr hdc = g.GetHdc();
                bool printed = NativeMethods.PrintWindow(hwnd, hdc, NativeMethods.PW_RENDERFULLCONTENT);
                g.ReleaseHdc(hdc);
                if (!printed)
                {
                    IntPtr hdc2 = g.GetHdc();
                    printed = NativeMethods.PrintWindow(hwnd, hdc2, 0);
                    g.ReleaseHdc(hdc2);
                }
                if (!printed)
                    return null;
            }

            double scale = Math.Min(1.0, (double)TargetWidth / crop.Width);
            int targetW = Math.Max(1, (int)Math.Round(crop.Width * scale));
            int targetH = Math.Max(1, (int)Math.Round(crop.Height * scale));

            small = new Bitmap(targetW, targetH, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(full, new Rectangle(0, 0, targetW, targetH), crop, GraphicsUnit.Pixel);
            }

            hBitmap = small.GetHbitmap(System.Drawing.Color.FromArgb(0));
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero)
                NativeMethods.DeleteObject(hBitmap);
            full?.Dispose();
            small?.Dispose();
        }
    }
}
