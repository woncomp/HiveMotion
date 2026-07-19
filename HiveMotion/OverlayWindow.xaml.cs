using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace HiveMotion;

public partial class OverlayWindow : Window
{
    public OverlayWindow()
    {
        InitializeComponent();

        TaskGrid.CellChosen += (_, cell) => CellChosen?.Invoke(this, cell);
        TaskGrid.CloseRequested += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        KeyDown += (_, e) => TaskGrid.HandleWindowKeyDown(e);
    }

    public event EventHandler<HiveCell>? CellChosen;
    public event EventHandler? CloseRequested;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var helper = new WindowInteropHelper(this);

        // Hide from Alt+Tab but take real keyboard focus when shown (IME + default editing work).
        int exStyle = NativeMethods.GetWindowLong(helper.Handle, NativeMethods.GWL_EXSTYLE);
        exStyle = (exStyle | NativeMethods.WS_EX_TOOLWINDOW) & ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLong(helper.Handle, NativeMethods.GWL_EXSTYLE, exStyle);

        TaskGrid.OverlayHwnd = helper.Handle;
    }

    /// <summary>Three separable box-blur passes approximate a Gaussian; radius in pixels.</summary>
    private static void ApplyBoxBlur(System.Drawing.Bitmap bmp, int radius)
    {
        var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            int size = Math.Abs(stride) * bmp.Height;
            byte[] pixels = new byte[size];
            byte[] buffer = new byte[size];
            Marshal.Copy(data.Scan0, pixels, 0, size);

            for (int pass = 0; pass < 3; pass++)
            {
                BoxBlurHorizontal(pixels, buffer, bmp.Width, bmp.Height, stride, radius);
                BoxBlurVertical(buffer, pixels, bmp.Width, bmp.Height, stride, radius);
            }

            Marshal.Copy(pixels, 0, data.Scan0, size);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private static void BoxBlurHorizontal(byte[] src, byte[] dst, int width, int height, int stride, int radius)
    {
        int div = radius * 2 + 1;
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int c = 0; c < 4; c++)
            {
                int sum = 0;
                for (int x = -radius; x <= radius; x++)
                    sum += src[row + Math.Clamp(x, 0, width - 1) * 4 + c];
                for (int x = 0; x < width; x++)
                {
                    dst[row + x * 4 + c] = (byte)(sum / div);
                    int add = Math.Clamp(x + radius + 1, 0, width - 1);
                    int sub = Math.Clamp(x - radius, 0, width - 1);
                    sum += src[row + add * 4 + c] - src[row + sub * 4 + c];
                }
            }
        }
    }

    private static void BoxBlurVertical(byte[] src, byte[] dst, int width, int height, int stride, int radius)
    {
        int div = radius * 2 + 1;
        for (int x = 0; x < width; x++)
        {
            for (int c = 0; c < 4; c++)
            {
                int sum = 0;
                for (int y = -radius; y <= radius; y++)
                    sum += src[Math.Clamp(y, 0, height - 1) * stride + x * 4 + c];
                for (int y = 0; y < height; y++)
                {
                    dst[y * stride + x * 4 + c] = (byte)(sum / div);
                    int add = Math.Clamp(y + radius + 1, 0, height - 1);
                    int sub = Math.Clamp(y - radius, 0, height - 1);
                    sum += src[add * stride + x * 4 + c] - src[sub * stride + x * 4 + c];
                }
            }
        }
    }

    public void ShowTaskGrid(IReadOnlyList<HiveCell> cells)
    {
        Dispatcher.BeginInvoke(() =>
        {
            MoveToCursorScreen();
            // Capture the desktop BEFORE showing so the frosted-glass layer sees the real screen.
            TaskGrid.SetBackdrop(CaptureBlurredBackdrop(_screen));
            TaskGrid.SetCells(cells);
            Show();
            // Plain Activate() is denied for a background process; the attach-input recipe is not.
            WindowManager.ActivateWindow(TaskGrid.OverlayHwnd);
            Focus();
        });
    }

    private System.Windows.Forms.Screen _screen = System.Windows.Forms.Screen.PrimaryScreen!;

    /// <summary>Cover the screen that currently holds the mouse cursor (multi-monitor aware).</summary>
    private void MoveToCursorScreen()
    {
        try
        {
            if (!NativeMethods.GetCursorPos(out var point))
                return;

            _screen = System.Windows.Forms.Screen.FromPoint(
                new System.Drawing.Point(point.x, point.y));

            uint dpiX = 96, dpiY = 96;
            var monitor = NativeMethods.MonitorFromPoint(point, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
                NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out dpiX, out dpiY);
            if (dpiX == 0) dpiX = 96;
            if (dpiY == 0) dpiY = 96;

            Left = _screen.Bounds.Left * 96.0 / dpiX;
            Top = _screen.Bounds.Top * 96.0 / dpiY;
            Width = _screen.Bounds.Width * 96.0 / dpiX;
            Height = _screen.Bounds.Height * 96.0 / dpiY;
        }
        catch
        {
            // fall back to wherever the window already is
        }
    }

    /// <summary>
    /// Snapshot one display via its own device DC + BitBlt. Going through the monitor's
    /// device (instead of virtual-screen coordinates) survives multi-monitor DPI mixes
    /// and odd display layouts where CopyFromScreen returns black.
    /// </summary>
    private static System.Windows.Media.ImageSource? CaptureBlurredBackdrop(System.Windows.Forms.Screen screen)
    {
        int width = screen.Bounds.Width;
        int height = screen.Bounds.Height;

        IntPtr hdcScreen = IntPtr.Zero;
        IntPtr hdcMem = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr hOld = IntPtr.Zero;
        System.Drawing.Bitmap? full = null;
        System.Drawing.Bitmap? small = null;
        IntPtr hBitmapSmall = IntPtr.Zero;
        try
        {
            hdcScreen = NativeMethods.CreateDC(screen.DeviceName, null, null, IntPtr.Zero);
            if (hdcScreen == IntPtr.Zero)
                return null;
            hdcMem = NativeMethods.CreateCompatibleDC(hdcScreen);
            hBitmap = NativeMethods.CreateCompatibleBitmap(hdcScreen, width, height);
            hOld = NativeMethods.SelectObject(hdcMem, hBitmap);
            if (!NativeMethods.BitBlt(hdcMem, 0, 0, width, height, hdcScreen, 0, 0, NativeMethods.SRCCOPY))
                return null;

            full = System.Drawing.Image.FromHbitmap(hBitmap);

            // Quarter-size + stretch gives most of the blur; bake the rest in with a CPU box blur
            // (a shader BlurEffect re-evaluates on dirty regions and leaves rectangular seams).
            small = new System.Drawing.Bitmap(width / 4, height / 4, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(small))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(full, 0, 0, small.Width, small.Height);
            }

            ApplyBoxBlur(small, 12);

            hBitmapSmall = small.GetHbitmap(System.Drawing.Color.FromArgb(0));
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmapSmall, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hOld != IntPtr.Zero)
                NativeMethods.SelectObject(hdcMem, hOld);
            if (hBitmap != IntPtr.Zero)
                NativeMethods.DeleteObject(hBitmap);
            if (hdcMem != IntPtr.Zero)
                NativeMethods.DeleteDC(hdcMem);
            if (hdcScreen != IntPtr.Zero)
                NativeMethods.DeleteDC(hdcScreen);
            if (hBitmapSmall != IntPtr.Zero)
                NativeMethods.DeleteObject(hBitmapSmall);
            full?.Dispose();
            small?.Dispose();
        }
    }

    public void HideOverlay()
    {
        Dispatcher.BeginInvoke(() =>
        {
            TaskGrid.ResetPreview();
            Hide();
        });
    }
}
