using System;

namespace HiveMotion;

/// <summary>Live window preview drawn by DWM straight into our overlay window — the same
/// mechanism as taskbar peek: native resolution, works for minimized windows.</summary>
internal sealed class DwmThumbnailPreview
{
    private IntPtr _thumbnail;

    public IntPtr CurrentSource { get; private set; }

    /// <summary>
    /// Creates the DWM relationship and obtains the source thumbnail size before WPF
    /// arranges its destination viewport. The size is authoritative for minimized
    /// windows, whose restore rectangle can differ from DWM's cached image.
    /// </summary>
    public bool TryRegister(IntPtr destHwnd, IntPtr sourceHwnd, out NativeMethods.SIZE sourceSize)
    {
        Hide();
        sourceSize = default;

        if (NativeMethods.DwmRegisterThumbnail(destHwnd, sourceHwnd, out _thumbnail) != 0 ||
            _thumbnail == IntPtr.Zero)
            return false;

        CurrentSource = sourceHwnd;
        NativeMethods.DwmQueryThumbnailSourceSize(_thumbnail, out sourceSize);
        return true;
    }

    /// <summary>Displays the registered thumbnail in overlay client coordinates (physical pixels).</summary>
    public bool Show(NativeMethods.RECT destRect)
    {
        if (_thumbnail == IntPtr.Zero)
            return false;

        var props = new NativeMethods.DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = NativeMethods.DWM_TNP_RECTDESTINATION
                    | NativeMethods.DWM_TNP_VISIBLE
                    | NativeMethods.DWM_TNP_OPACITY,
            rcDestination = destRect,
            opacity = 255,
            fVisible = true
        };
        if (NativeMethods.DwmUpdateThumbnailProperties(_thumbnail, ref props) != 0)
        {
            Hide();
            return false;
        }
        return true;
    }
    public void Hide()
    {
        if (_thumbnail == IntPtr.Zero)
            return;
        NativeMethods.DwmUnregisterThumbnail(_thumbnail);
        _thumbnail = IntPtr.Zero;
        CurrentSource = IntPtr.Zero;
    }
}
