using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HiveMotion;

public static class IconHelper
{
    private static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    private const int SIIGBF_BIGGERSIZEOK = 0x01;
    private const int SIIGBF_ICONONLY = 0x04;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid, out IShellItemImageFactory ppv);

    public static ImageSource? ForWindow(IntPtr hWnd, Process process)
    {
        // Request the overlay presentation size so Shell providers scale smaller
        // icon resources instead of returning them in a large transparent canvas.
        string? path = TryGetModulePath(process);
        if (path != null)
        {
            var shellIcon = ForExecutable(path);
            if (shellIcon != null)
                return shellIcon;
        }

        // Window-provided icons: large first (a 16px small icon scaled to 48 is the blur bug).
        IntPtr hIcon = GetWindowIcon(hWnd, NativeMethods.ICON_BIG);
        if (hIcon == IntPtr.Zero)
            hIcon = NativeMethods.GetClassLongPtr64(hWnd, NativeMethods.GCL_HICON);
        if (hIcon == IntPtr.Zero)
            hIcon = GetWindowIcon(hWnd, NativeMethods.ICON_SMALL2);
        if (hIcon == IntPtr.Zero)
            hIcon = NativeMethods.GetClassLongPtr64(hWnd, NativeMethods.GCL_HICONSM);
        if (hIcon != IntPtr.Zero)
            return FromHIcon(hIcon);

        return path == null ? null : ForExecutable(path);
    }

    public static ImageSource? ForProcess(Process process)
    {
        string? path = TryGetModulePath(process);
        return path == null ? null : ForExecutable(path);
    }

    /// <summary>Scanner callers share the asynchronous cache; they never extract an exe inline.</summary>
    public static ImageSource? ForExecutable(string executablePath)
    {
        var request = new IconRequest(ExecutablePath: executablePath);
        // The scan thread has no visual; the system DPI is the best synchronous estimate.
        double pixels = IconService.SystemFallbackPixels;
        var image = IconService.Shared.TryGetCached(request, pixels);
        IconService.Shared.Request(request, pixels, visible: false);
        return image;
    }

    /// <summary>Worker-only extraction at the requested pixel size. Versioning and caching belong to IconService.</summary>
    internal static ImageSource? LoadFile(string path, int pixels)
    {
        if (System.Windows.Application.Current?.Dispatcher.CheckAccess() == true)
            throw new InvalidOperationException("Icon extraction cannot execute on the UI thread.");
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".exe" or ".dll"))
            return extension is ".png" or ".ico" or ".jpg" or ".jpeg" or ".bmp"
                ? DecodeImageFile(path, pixels) : null;
        var result = FromShellImageFactory(path, pixels);
        if (result != null) return result;
        long start = Stopwatch.GetTimestamp();
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            return icon == null ? null : FromHIcon(icon.Handle);
        }
        catch { return null; }
        finally
        {
            if (Logger.IsVerboseEnabled)
                Logger.Info($"icon-fallback-extraction {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F1}ms thread={Environment.CurrentManagedThreadId}");
        }
    }

    private static ImageSource? DecodeImageFile(string path, int pixels)
    {
        try
        {
            // Probe dimensions on the worker; constrain the longer axis to the tier size.
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            bool landscape = frame.PixelWidth >= frame.PixelHeight;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            if (landscape) bitmap.DecodePixelWidth = pixels;
            else bitmap.DecodePixelHeight = pixels;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetModulePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Foreign windows may be hung; icon lookup must never hold up the scan.</summary>
    private static IntPtr GetWindowIcon(IntPtr hWnd, int iconType)
    {
        return NativeMethods.SendMessageTimeout(hWnd, NativeMethods.WM_GETICON, (IntPtr)iconType, IntPtr.Zero,
            NativeMethods.SMTO_ABORTIFHUNG, 100, out var icon) != IntPtr.Zero ? icon : IntPtr.Zero;
    }

    private static ImageSource? FromShellImageFactory(string path, int pixels)
    {
        long start = Stopwatch.GetTimestamp();
        IShellItemImageFactory? factory = null;
        IntPtr hBitmap = IntPtr.Zero;
        try
        {
            if (!Path.IsPathRooted(path))
            {
                string systemPath = Path.Combine(Environment.SystemDirectory, path);
                if (File.Exists(systemPath))
                    path = systemPath;
            }

            Guid iid = IID_IShellItemImageFactory;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory) != 0 || factory == null)
                return null;

            // Request the tier size so Shell providers scale to it instead of placing a
            // native-size glyph in a larger transparent canvas; bigger sources are fine.
            factory.GetImage(new SIZE { cx = pixels, cy = pixels }, SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY, out hBitmap);
            if (hBitmap == IntPtr.Zero)
                return null;

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
            if (factory != null)
                Marshal.ReleaseComObject(factory);
            if (Logger.IsVerboseEnabled)
                Logger.Info($"icon-shell-extraction {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F1}ms thread={Environment.CurrentManagedThreadId}");
        }
    }

    private static ImageSource FromHIcon(IntPtr hIcon)
    {
        var source = Imaging.CreateBitmapSourceFromHIcon(
            hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
    }
}
