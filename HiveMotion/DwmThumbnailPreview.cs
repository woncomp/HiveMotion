using System;

namespace HiveMotion;

/// <summary>Live window preview drawn by DWM straight into our overlay window — the same
/// mechanism as taskbar peek: native resolution, works for minimized windows.</summary>
internal sealed class DwmThumbnailPreview
{
    private IntPtr _thumbnail;

    public IntPtr CurrentSource { get; private set; }

    /// <param name="destRect">Destination in the overlay window's client coordinates (physical pixels).</param>
    public bool Show(IntPtr destHwnd, IntPtr sourceHwnd, NativeMethods.RECT destRect)
    {
        Hide();

        if (NativeMethods.DwmRegisterThumbnail(destHwnd, sourceHwnd, out _thumbnail) != 0 ||
            _thumbnail == IntPtr.Zero)
            return false;

        CurrentSource = sourceHwnd;
        var props = new NativeMethods.DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = NativeMethods.DWM_TNP_RECTDESTINATION
                    | NativeMethods.DWM_TNP_VISIBLE
                    | NativeMethods.DWM_TNP_OPACITY
                    | NativeMethods.DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = destRect,
            opacity = 255,
            fVisible = true,
            fSourceClientAreaOnly = true
        };
        NativeMethods.DwmUpdateThumbnailProperties(_thumbnail, ref props);
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
