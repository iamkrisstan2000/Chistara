using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Chistara.Services;

namespace Chistara.Controls;

public enum VisualizationStyle
{
    Waves,
    ModernWaves,
    Topographic,
    SimpleSpectrum
}

/// <summary>
/// Lightweight retained-mode visualizer. Rendering is driven by WPF's composition
/// clock and consumes immutable FFT snapshots from AudioEngine.
/// </summary>
public sealed class AudioVisualizer : FrameworkElement
{
    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush), typeof(Brush), typeof(AudioVisualizer),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectedStyleProperty = DependencyProperty.Register(
        nameof(SelectedStyle), typeof(VisualizationStyle), typeof(AudioVisualizer),
        new FrameworkPropertyMetadata(VisualizationStyle.Waves, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsPlaybackActiveProperty = DependencyProperty.Register(
        nameof(IsPlaybackActive), typeof(bool), typeof(AudioVisualizer),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LiteModeProperty = DependencyProperty.Register(
        nameof(LiteMode), typeof(bool), typeof(AudioVisualizer),
        new FrameworkPropertyMetadata(false));

    private readonly float[] _spectrum = new float[64];
    private readonly float[] _waveform = new float[192];
    private readonly float[] _peaks = new float[64];
    private readonly Point[] _pointBuffer = new Point[96];
    private readonly Dictionary<byte, SolidColorBrush> _solidAccentBrushes = [];
    private Color _cachedAccentColor;
    private double _cachedAccentOpacity;
    private bool _hasCachedAccentColor;
    private RectangleGeometry? _clipGeometry;
    private Size _clipSize;
    private double _phase;
    private double _level;
    private double _bass;
    private double _beat;
    private long _lastFrameTimestamp;
    private bool _renderingSubscribed;

    public AudioVisualizer()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        Loaded += (_, _) => UpdateRenderingSubscription();
        Unloaded += (_, _) => UnsubscribeFromRendering();
        IsVisibleChanged += (_, _) => UpdateRenderingSubscription();
    }

    public Brush AccentBrush
    {
        get => (Brush)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public VisualizationStyle SelectedStyle
    {
        get => (VisualizationStyle)GetValue(SelectedStyleProperty);
        set => SetValue(SelectedStyleProperty, value);
    }

    public bool IsPlaybackActive
    {
        get => (bool)GetValue(IsPlaybackActiveProperty);
        set => SetValue(IsPlaybackActiveProperty, value);
    }

    public bool LiteMode
    {
        get => (bool)GetValue(LiteModeProperty);
        set => SetValue(LiteModeProperty, value);
    }

    public Func<VisualizationFrame>? FrameProvider { get; set; }

    private void UpdateRenderingSubscription()
    {
        if (IsLoaded && IsVisible) SubscribeToRendering();
        else UnsubscribeFromRendering();
    }

    private void SubscribeToRendering()
    {
        if (_renderingSubscribed) return;
        CompositionTarget.Rendering += CompositionTarget_Rendering;
        _renderingSubscribed = true;
        _lastFrameTimestamp = Stopwatch.GetTimestamp();
    }

    private void UnsubscribeFromRendering()
    {
        if (!_renderingSubscribed) return;
        CompositionTarget.Rendering -= CompositionTarget_Rendering;
        _renderingSubscribed = false;
    }

    private void CompositionTarget_Rendering(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        if (!IsVisible || ActualWidth < 24 || ActualHeight < 24 || Window.GetWindow(this)?.WindowState == WindowState.Minimized)
        {
            _lastFrameTimestamp = now;
            return;
        }
        var elapsed = _lastFrameTimestamp == 0 ? 1d / 60 : (now - _lastFrameTimestamp) / (double)Stopwatch.Frequency;
        var minimumInterval = LiteMode || !IsPlaybackActive ? 1d / 30 : 1d / 60;
        if (elapsed < minimumInterval * 0.82) return;
        _lastFrameTimestamp = now;
        elapsed = Math.Clamp(elapsed, 1d / 240, 0.1);

        var frame = FrameProvider?.Invoke();
        if (frame is not null)
        {
            SmoothArray(_spectrum, frame.Spectrum, IsPlaybackActive ? 0.46f : 0.08f, IsPlaybackActive ? 0.11f : 0.18f);
            SmoothArray(_waveform, frame.Waveform, IsPlaybackActive ? 0.42f : 0.08f, IsPlaybackActive ? 0.16f : 0.22f);
            _level = Smooth(_level, IsPlaybackActive ? frame.Level : 0, frame.Level > _level ? 0.46 : 0.10);
            _bass = Smooth(_bass, IsPlaybackActive ? frame.Bass : 0, frame.Bass > _bass ? 0.42 : 0.10);
            _beat = Smooth(_beat, IsPlaybackActive ? frame.Beat : 0, frame.Beat > _beat ? 0.62 : 0.16);
        }
        else
        {
            SmoothArray(_spectrum, Array.Empty<float>(), 0, 0.16f);
            SmoothArray(_waveform, Array.Empty<float>(), 0, 0.18f);
            _level *= 0.9;
            _bass *= 0.9;
            _beat *= 0.82;
        }

        var motion = IsPlaybackActive ? 0.58 + _bass * 1.8 + _beat * 1.1 : 0.16;
        _phase += elapsed * motion;
        UpdatePeaks((float)elapsed);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        drawingContext.PushClip(GetClipGeometry(bounds));
        DrawAmbientGlow(drawingContext, bounds);
        switch (SelectedStyle)
        {
            case VisualizationStyle.ModernWaves:
                DrawModernWaves(drawingContext, bounds);
                break;
            case VisualizationStyle.Topographic:
                DrawTopographic(drawingContext, bounds);
                break;
            case VisualizationStyle.SimpleSpectrum:
                DrawSimpleSpectrum(drawingContext, bounds);
                break;
            default:
                DrawWaves(drawingContext, bounds);
                break;
        }
        drawingContext.Pop();
    }

    private RectangleGeometry GetClipGeometry(Rect bounds)
    {
        var size = bounds.Size;
        if (_clipGeometry is not null && _clipSize == size) return _clipGeometry;
        _clipSize = size;
        _clipGeometry = new RectangleGeometry(bounds, 8, 8);
        _clipGeometry.Freeze();
        return _clipGeometry;
    }

    private void DrawAmbientGlow(DrawingContext dc, Rect bounds)
    {
        var color = CurrentAccentColor();
        var intensity = IsPlaybackActive ? 0.08 + _level * 0.09 + _beat * 0.05 : 0.035;
        var glow = new RadialGradientBrush(
            Color.FromArgb((byte)Math.Clamp(intensity * 255, 0, 255), color.R, color.G, color.B),
            Color.FromArgb(0, color.R, color.G, color.B))
        {
            RadiusX = 0.72,
            RadiusY = 0.75,
            Center = new Point(0.42 + Math.Sin(_phase * 0.45) * 0.08, 0.52),
            GradientOrigin = new Point(0.44, 0.52)
        };
        glow.Freeze();
        dc.DrawRectangle(glow, null, bounds);
    }

    private void DrawWaves(DrawingContext dc, Rect bounds)
    {
        var width = bounds.Width;
        var height = bounds.Height;
        const int samples = 84;
        for (var layer = 0; layer < 5; layer++)
        {
            var center = height * (0.37 + layer * 0.064);
            var amplitude = height * (0.036 + layer * 0.004 + _level * 0.075 + _bass * 0.055);
            var frequency = 1.35 + layer * 0.19;
            var points = _pointBuffer;
            for (var point = 0; point < samples; point++)
            {
                var t = point / (double)(samples - 1);
                var envelope = Math.Pow(Math.Sin(Math.PI * t), 0.72);
                var wave = Math.Sin(t * Math.PI * 2 * frequency + _phase * (1.45 + layer * 0.08) + layer * 0.92);
                wave += Math.Sin(t * Math.PI * 4.1 - _phase * 0.62 + layer) * 0.27;
                wave += SampleWave(t) * (0.34 + _level * 0.46);
                points[point] = new Point(t * width, center + wave * amplitude * envelope);
            }

            var path = BuildOpenGeometry(points, samples);
            var glowOpacity = 0.035 + layer * 0.012 + _beat * 0.035;
            dc.DrawGeometry(null, CreatePen(glowOpacity, 7 + layer * 0.7), path);
            dc.DrawGeometry(null, CreatePen(0.32 + layer * 0.085, 1.05 + layer * 0.16), path);

            if (layer < 3)
            {
                var ribbon = BuildRibbonGeometry(points, samples, 2.2 + layer * 1.25 + _bass * 4.2);
                dc.DrawGeometry(Accent(0.035 + layer * 0.018), null, ribbon);
            }
        }
    }

    private void DrawModernWaves(DrawingContext dc, Rect bounds)
    {
        var width = bounds.Width;
        var height = bounds.Height;
        const int samples = 92;
        const int strands = 13;
        var centerY = height * (0.51 + Math.Sin(_phase * 0.34) * 0.04);
        for (var strand = 0; strand < strands; strand++)
        {
            var relative = strand - (strands - 1) / 2d;
            var points = _pointBuffer;
            for (var point = 0; point < samples; point++)
            {
                var t = point / (double)(samples - 1);
                var fan = Math.Pow(Math.Sin(Math.PI * t), 0.62);
                var spread = relative * (2.0 + 3.7 * fan) * (0.55 + _level * 0.7);
                var frequency = 1.6 + Math.Abs(relative) * 0.035;
                var oscillation = Math.Sin(t * Math.PI * 2 * frequency + _phase * 2.15 + relative * 0.19);
                var detail = Math.Sin(t * Math.PI * 5.2 - _phase * 1.05 + relative * 0.31) * 0.24;
                var audio = SampleWave((t + strand * 0.018) % 1) * (0.22 + _level * 0.48);
                var y = centerY + spread + (oscillation + detail + audio) * height * (0.025 + _bass * 0.045) * fan;
                points[point] = new Point(t * width, y);
            }
            var path = BuildOpenGeometry(points, samples);
            var prominence = 1 - Math.Abs(relative) / (strands * 0.62);
            if (strand % 3 == 0)
                dc.DrawGeometry(null, CreatePen(0.035 + _beat * 0.025, 5.5), path);
            dc.DrawGeometry(null, CreatePen(0.18 + prominence * 0.34, 0.72 + prominence * 0.72), path);
        }

        for (var particle = 0; particle < 10; particle++)
        {
            var t = (_phase * (0.045 + particle * 0.0027) + particle * 0.113) % 1;
            var envelope = Math.Sin(Math.PI * t);
            var y = centerY + Math.Sin(t * Math.PI * 3.1 + particle * 1.7 + _phase) * height * 0.09 * envelope;
            var radius = 0.7 + (particle % 3) * 0.45 + _beat * 0.8;
            dc.DrawEllipse(Accent(0.22 + (particle % 4) * 0.06), null, new Point(t * width, y), radius, radius);
        }
    }

    private void DrawTopographic(DrawingContext dc, Rect bounds)
    {
        var width = bounds.Width;
        var height = bounds.Height;
        var rows = LiteMode ? 14 : 20;
        var samples = LiteMode ? 60 : 86;
        for (var row = 0; row < rows; row++)
        {
            var depth = row / (double)(rows - 1);
            var baseY = height * (0.10 + depth * 0.80);
            var points = _pointBuffer;
            for (var point = 0; point < samples; point++)
            {
                var t = point / (double)(samples - 1);
                var spectrum = SampleSpectrum(t);
                var ridgeOne = Math.Exp(-Math.Pow((t - (0.34 + Math.Sin(_phase * 0.24) * 0.07)) / 0.18, 2));
                var ridgeTwo = Math.Exp(-Math.Pow((t - (0.70 + Math.Cos(_phase * 0.19) * 0.06)) / 0.14, 2));
                var contour = Math.Sin(t * Math.PI * 3.6 + _phase * 0.82 + row * 0.31) * 0.32;
                contour += Math.Sin(t * Math.PI * 7.4 - _phase * 0.36 + row * 0.18) * 0.12;
                contour += SampleWave((t + row * 0.009) % 1) * 0.22;
                var elevation = (ridgeOne * (0.50 + _bass * 0.95) + ridgeTwo * (0.30 + _level * 0.78) + spectrum * 0.48 + contour * 0.25);
                var perspective = 0.34 + depth * 0.92;
                var y = baseY - elevation * height * 0.13 * perspective;
                points[point] = new Point(t * width, y);
            }
            var opacity = 0.12 + depth * 0.38 + (row % 5 == 0 ? 0.11 : 0);
            var thickness = row % 5 == 0 ? 1.25 : 0.78;
            dc.DrawGeometry(null, CreatePen(opacity, thickness), BuildOpenGeometry(points, samples));
        }
    }

    private void DrawSimpleSpectrum(DrawingContext dc, Rect bounds)
    {
        var width = bounds.Width;
        var height = bounds.Height;
        var barCount = Math.Clamp((int)(width / 8.5), 24, Math.Min(56, _spectrum.Length));
        var sidePadding = Math.Max(14, width * 0.055);
        var available = width - sidePadding * 2;
        var slot = available / barCount;
        var barWidth = Math.Max(2.2, slot * 0.54);
        var baseline = height * 0.82;
        var maxHeight = height * 0.62;

        for (var bar = 0; bar < barCount; bar++)
        {
            var sourceIndex = (int)Math.Round(bar * (_spectrum.Length - 1d) / Math.Max(1, barCount - 1));
            var value = Math.Clamp(_spectrum[sourceIndex] * 1.08 + _beat * Math.Exp(-bar / 8d) * 0.12, 0.012, 1);
            var barHeight = 2.5 + value * maxHeight;
            var x = sidePadding + bar * slot + (slot - barWidth) / 2;
            var barRect = new Rect(x, baseline - barHeight, barWidth, barHeight);
            var glowRect = new Rect(x - 2.4, baseline - barHeight - 2.4, barWidth + 4.8, barHeight + 4.8);
            dc.DrawRoundedRectangle(Accent(0.045 + value * 0.055), null, glowRect, 3.5, 3.5);
            dc.DrawRoundedRectangle(Accent(0.48 + value * 0.42), null, barRect, barWidth / 2, barWidth / 2);

            var peak = _peaks[sourceIndex];
            var peakY = baseline - 2.5 - peak * maxHeight;
            dc.DrawRoundedRectangle(Accent(0.42), null, new Rect(x, peakY, barWidth, 1.35), 0.7, 0.7);

            var reflectionHeight = Math.Min(height - baseline - 4, barHeight * 0.16);
            if (reflectionHeight > 0)
                dc.DrawRoundedRectangle(Accent(0.08), null, new Rect(x, baseline + 3, barWidth, reflectionHeight), barWidth / 2, barWidth / 2);
        }
    }

    private void UpdatePeaks(float elapsed)
    {
        for (var index = 0; index < _peaks.Length; index++)
        {
            var value = index < _spectrum.Length ? _spectrum[index] : 0;
            _peaks[index] = value >= _peaks[index] ? value : Math.Max(value, _peaks[index] - elapsed * 0.34f);
        }
    }

    private float SampleSpectrum(double position)
    {
        if (_spectrum.Length == 0) return 0;
        position = Math.Clamp(position, 0, 1);
        var exact = position * (_spectrum.Length - 1);
        var low = (int)Math.Floor(exact);
        var high = Math.Min(_spectrum.Length - 1, low + 1);
        return (float)(_spectrum[low] + (_spectrum[high] - _spectrum[low]) * (exact - low));
    }

    private float SampleWave(double position)
    {
        if (_waveform.Length == 0) return 0;
        position = Math.Clamp(position, 0, 1);
        var exact = position * (_waveform.Length - 1);
        var low = (int)Math.Floor(exact);
        var high = Math.Min(_waveform.Length - 1, low + 1);
        return (float)(_waveform[low] + (_waveform[high] - _waveform[low]) * (exact - low));
    }

    private Brush Accent(double opacity)
    {
        var source = AccentBrush ?? Brushes.DeepSkyBlue;
        var effectiveOpacity = Math.Clamp(source.Opacity * opacity, 0, 1);
        if (source is SolidColorBrush solid)
        {
            if (!_hasCachedAccentColor || _cachedAccentColor != solid.Color ||
                Math.Abs(_cachedAccentOpacity - source.Opacity) > 0.0001)
            {
                _solidAccentBrushes.Clear();
                _cachedAccentColor = solid.Color;
                _cachedAccentOpacity = source.Opacity;
                _hasCachedAccentColor = true;
            }

            var alpha = (byte)Math.Clamp((int)Math.Round(solid.Color.A * effectiveOpacity), 0, 255);
            if (_solidAccentBrushes.TryGetValue(alpha, out var cached)) return cached;
            var brush = new SolidColorBrush(Color.FromArgb(alpha, solid.Color.R, solid.Color.G, solid.Color.B));
            brush.Freeze();
            _solidAccentBrushes[alpha] = brush;
            return brush;
        }

        var clone = source.CloneCurrentValue();
        clone.Opacity = effectiveOpacity;
        if (clone.CanFreeze) clone.Freeze();
        return clone;
    }

    private Pen CreatePen(double opacity, double thickness)
    {
        var pen = new Pen(Accent(opacity), thickness);
        if (pen.CanFreeze) pen.Freeze();
        return pen;
    }

    private Color CurrentAccentColor()
    {
        return AccentBrush switch
        {
            SolidColorBrush solid => solid.Color,
            LinearGradientBrush gradient when gradient.GradientStops.Count > 0 =>
                gradient.GradientStops[(int)(Math.Abs(_phase * 0.24) * gradient.GradientStops.Count) % gradient.GradientStops.Count].Color,
            _ => Colors.DeepSkyBlue
        };
    }

    private static StreamGeometry BuildOpenGeometry(IReadOnlyList<Point> points, int count)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], false, false);
            for (var index = 1; index < count; index++)
                context.LineTo(points[index], true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    private static StreamGeometry BuildRibbonGeometry(IReadOnlyList<Point> points, int count, double halfWidth)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(points[0].X, points[0].Y - halfWidth), true, true);
            for (var index = 1; index < count; index++)
                context.LineTo(new Point(points[index].X, points[index].Y - halfWidth), true, true);
            for (var index = count - 1; index >= 0; index--)
                context.LineTo(new Point(points[index].X, points[index].Y + halfWidth), true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    private static void SmoothArray(float[] current, IReadOnlyList<float> target, float attack, float release)
    {
        for (var index = 0; index < current.Length; index++)
        {
            var targetIndex = target.Count == 0 ? -1 : (int)Math.Round(index * (target.Count - 1d) / Math.Max(1, current.Length - 1));
            var value = targetIndex >= 0 ? target[targetIndex] : 0;
            var amount = value > current[index] ? attack : release;
            current[index] += (value - current[index]) * amount;
        }
    }

    private static double Smooth(double current, double target, double amount) => current + (target - current) * amount;
}
