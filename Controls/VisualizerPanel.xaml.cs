using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Chistara.Services;

namespace Chistara.Controls;

public sealed class VisualizationStyleChangedEventArgs(VisualizationStyle style) : EventArgs
{
    public VisualizationStyle Style { get; } = style;
}

public partial class VisualizerPanel : UserControl
{
    public static readonly DependencyProperty IsSelectorEngagedProperty = DependencyProperty.Register(
        nameof(IsSelectorEngaged), typeof(bool), typeof(VisualizerPanel), new PropertyMetadata(false));

    private readonly DispatcherTimer _selectorCollapseTimer;
    private bool _synchronizing;
    private VisualizationStyle _selectedStyle = VisualizationStyle.Waves;
    private bool _isPlaybackActive;

    public VisualizerPanel()
    {
        // Do not select an item from XAML: SelectionChanged can fire while the
        // remainder of this UserControl (including VisualizerCanvas) is still null.
        _synchronizing = true;
        InitializeComponent();

        _selectorCollapseTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1.5)
        };
        _selectorCollapseTimer.Tick += SelectorCollapseTimer_Tick;
        StyleCombo.PreviewMouseLeftButtonDown += (_, _) => EngageSelector();
        StyleCombo.MouseEnter += (_, _) => _selectorCollapseTimer.Stop();
        StyleCombo.MouseLeave += (_, _) => QueueSelectorCollapse();
        StyleCombo.DropDownOpened += (_, _) => EngageSelector();
        StyleCombo.DropDownClosed += (_, _) => QueueSelectorCollapse();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is bool visible && !visible) ResetSelectorPresentation();
        };
        Unloaded += (_, _) => ResetSelectorPresentation();

        StyleCombo.SelectedIndex = 0;
        VisualizerCanvas.SelectedStyle = _selectedStyle;
        _synchronizing = false;
        UpdatePlaybackState();
    }

    public event EventHandler<VisualizationStyleChangedEventArgs>? StyleChanged;

    public bool IsSelectorEngaged
    {
        get => (bool)GetValue(IsSelectorEngagedProperty);
        private set => SetValue(IsSelectorEngagedProperty, value);
    }

    public Func<VisualizationFrame>? FrameProvider
    {
        get => VisualizerCanvas.FrameProvider;
        set => VisualizerCanvas.FrameProvider = value;
    }

    public VisualizationStyle SelectedStyle
    {
        get => _selectedStyle;
        set
        {
            if (_selectedStyle == value && VisualizerCanvas.SelectedStyle == value) return;
            _selectedStyle = value;
            VisualizerCanvas.SelectedStyle = value;
            _synchronizing = true;
            var tag = value.ToString();
            StyleCombo.SelectedItem = StyleCombo.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal));
            _synchronizing = false;
            UpdatePlaybackState();
        }
    }

    public ImageSource? ArtworkSource
    {
        get => VisualizerCanvas.ArtworkSource;
        set
        {
            VisualizerCanvas.ArtworkSource = value;
            UpdatePlaybackState();
        }
    }

    public bool IsPlaybackActive
    {
        get => _isPlaybackActive;
        set
        {
            if (_isPlaybackActive == value) return;
            _isPlaybackActive = value;
            UpdatePlaybackState();
        }
    }

    public bool LiteMode
    {
        get => VisualizerCanvas.LiteMode;
        set => VisualizerCanvas.LiteMode = value;
    }

    private void EngageSelector()
    {
        _selectorCollapseTimer.Stop();
        IsSelectorEngaged = true;
    }

    private void QueueSelectorCollapse()
    {
        _selectorCollapseTimer.Stop();
        if (!IsSelectorEngaged || StyleCombo.IsDropDownOpen || StyleCombo.IsMouseOver) return;
        _selectorCollapseTimer.Start();
    }

    private void SelectorCollapseTimer_Tick(object? sender, EventArgs e)
    {
        _selectorCollapseTimer.Stop();
        if (StyleCombo.IsDropDownOpen || StyleCombo.IsMouseOver) return;
        IsSelectorEngaged = false;
    }

    private void ResetSelectorPresentation()
    {
        _selectorCollapseTimer.Stop();
        IsSelectorEngaged = false;
    }

    private void StyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_synchronizing || VisualizerCanvas is null || StyleCombo.SelectedItem is not ComboBoxItem item ||
            !Enum.TryParse<VisualizationStyle>(item.Tag?.ToString(), out var style)) return;
        EngageSelector();
        _selectedStyle = style;
        VisualizerCanvas.SelectedStyle = style;
        UpdatePlaybackState();
        StyleChanged?.Invoke(this, new VisualizationStyleChangedEventArgs(style));
        QueueSelectorCollapse();
    }

    private void UpdatePlaybackState()
    {
        if (VisualizerCanvas is null) return;
        VisualizerCanvas.IsPlaybackActive = _isPlaybackActive;
        var showArtworkAtRest = _selectedStyle == VisualizationStyle.CdDays && ArtworkSource is not null;
        IdleOverlay.Visibility = _isPlaybackActive || showArtworkAtRest ? Visibility.Collapsed : Visibility.Visible;
    }
}
