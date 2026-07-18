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
    }

    public event EventHandler<HiveCell>? CellChosen;
    public event EventHandler? CloseRequested;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var helper = new WindowInteropHelper(this);

        // Hide from Alt+Tab and never steal activation from the window we are switching away from.
        int exStyle = NativeMethods.GetWindowLong(helper.Handle, NativeMethods.GWL_EXSTYLE);
        exStyle = (exStyle | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE) & ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLong(helper.Handle, NativeMethods.GWL_EXSTYLE, exStyle);

        TryEnableAcrylic(helper.Handle);
    }

    /// <summary>Frosted backdrop like the demo's blur(52px) layer; a dim border inside the view is the fallback tint.</summary>
    private static void TryEnableAcrylic(IntPtr hwnd)
    {
        try
        {
            var policy = new NativeMethods.AccentPolicy
            {
                AccentState = NativeMethods.AccentState.EnableAcrylicBlurBehind,
                AccentFlags = 0,
                GradientColor = 0x20101A26, // ABGR: faint honey-blue tint over the blurred desktop
                AnimationId = 0
            };

            int size = Marshal.SizeOf(policy);
            IntPtr policyPtr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, policyPtr, false);
                var data = new NativeMethods.WindowCompositionAttributeData
                {
                    Attribute = NativeMethods.WCA_ACCENT_POLICY,
                    Data = policyPtr,
                    SizeOfData = size
                };
                NativeMethods.SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(policyPtr);
            }
        }
        catch
        {
            // Older Windows without acrylic: the view's own dim layer still renders correctly.
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

            // Quarter-size + stretch gives most of the blur; the view adds a BlurEffect on top.
            small = new System.Drawing.Bitmap(bounds.Width / 4, bounds.Height / 4, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(small))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(full, 0, 0, small.Width, small.Height);
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

    public void EnterSearch() => Dispatcher.BeginInvoke(TaskGrid.EnterSearch);
    public void ExitSearch() => Dispatcher.BeginInvoke(TaskGrid.ExitSearch);
    public void AppendSearchChar(char c) => Dispatcher.BeginInvoke(() => TaskGrid.AppendSearchChar(c));
    public void SearchBackspace() => Dispatcher.BeginInvoke(TaskGrid.SearchBackspace);
    public void MoveSearchHighlight(int delta) => Dispatcher.BeginInvoke(() => TaskGrid.MoveSearchHighlight(delta));
    public void SubmitSearch() => Dispatcher.BeginInvoke(TaskGrid.SubmitSearch);

    public void HideOverlay()
    {
        Dispatcher.BeginInvoke(Hide);
    }
}
