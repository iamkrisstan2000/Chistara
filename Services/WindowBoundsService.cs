using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Chistara.Services;

/// <summary>
/// Keeps a custom-chrome maximized window inside the active monitor's working area.
/// WPF's borderless maximize path can otherwise use the full monitor rectangle,
/// covering the taskbar and pushing the resize frame past the screen edges.
/// </summary>
public static class WindowBoundsService
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;

    public static void Attach(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || HwndSource.FromHwnd(handle) is not { } source) return;

        HwndSourceHook hook = (IntPtr hwnd, int message, IntPtr _, IntPtr lParam, ref bool handled) =>
        {
            if (message == WmGetMinMaxInfo && ApplyWorkingArea(window, hwnd, lParam)) handled = true;
            return IntPtr.Zero;
        };
        source.AddHook(hook);
        window.Closed += (_, _) => source.RemoveHook(hook);
    }

    private static bool ApplyWorkingArea(Window window, IntPtr hwnd, IntPtr lParam)
    {
        if (lParam == IntPtr.Zero) return false;
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return false;

        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return false;

        var minMax = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var work = monitorInfo.WorkArea;
        var bounds = monitorInfo.MonitorArea;
        var width = Math.Max(1, work.Right - work.Left);
        var height = Math.Max(1, work.Bottom - work.Top);

        // Coordinates and dimensions for WM_GETMINMAXINFO are physical monitor pixels.
        // PerMonitorV2 DPI awareness in the manifest lets Windows map the WPF content.
        minMax.MaxPosition.X = work.Left - bounds.Left;
        minMax.MaxPosition.Y = work.Top - bounds.Top;
        minMax.MaxSize.X = width;
        minMax.MaxSize.Y = height;
        minMax.MaxTrackSize.X = width;
        minMax.MaxTrackSize.Y = height;

        // Handling WM_GETMINMAXINFO ourselves bypasses WPF's normal MinWidth/
        // MinHeight contribution. Reapply those values in physical pixels so the
        // custom frame can never collapse to a title-bar-sized strip.
        var dpi = VisualTreeHelper.GetDpi(window);
        minMax.MinTrackSize.X = Math.Min(width, Math.Max(1, (int)Math.Ceiling(window.MinWidth * dpi.DpiScaleX)));
        minMax.MinTrackSize.Y = Math.Min(height, Math.Max(1, (int)Math.Ceiling(window.MinHeight * dpi.DpiScaleY)));
        Marshal.StructureToPtr(minMax, lParam, false);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);
}
