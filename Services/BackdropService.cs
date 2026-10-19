using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Color = System.Windows.Media.Color;

namespace Chistara.Services;

public static class BackdropService
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmColorNone = unchecked((int)0xFFFFFFFE);

    public static void Apply(Window window, bool dark, bool liteMode)
    {
        if (!OperatingSystem.IsWindows()) return;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            if (PresentationSource.FromVisual(window) is HwndSource source)
                source.CompositionTarget.BackgroundColor = Colors.Transparent;

            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);

            var darkValue = dark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkValue, sizeof(int)) != 0)
                _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, ref darkValue, sizeof(int));

            var rounded = 2; // DWMWCP_ROUND
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref rounded, sizeof(int));

            // TransientWindow is Windows 11's Acrylic system backdrop. Lite mode uses lower-cost Mica.
            var backdrop = liteMode ? 2 : 3;
            var result = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int));

            // Suppress the DWM colorized outline; the app's own neutral separators remain.
            var borderColor = DwmColorNone;
            _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref borderColor, sizeof(int));

            if (result != 0)
                ApplyLegacyAcrylic(hwnd, dark);
        }
        catch
        {
            // The semi-transparent Fluent layers remain usable if a system backdrop is unavailable.
        }
    }

    public static bool IsSystemDark()
    {
        if (!OperatingSystem.IsWindows()) return true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return true;
        }
    }

    public static Color GetSystemAccentColor()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (DwmGetColorizationColor(out var argb, out _) == 0)
                {
                    var color = Color.FromArgb(
                        255,
                        (byte)((argb >> 16) & 0xff),
                        (byte)((argb >> 8) & 0xff),
                        (byte)(argb & 0xff));
                    // ThemeService preserves this hue while correcting only unusable contrast.
                    return color;
                }
            }
            catch { }
        }
        return Color.FromRgb(96, 165, 250);
    }

    private static void ApplyLegacyAcrylic(IntPtr hwnd, bool dark)
    {
        var accent = new AccentPolicy
        {
            AccentState = 4, // ACCENT_ENABLE_ACRYLICBLURBEHIND
            AccentFlags = 2,
            GradientColor = dark ? 0xCC181A18u : 0xCCF3F5F1u,
            AnimationId = 0
        };
        var size = Marshal.SizeOf<AccentPolicy>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, pointer, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = 19, // WCA_ACCENT_POLICY
                Data = pointer,
                SizeOfData = size
            };
            _ = SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetColorizationColor(out uint colorizationColor, out bool opaqueBlend);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);
}
