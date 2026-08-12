using System.Drawing.Imaging;

namespace Recite;

/// <summary>
/// Capture geometry. Under PerMonitorV2 the values returned by
/// <see cref="SystemInformation.VirtualScreen"/> and <see cref="Screen.Bounds"/> are already
/// physical pixels, so no scaling factor is ever applied on top of them.
/// </summary>
internal static class ScreenCapture
{
    public static Rectangle VirtualBounds => SystemInformation.VirtualScreen;

    public static Bitmap CaptureRect(Rectangle bounds)
    {
        var bitmap = new Bitmap(
            Math.Max(1, bounds.Width), Math.Max(1, bounds.Height), PixelFormat.Format32bppArgb);

        using var g = Graphics.FromImage(bitmap);
        g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    public static Bitmap CaptureVirtualDesktop() => CaptureRect(VirtualBounds);

    /// <summary>
    /// The display holding the foreground window — deliberately not the display under the
    /// cursor, which is often somewhere else entirely. Falls back to the primary display.
    /// </summary>
    public static Rectangle ActiveDisplayBounds()
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return Screen.PrimaryScreen?.Bounds ?? VirtualBounds;
        }

        var monitor = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero)
        {
            var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
            if (Native.GetMonitorInfoW(monitor, ref info))
            {
                var rect = info.rcMonitor.ToRectangle();
                var match = Screen.AllScreens.FirstOrDefault(s => s.Bounds == rect);
                return match?.Bounds ?? rect;
            }
        }

        return Screen.FromHandle(hwnd).Bounds;
    }
}
