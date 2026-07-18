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
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;

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
            // Capture the desktop BEFORE showing so the frosted-glass layer sees the real screen.
            TaskGrid.SetBackdrop(CaptureBlurredBackdrop());
            TaskGrid.SetCells(cells);
            Show();
            // Plain Activate() is denied for a background process; the attach-input recipe is not.
            WindowManager.ActivateWindow(TaskGrid.OverlayHwnd);
            Focus();
        });
    }

    private static System.Windows.Media.ImageSource? CaptureBlurredBackdrop()
    {
        System.Drawing.Bitmap? full = null;
        System.Drawing.Bitmap? small = null;
        IntPtr hBitmap = IntPtr.Zero;
        try
        {
            var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            full = new System.Drawing.Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(full))
                g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);

            // Quarter-size + stretch gives most of the blur; bake the rest in with a CPU box blur
            // (a shader BlurEffect re-evaluates on dirty regions and leaves rectangular seams).
            small = new System.Drawing.Bitmap(bounds.Width / 4, bounds.Height / 4, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(small))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(full, 0, 0, small.Width, small.Height);
            }

            ApplyBoxBlur(small, 12);

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

    public void HideOverlay()
    {
        Dispatcher.BeginInvoke(() =>
        {
            TaskGrid.ResetPreview();
            Hide();
        });
    }
}
