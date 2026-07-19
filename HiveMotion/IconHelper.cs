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
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

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
    private const int IconSize = 256;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid, out IShellItemImageFactory ppv);

    public static ImageSource? ForWindow(IntPtr hWnd, Process process)
    {
        // Prefer the full-resolution icon group from the app's image (what Explorer shows),
        // rendered at 256px and downscaled by WPF — always crisp.
        string? path = TryGetModulePath(process);
        if (path != null)
        {
            var shellIcon = FromShellImageFactory(path);
            if (shellIcon != null)
                return shellIcon;
        }

        // Window-provided icons: large first (a 16px small icon scaled to 48 is the blur bug).
        IntPtr hIcon = NativeMethods.SendMessage(hWnd, NativeMethods.WM_GETICON, (IntPtr)NativeMethods.ICON_BIG, IntPtr.Zero);
        if (hIcon == IntPtr.Zero)
            hIcon = NativeMethods.GetClassLongPtr64(hWnd, NativeMethods.GCL_HICON);
        if (hIcon == IntPtr.Zero)
            hIcon = NativeMethods.SendMessage(hWnd, NativeMethods.WM_GETICON, (IntPtr)NativeMethods.ICON_SMALL2, IntPtr.Zero);
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

    public static ImageSource? ForExecutable(string executablePath)
    {
        if (Cache.TryGetValue(executablePath, out var cached))
            return cached;

        ImageSource? result = FromShellImageFactory(executablePath);

        if (result == null)
        {
            try
            {
                string path = executablePath;
                if (!Path.IsPathRooted(path))
                {
                    string systemPath = Path.Combine(Environment.SystemDirectory, path);
                    if (File.Exists(systemPath))
                        path = systemPath;
                }

                using (var icon = System.Drawing.Icon.ExtractAssociatedIcon(path))
                {
                    if (icon != null)
                        result = FromHIcon(icon.Handle);
                }
            }
            catch
            {
                // no icon available
            }
        }

        Cache[executablePath] = result;
        return result;
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

    private static ImageSource? FromShellImageFactory(string path)
    {
        if (Cache.TryGetValue(path, out var cached))
            return cached;

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

            factory.GetImage(new SIZE { cx = IconSize, cy = IconSize }, SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY, out hBitmap);
            if (hBitmap == IntPtr.Zero)
                return null;

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            Cache[path] = source;
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
