using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Chistara.Models;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace Chistara.Services;

public static class ThemeService
{
    private static readonly Dictionary<string, Color> AccentColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Red"] = Color.FromRgb(235, 91, 88),
        ["Pink"] = Color.FromRgb(239, 105, 164),
        ["Matcha"] = Color.FromRgb(155, 211, 111),
        ["Purple"] = Color.FromRgb(170, 139, 250),
        ["Cyan"] = Color.FromRgb(69, 210, 228),
        ["Lime"] = Color.FromRgb(190, 226, 74)
    };

    private static LinearGradientBrush? _animatedRgbBrush;
    private static SolidColorBrush? _animatedRgbLowBrush;
    private static SolidColorBrush? _animatedRgbMediumBrush;
    private static LinearGradientBrush? _animatedTransportBrush;

    public static bool Apply(AppSettings settings)
    {
        var dark = settings.ThemeMode switch
        {
            "Dark" => true,
            "Light" => false,
            _ => BackdropService.IsSystemDark()
        };

        var resources = System.Windows.Application.Current.Resources;
        var colors = dark ? DarkPalette : LightPalette;
        foreach (var pair in colors)
            resources[pair.Key] = Solid(pair.Value);

        var rawSystemAccent = BackdropService.GetSystemAccentColor();
        var systemAccent = EnsureSystemAccentVisibility(rawSystemAccent, dark);
        resources["SystemAccentPreviewBrush"] = Solid(systemAccent);
        var rgbAccent = settings.Accent.Equals("RGB gradient", StringComparison.OrdinalIgnoreCase);
        var accent = settings.Accent.Equals("System", StringComparison.OrdinalIgnoreCase)
            ? systemAccent
            : rgbAccent
                ? Color.FromRgb(178, 121, 246)
                : AccentColors.GetValueOrDefault(settings.Accent, systemAccent);
        resources["AccentHeroStartColor"] = Color.FromArgb(dark ? (byte)60 : (byte)42, accent.R, accent.G, accent.B);
        resources["AccentHeroMidColor"] = Color.FromArgb(dark ? (byte)24 : (byte)18, accent.R, accent.G, accent.B);
        resources["DropDownTextBrush"] = Solid(dark ? Colors.White : Color.FromRgb(25, 28, 32));

        StopRgbAnimation();
        if (rgbAccent)
        {
            _animatedRgbBrush = CreateAnimatedRgbBrush();
            _animatedRgbLowBrush = CreateAnimatedSolidBrush(Color.FromArgb(dark ? (byte)44 : (byte)36, 178, 121, 246), Color.FromArgb(dark ? (byte)44 : (byte)36, 69, 210, 228), 9);
            _animatedRgbMediumBrush = CreateAnimatedSolidBrush(Color.FromArgb(112, 69, 210, 228), Color.FromArgb(112, 190, 226, 74), 11);
            resources["AccentBrush"] = _animatedRgbBrush;
            resources["AccentLowBrush"] = _animatedRgbLowBrush;
            resources["AccentMediumBrush"] = _animatedRgbMediumBrush;
            resources["AccentTextBrush"] = Solid(Colors.White);
            _animatedTransportBrush = CreateTwoToneTransportWave();
            resources["TransportPlayBrush"] = _animatedTransportBrush;
            resources["TransportPlayForegroundBrush"] = Solid(Colors.White);
        }
        else
        {
            resources["AccentBrush"] = Solid(Color.FromArgb(255, accent.R, accent.G, accent.B));
            resources["AccentLowBrush"] = Solid(Color.FromArgb(dark ? (byte)45 : (byte)38, accent.R, accent.G, accent.B));
            resources["AccentMediumBrush"] = Solid(Color.FromArgb(112, accent.R, accent.G, accent.B));
            resources["AccentTextBrush"] = Solid(ContrastColor(accent));
            resources["TransportPlayBrush"] = Solid(Color.FromArgb(255, accent.R, accent.G, accent.B));
            resources["TransportPlayForegroundBrush"] = Solid(ContrastColor(accent));
        }

        resources["SuccessBrush"] = Solid(dark ? Color.FromRgb(144, 208, 109) : Color.FromRgb(69, 133, 51));
        return dark;
    }

    private static LinearGradientBrush CreateAnimatedRgbBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            GradientStops = new GradientStopCollection
            {
                new(Color.FromRgb(244, 93, 121), 0),
                new(Color.FromRgb(178, 121, 246), 0.34),
                new(Color.FromRgb(69, 210, 228), 0.68),
                new(Color.FromRgb(190, 226, 74), 1)
            }
        };
        var targets = new[]
        {
            Color.FromRgb(178, 121, 246),
            Color.FromRgb(69, 210, 228),
            Color.FromRgb(190, 226, 74),
            Color.FromRgb(244, 93, 121)
        };
        for (var index = 0; index < brush.GradientStops.Count; index++)
        {
            var animation = new ColorAnimation(targets[index], TimeSpan.FromSeconds(10 + index * 1.5))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            brush.GradientStops[index].BeginAnimation(GradientStop.ColorProperty, animation);
        }
        return brush;
    }

    private static LinearGradientBrush CreateTwoToneTransportWave()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(-0.25, 0),
            EndPoint = new Point(0.9, 1),
            GradientStops = new GradientStopCollection
            {
                new(Color.FromRgb(244, 93, 121), 0),
                new(Color.FromRgb(69, 210, 228), 1)
            }
        };
        var duration = TimeSpan.FromSeconds(4.8);
        var easing = new SineEase { EasingMode = EasingMode.EaseInOut };
        brush.GradientStops[0].BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(Color.FromRgb(178, 121, 246), duration)
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = easing
        });
        brush.GradientStops[1].BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(Color.FromRgb(190, 226, 74), duration)
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = easing
        });
        brush.BeginAnimation(LinearGradientBrush.StartPointProperty, new PointAnimation(new Point(0.35, 0), new Point(-0.25, 1), duration)
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = easing
        });
        brush.BeginAnimation(LinearGradientBrush.EndPointProperty, new PointAnimation(new Point(1.1, 0), new Point(0.65, 1), duration)
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = easing
        });
        return brush;
    }

    private static SolidColorBrush CreateAnimatedSolidBrush(Color from, Color to, double seconds)
    {
        var brush = new SolidColorBrush(from);
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
        return brush;
    }

    private static void StopRgbAnimation()
    {
        if (_animatedRgbBrush is not null)
            foreach (var stop in _animatedRgbBrush.GradientStops) stop.BeginAnimation(GradientStop.ColorProperty, null);
        _animatedRgbLowBrush?.BeginAnimation(SolidColorBrush.ColorProperty, null);
        _animatedRgbMediumBrush?.BeginAnimation(SolidColorBrush.ColorProperty, null);
        if (_animatedTransportBrush is not null)
        {
            foreach (var stop in _animatedTransportBrush.GradientStops) stop.BeginAnimation(GradientStop.ColorProperty, null);
            _animatedTransportBrush.BeginAnimation(LinearGradientBrush.StartPointProperty, null);
            _animatedTransportBrush.BeginAnimation(LinearGradientBrush.EndPointProperty, null);
        }
        _animatedRgbBrush = null;
        _animatedRgbLowBrush = null;
        _animatedRgbMediumBrush = null;
        _animatedTransportBrush = null;
    }

    private static readonly Dictionary<string, Color> DarkPalette = new()
    {
        ["WindowLayerBrush"] = Color.FromArgb(230, 21, 23, 25),
        ["TopBarBrush"] = Color.FromArgb(242, 26, 28, 31),
        ["PaneBrush"] = Color.FromArgb(168, 18, 20, 22),
        ["PlaybackBrush"] = Color.FromArgb(242, 18, 20, 22),
        ["SurfaceBrush"] = Color.FromArgb(132, 29, 32, 35),
        ["SurfaceStrongBrush"] = Color.FromArgb(238, 30, 33, 36),
        ["SurfaceHoverBrush"] = Color.FromArgb(26, 255, 255, 255),
        ["SurfacePressedBrush"] = Color.FromArgb(42, 255, 255, 255),
        ["InputBrush"] = Color.FromArgb(150, 15, 17, 19),
        ["BorderBrush"] = Color.FromArgb(38, 255, 255, 255),
        ["BorderStrongBrush"] = Color.FromArgb(72, 255, 255, 255),
        ["TextPrimaryBrush"] = Color.FromRgb(244, 245, 247),
        ["TextSecondaryBrush"] = Color.FromRgb(178, 183, 190),
        ["TextMutedBrush"] = Color.FromRgb(128, 135, 144),
        ["DangerBrush"] = Color.FromRgb(255, 107, 107)
    };

    private static readonly Dictionary<string, Color> LightPalette = new()
    {
        ["WindowLayerBrush"] = Color.FromArgb(228, 244, 246, 249),
        ["TopBarBrush"] = Color.FromArgb(250, 238, 241, 245),
        ["PaneBrush"] = Color.FromArgb(170, 248, 249, 251),
        ["PlaybackBrush"] = Color.FromArgb(250, 241, 244, 248),
        ["SurfaceBrush"] = Color.FromArgb(175, 255, 255, 255),
        ["SurfaceStrongBrush"] = Color.FromArgb(242, 250, 251, 253),
        ["SurfaceHoverBrush"] = Color.FromArgb(18, 0, 0, 0),
        ["SurfacePressedBrush"] = Color.FromArgb(30, 0, 0, 0),
        ["InputBrush"] = Color.FromArgb(188, 255, 255, 255),
        ["BorderBrush"] = Color.FromArgb(34, 0, 0, 0),
        ["BorderStrongBrush"] = Color.FromArgb(65, 0, 0, 0),
        ["TextPrimaryBrush"] = Color.FromRgb(25, 28, 32),
        ["TextSecondaryBrush"] = Color.FromRgb(75, 82, 91),
        ["TextMutedBrush"] = Color.FromRgb(112, 119, 129),
        ["DangerBrush"] = Color.FromRgb(190, 43, 52)
    };

    private static Color EnsureSystemAccentVisibility(Color color, bool dark)
    {
        var opaque = Color.FromRgb(color.R, color.G, color.B);
        var luminance = RelativeLuminance(opaque);
        var largestChannel = Math.Max(opaque.R, Math.Max(opaque.G, opaque.B));
        var smallestChannel = Math.Min(opaque.R, Math.Min(opaque.G, opaque.B));

        // DWM can return a near-black neutral color for some wallpaper-derived
        // accents. There is no useful hue to preserve in that case, so use the
        // recognizable Windows blue instead of turning black into grey.
        if (luminance < 0.18 && largestChannel - smallestChannel < 12)
            opaque = Color.FromRgb(0, 120, 212);

        luminance = RelativeLuminance(opaque);

        // Apply the same lower luminance floor in Light and Dark modes. Previously
        // only Dark mode received this correction, which made an identical System
        // accent look vivid in Dark mode but almost black in Light mode.
        if (luminance < 0.18)
            return RaiseLuminancePreservingSaturation(opaque, 0.18);
        if (!dark && luminance > 0.30)
            return BlendToLuminance(opaque, Colors.Black, 0.30, lighten: false);
        return opaque;
    }

    private static Color RaiseLuminancePreservingSaturation(Color source, double targetLuminance)
    {
        var largestChannel = Math.Max(source.R, Math.Max(source.G, source.B));
        if (largestChannel == 0)
            return BlendToLuminance(source, Colors.White, targetLuminance, lighten: true);

        var maximumScale = 255d / largestChannel;
        var brightestSameChroma = Scale(source, maximumScale);
        if (RelativeLuminance(brightestSameChroma) < targetLuminance)
            return BlendToLuminance(brightestSameChroma, Colors.White, targetLuminance, lighten: true);

        var low = 1d;
        var high = maximumScale;
        for (var index = 0; index < 16; index++)
        {
            var scale = (low + high) / 2;
            if (RelativeLuminance(Scale(source, scale)) < targetLuminance)
                low = scale;
            else
                high = scale;
        }
        return Scale(source, high);
    }

    private static Color Scale(Color source, double scale) => Color.FromRgb(
        (byte)Math.Min(255, Math.Round(source.R * scale)),
        (byte)Math.Min(255, Math.Round(source.G * scale)),
        (byte)Math.Min(255, Math.Round(source.B * scale)));

    private static Color BlendToLuminance(Color source, Color destination, double targetLuminance, bool lighten)
    {
        var low = 0d;
        var high = 1d;
        for (var index = 0; index < 16; index++)
        {
            var amount = (low + high) / 2;
            var candidate = Blend(source, destination, amount);
            var candidateLuminance = RelativeLuminance(candidate);
            if (lighten ? candidateLuminance < targetLuminance : candidateLuminance > targetLuminance)
                low = amount;
            else
                high = amount;
        }
        return Blend(source, destination, high);
    }

    private static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * amount),
        (byte)Math.Round(from.G + (to.G - from.G) * amount),
        (byte)Math.Round(from.B + (to.B - from.B) * amount));

    private static double RelativeLuminance(Color color) =>
        0.2126 * LinearChannel(color.R) + 0.7152 * LinearChannel(color.G) + 0.0722 * LinearChannel(color.B);

    private static double LinearChannel(byte value)
    {
        var channel = value / 255d;
        return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
    }

    private static SolidColorBrush Solid(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Color ContrastColor(Color color)
    {
        var luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255;
        return luminance > 0.6 ? Color.FromRgb(15, 17, 20) : Colors.White;
    }

}
