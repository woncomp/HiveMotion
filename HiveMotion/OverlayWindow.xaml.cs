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
        TaskGrid.PinToggleRequested += (_, cell) => PinToggleRequested?.Invoke(this, cell);
        TaskGrid.RevealRequested += (_, cell) => RevealRequested?.Invoke(this, cell);
        TaskGrid.CopyCommandRequested += (_, cell) => CopyCommandRequested?.Invoke(this, cell);
        KeyDown += (_, e) => TaskGrid.HandleWindowKeyDown(e);
    }

    public event EventHandler<HiveCell>? CellChosen;
    public event EventHandler? CloseRequested;
    public event EventHandler<HiveCell>? PinToggleRequested;
    public event EventHandler<HiveCell>? RevealRequested;
    public event EventHandler<HiveCell>? CopyCommandRequested;

    /// <summary>Rebuilds the cells in place after a pin change, without re-showing the overlay.</summary>
    public void UpdateCells(IReadOnlyList<HiveCell> cells) =>
        Dispatcher.BeginInvoke(() => TaskGrid.SetCells(cells));

    /// <summary>Modal in-overlay question; null action shows a dismiss-only notice.</summary>
    public void ShowConfirm(string message, string confirmText, Action? onConfirm) =>
        Dispatcher.BeginInvoke(() => TaskGrid.ShowConfirm(message, confirmText, onConfirm));

    /// <summary>Brief "copied" pill; the overlay stays open.</summary>
    public void ShowCopyToast() =>
        Dispatcher.BeginInvoke(() => TaskGrid.ShowCopyToast());

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
            Logger.Info($"ShowTaskGrid: {DescribeGeometry()}");
            // Capture the desktop BEFORE showing so the frosted-glass layer sees the real screen.
            TaskGrid.SetBackdrop(CaptureBlurredBackdrop(_screen));
            TaskGrid.SetCells(cells);
            // Hide the cursor and suspend hover/clicks until the user actually moves the mouse.
            TaskGrid.DisarmMouse();
            Show();
            // Plain Activate() is denied for a background process; the attach-input recipe is not.
            WindowManager.ActivateWindow(TaskGrid.OverlayHwnd);
            Focus();
            // Re-assert the top of the topmost band: when activation is denied, another
            // always-on-top window would otherwise cover the grid.
            NativeMethods.SetWindowPos(TaskGrid.OverlayHwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            // Windows may apply its own DPI-suggested rect on the cross-DPI hop; re-assert ours.
            Dispatcher.BeginInvoke(ApplyScreenBounds);
        });
    }

    private System.Windows.Forms.Screen _screen = System.Windows.Forms.Screen.PrimaryScreen!;

    public string DescribeGeometry() =>
        $"screen={_screen.DeviceName} bounds={_screen.Bounds} window=({Left},{Top},{Width},{Height})";

    /// <summary>
    /// Cover the screen that currently holds the mouse cursor.
    /// Position is set with SetWindowPos in PHYSICAL pixels: assigning WPF Left/Width in DIPs
    /// converts through the window's CURRENT monitor DPI, which lands the window in empty
    /// space when moving between monitors with different scaling.
    /// </summary>
    private void MoveToCursorScreen()
    {
        try
        {
            if (!NativeMethods.GetCursorPos(out var point))
                return;

            _screen = System.Windows.Forms.Screen.FromPoint(
                new System.Drawing.Point(point.x, point.y));
            ApplyScreenBounds();
        }
        catch
        {
            // fall back to wherever the window already is
        }
    }

    private void ApplyScreenBounds()
    {
        // EnsureHandle lets the first open position the window before its first Show.
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var b = _screen.Bounds;
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, b.Left, b.Top, b.Width, b.Height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
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
            TaskGrid.ArmMouse();
            Hide();
        });
    }
}
