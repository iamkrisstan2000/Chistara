using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Chistara.Controls;
using Chistara.Models;
using Chistara.Services;

namespace Chistara;

public partial class MainWindow : Window
{
    public BulkObservableCollection<PaneItem> PaneItems { get; } = [];
    public BulkObservableCollection<RecentPlay> RecentItems { get; } = [];
    public BulkObservableCollection<DriveItem> Devices { get; } = [];
    public BulkObservableCollection<TrackItem> VisibleTracks { get; } = [];
    public BulkObservableCollection<LibraryGroup> LibraryGroups { get; } = [];
    public BulkObservableCollection<RadioStation> VisibleStations { get; } = [];
    public BulkObservableCollection<RadioStation> HomeLocalStations { get; } = [];
    public BulkObservableCollection<RadioStation> HomeWorldwideStations { get; } = [];
    public BulkObservableCollection<SearchResult> SearchResults { get; } = [];

    private AppSettings _settings = AppStorage.LoadSettings();
    private readonly RadioService _radioService = new();
    private readonly ArtworkService _artworkService = new();
    private readonly AudioEngine _audio = new();
    private readonly DispatcherTimer _playbackTimer;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _toastTimer;
    private readonly DispatcherTimer _searchDebounceTimer;

    private CancellationTokenSource? _libraryCancellation;
    private CancellationTokenSource? _artworkCancellation;
    private CancellationTokenSource? _deviceCancellation;
    private CancellationTokenSource? _radioCancellation;
    private CancellationTokenSource? _playCancellation;
    private CancellationTokenSource? _searchRadioCancellation;
    private CancellationTokenSource? _homeRecommendationsCancellation;
    private List<TrackItem> _allTracks = [];
    private List<TrackItem> _deviceTracks = [];
    private List<TrackItem> _orderedQueue = [];
    private List<TrackItem> _queue = [];
    private List<RadioStation> _allStations = [];
    private List<RadioStation> _searchStations = [];
    private string _lastStationSearchQuery = string.Empty;
    private int _queueIndex = -1;
    private string _currentSection = "Home";
    private string _homeMode = "overview";
    private string _musicMode = "songs";
    private string _radioMode = "local";
    private string _settingsDestination = "visual";
    private TrackItem? _currentTrack;
    private RadioStation? _currentStation;
    private Uri? _currentRadioUri;
    private bool _paneExpanded;
    private bool _isRadio;
    private bool _radioUsesMediaElement;
    private bool _radioFallbackAttempted;
    private bool _radioOriginalAttempted;
    private bool _isPlaying;
    private bool _userSeeking;
    private bool _crossfadeTriggered;
    private bool _searchStationsLoaded;
    private bool _searchStationsLoading;
    private bool _initializing = true;
    private bool _isDark = true;
    private bool _homeDiscSpinning;
    private bool _devicesRefreshing;
    private bool _shuffleEnabled;
    private MusicRepeatMode _repeatMode;
    private string _lastHomeGreeting = string.Empty;
    private readonly RotateTransform _homeDiscRotation = new();
    private double _volumeBeforeMute = 0.72;

    private enum SearchScope { All, Music, Song, Artist, Album, Radio }
    private enum MusicRepeatMode { Off, All, One }
    private readonly record struct ParsedSearch(string Term, SearchScope Scope);

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        HomeHeroDisc.RenderTransform = _homeDiscRotation;
        MusicVisualizerPanel.FrameProvider = _audio.GetVisualizationFrame;
        RadioVisualizerPanel.FrameProvider = _audio.GetVisualizationFrame;
        MusicVisualizerPanel.StyleChanged += VisualizerPanel_StyleChanged;
        RadioVisualizerPanel.StyleChanged += VisualizerPanel_StyleChanged;

        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        if (!double.IsNaN(_settings.WindowLeft) && !double.IsNaN(_settings.WindowTop) && IsPositionVisible(_settings.WindowLeft, _settings.WindowTop))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = _settings.WindowLeft;
            Top = _settings.WindowTop;
        }

        _paneExpanded = _settings.PaneExpanded;
        _shuffleEnabled = _settings.ShuffleEnabled;
        _repeatMode = Enum.TryParse<MusicRepeatMode>(_settings.RepeatMode, true, out var savedRepeatMode) ? savedRepeatMode : MusicRepeatMode.Off;
        ApplyPaneState(animate: false);
        ApplySettingsToControls();
        UpdateMusicTransportControls();
        _isDark = ThemeService.Apply(_settings);
        UpdateSettingsSelectionVisuals();
        RefreshRecentItems();

        _audio.Volume = (float)_settings.Volume;
        _audio.ConfigureEqualizer(_settings.EqualizerEnabled, _settings.BassGain, _settings.MidGain, _settings.TrebleGain);
        _audio.ConfigureReverb(_settings.ReverbEnabled, _settings.ReverbMix);
        _audio.PlaybackEnded += Audio_PlaybackEnded;
        _audio.PlaybackError += Audio_PlaybackError;

        _playbackTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _playbackTimer.Tick += PlaybackTimer_Tick;
        _playbackTimer.Start();

        _saveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(450) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            AppStorage.SaveSettings(_settings);
        };

        _toastTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3.6) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast.Visibility = Visibility.Collapsed;
        };

        _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
        _searchDebounceTimer.Tick += SearchDebounceTimer_Tick;
        GlobalSearchShell.SizeChanged += (_, _) => SearchPopupBorder.Width = Math.Max(360, GlobalSearchShell.ActualWidth);

        StateChanged += (_, _) => MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        _initializing = false;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _ = RefreshDevicesAsync();
        NavigateTop(_settings.LastSection is "Home" or "Music" or "FM" or "Settings" ? _settings.LastSection : "Home");

        // Show the restored shell immediately; large cached libraries are parsed away
        // from the dispatcher and applied in one collection reset when ready.
        var cached = await Task.Run(AppStorage.LoadLibraryCache);
        if (cached.Count > 0) UpdateLibrary(cached);
        await ScanLibraryAsync(showCompletion: false);
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        WindowBoundsService.Attach(this);
        _isDark = ThemeService.Apply(_settings);
        BackdropService.Apply(this, _isDark, _settings.LiteMode);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowWidth = ActualWidth;
            _settings.WindowHeight = ActualHeight;
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
        }
        _settings.PaneExpanded = _paneExpanded;
        AppStorage.SaveSettings(_settings);
        _libraryCancellation?.Cancel();
        _artworkCancellation?.Cancel();
        _deviceCancellation?.Cancel();
        _radioCancellation?.Cancel();
        _playCancellation?.Cancel();
        _searchRadioCancellation?.Cancel();
        _homeRecommendationsCancellation?.Cancel();
        _searchDebounceTimer.Stop();
        try { RadioMedia.Stop(); } catch { }
        _audio.Dispose();
        _artworkService.Dispose();
        _radioService.Dispose();
    }

    #region Window chrome and navigation

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            GlobalSearchBox.Focus();
            GlobalSearchBox.SelectAll();
            if (!string.IsNullOrWhiteSpace(GlobalSearchBox.Text)) BuildUnifiedSearchResults();
            e.Handled = true;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        try { DragMove(); } catch { }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void TopTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string section }) NavigateTop(section);
    }

    private void NavigateTop(string section)
    {
        _currentSection = section;
        _settings.LastSection = section;
        HomePage.Visibility = section == "Home" ? Visibility.Visible : Visibility.Collapsed;
        MusicPage.Visibility = section == "Music" ? Visibility.Visible : Visibility.Collapsed;
        RadioPage.Visibility = section == "FM" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = section == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        HomeTab.IsChecked = section == "Home";
        MusicTab.IsChecked = section == "Music";
        FMTab.IsChecked = section == "FM";
        SettingsTab.IsChecked = section == "Settings";
        PaneTitleText.Text = section;

        switch (section)
        {
            case "Home":
                BuildPane([
                    new PaneItem { Id = "overview", Label = "Overview", Icon = "\uE80F", Hint = "Home and quick actions" },
                    new PaneItem { Id = "recent", Label = "Recently Played", Icon = "\uE823", Hint = "Your latest music and FM" }
                ], _homeMode);
                UpdateHomeGreeting();
                ShowHomeMode(_homeMode);
                RefreshRecentItems();
                _ = LoadHomeRecommendationsAsync();
                break;
            case "Music":
                BuildPane([
                    new PaneItem { Id = "artists", Label = "Artists", Icon = "\uE77B", Hint = "Browse by artist" },
                    new PaneItem { Id = "albums", Label = "Albums", Icon = "\uE93C", Hint = "Browse by album" },
                    new PaneItem { Id = "songs", Label = "All Songs", Icon = "\uE8D6", Hint = "Every song" },
                    new PaneItem { Id = "devices", Label = "Devices", Icon = "\uEDA2", Hint = "Music on other drives" }
                ], "songs");
                ShowMusicMode("songs");
                break;
            case "FM":
                BuildPane([
                    new PaneItem { Id = "local", Label = "Local FM", Icon = "\uE707", Hint = "Stations from your region" },
                    new PaneItem { Id = "worldwide", Label = "Worldwide FM", Icon = "\uE774", Hint = "Popular stations worldwide" }
                ], _radioMode);
                _ = LoadRadioStationsAsync(_radioMode == "local", forceRefresh: false);
                break;
            case "Settings":
                BuildPane([
                    new PaneItem { Id = "visual", Label = "Visual", Icon = "\uE790", Hint = "Modes and accent" },
                    new PaneItem { Id = "player", Label = "Player", Icon = "\uE767", Hint = "Folders, sound and streams" },
                    new PaneItem { Id = "troubleshoot", Label = "Troubleshoot", Icon = "\uE90F", Hint = "Repair and reset tools" },
                    new PaneItem { Id = "about", Label = "About", Icon = "\uE946", Hint = "Version and credits" }
                ], _settingsDestination);
                ShowSettingsDestination(_settingsDestination);
                break;
        }
        UpdateMusicTransportControls();
        SaveSoon();
    }

    private void BuildPane(IEnumerable<PaneItem> items, string selectedId)
    {
        var list = items.ToList();
        foreach (var item in list) item.IsSelected = item.Id == selectedId;
        PaneItems.ReplaceAll(list);
    }

    private void PaneItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.ToggleButton { CommandParameter: string id }) return;
        SelectPaneItem(id);

        if (_currentSection == "Home")
        {
            ShowHomeMode(id);
        }
        else if (_currentSection == "Music")
        {
            ShowMusicMode(id);
        }
        else if (_currentSection == "FM")
        {
            _radioMode = id == "worldwide" ? "worldwide" : "local";
            _ = LoadRadioStationsAsync(_radioMode == "local", forceRefresh: false);
        }
        else if (_currentSection == "Settings")
        {
            ShowSettingsDestination(id);
        }
    }

    private void ShowHomeMode(string mode)
    {
        _homeMode = mode == "recent" ? "recent" : "overview";
        HomeOverviewView.Visibility = _homeMode == "overview" ? Visibility.Visible : Visibility.Collapsed;
        HomeRecentView.Visibility = _homeMode == "recent" ? Visibility.Visible : Visibility.Collapsed;
        if (_homeMode == "overview") HomeOverviewView.ScrollToTop();
        else HomeRecentView.ScrollToTop();
    }

    private void UpdateHomeGreeting()
    {
        var timeGreeting = DateTime.Now.Hour switch
        {
            < 12 => "Good morning — find your rhythm",
            < 18 => "Good afternoon — let the music in",
            _ => "Good evening — settle into the sound"
        };
        string[] greetings =
        [
            timeGreeting,
            "Welcome back to your sound",
            "Something beautiful is ready to play",
            "Tune in to a better moment",
            "Your next favorite sound is waiting",
            "Let the room find its rhythm",
            "Make this moment sing",
            "Find your frequency",
            "A little music changes everything"
        ];
        var choices = greetings.Where(greeting => !greeting.Equals(_lastHomeGreeting, StringComparison.Ordinal)).ToArray();
        _lastHomeGreeting = choices[Random.Shared.Next(choices.Length)];
        HomeGreetingText.Text = _lastHomeGreeting;
    }

    private async Task LoadHomeRecommendationsAsync()
    {
        _homeRecommendationsCancellation?.Cancel();
        _homeRecommendationsCancellation?.Dispose();
        _homeRecommendationsCancellation = new CancellationTokenSource();
        var token = _homeRecommendationsCancellation.Token;
        HomeLocalRecommendationStatus.Text = "Finding local stations…";
        HomeWorldwideRecommendationStatus.Text = "Finding worldwide stations…";
        HomeLocalRecommendationStatus.Visibility = Visibility.Visible;
        HomeWorldwideRecommendationStatus.Visibility = Visibility.Visible;
        HomeLocalStations.ReplaceAll([]);
        HomeWorldwideStations.ReplaceAll([]);

        try
        {
            var localTask = _radioService.GetHomeRecommendationsAsync(true, _settings.StreamQuality, 3, token);
            var worldwideTask = _radioService.GetHomeRecommendationsAsync(false, _settings.StreamQuality, 3, token);
            await Task.WhenAll(localTask, worldwideTask);
            if (token.IsCancellationRequested) return;

            HomeLocalStations.ReplaceAll(localTask.Result);
            HomeWorldwideStations.ReplaceAll(worldwideTask.Result);
            HomeLocalRecommendationSubtitle.Text = $"Popular streams near {_radioService.LocalLocationName}";
            HomeLocalRecommendationStatus.Text = localTask.Result.Count == 0 ? "Local recommendations are temporarily unavailable." : string.Empty;
            HomeWorldwideRecommendationStatus.Text = worldwideTask.Result.Count == 0 ? "Worldwide recommendations are temporarily unavailable." : string.Empty;
            HomeLocalRecommendationStatus.Visibility = localTask.Result.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            HomeWorldwideRecommendationStatus.Visibility = worldwideTask.Result.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (token.IsCancellationRequested) return;
            HomeLocalRecommendationStatus.Text = "Local recommendations are temporarily unavailable.";
            HomeWorldwideRecommendationStatus.Text = "Worldwide recommendations are temporarily unavailable.";
        }
    }

    private void ShowSettingsDestination(string destination)
    {
        _settingsDestination = destination is "visual" or "player" or "troubleshoot" or "about" ? destination : "visual";
        destination = _settingsDestination;
        var visual = destination == "visual";
        var player = destination == "player";
        ModesSection.Visibility = visual ? Visibility.Visible : Visibility.Collapsed;
        AccentsSection.Visibility = visual ? Visibility.Visible : Visibility.Collapsed;
        FoldersSection.Visibility = player ? Visibility.Visible : Visibility.Collapsed;
        SoundSection.Visibility = player ? Visibility.Visible : Visibility.Collapsed;
        StreamSection.Visibility = player ? Visibility.Visible : Visibility.Collapsed;
        TroubleshootSection.Visibility = destination == "troubleshoot" ? Visibility.Visible : Visibility.Collapsed;
        AboutSection.Visibility = destination == "about" ? Visibility.Visible : Visibility.Collapsed;

        (SettingsPageTitle.Text, SettingsPageSubtitle.Text) = destination switch
        {
            "player" => ("Player", "Choose media folders, tune sound enhancements, and manage streaming behavior."),
            "troubleshoot" => ("Troubleshoot", "Repair local state without touching the music files on your drives."),
            "about" => ("About", "Version information, privacy details, and open-source services used by Chistara."),
            _ => ("Visual", "Choose the appearance and accent used throughout Chistara.")
        };
        Dispatcher.BeginInvoke(() => SettingsScroll.ScrollToTop(), DispatcherPriority.Loaded);
    }

    private void SelectPaneItem(string id)
    {
        foreach (var item in PaneItems) item.IsSelected = item.Id == id;
    }

    private void PaneToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _paneExpanded = !_paneExpanded;
        ApplyPaneState(animate: true);
        _settings.PaneExpanded = _paneExpanded;
        SaveSoon();
    }

    private void ApplyPaneState(bool animate)
    {
        var target = _paneExpanded ? 224d : 56d;
        if (_paneExpanded) PaneTitleText.Visibility = Visibility.Visible;

        if (!animate)
        {
            SidePane.Width = target;
            if (!_paneExpanded) PaneTitleText.Visibility = Visibility.Collapsed;
            return;
        }

        var animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        animation.Completed += (_, _) =>
        {
            SidePane.BeginAnimation(WidthProperty, null);
            SidePane.Width = target;
            if (!_paneExpanded) PaneTitleText.Visibility = Visibility.Collapsed;
        };
        SidePane.BeginAnimation(WidthProperty, animation);
    }

    private void GlobalSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (GlobalSearchHint is null) return;
        var hasQuery = !string.IsNullOrWhiteSpace(GlobalSearchBox.Text);
        GlobalSearchHint.Visibility = hasQuery ? Visibility.Collapsed : Visibility.Visible;
        ClearSearchButton.Visibility = hasQuery ? Visibility.Visible : Visibility.Collapsed;
        if (_initializing) return;

        _searchDebounceTimer.Stop();
        var parsed = ParseSearch(GlobalSearchBox.Text);
        if (!parsed.Term.Equals(_lastStationSearchQuery, StringComparison.OrdinalIgnoreCase))
        {
            _searchRadioCancellation?.Cancel();
            _searchStations = [];
            _searchStationsLoaded = false;
            _searchStationsLoading = false;
        }
        UpdateCurrentPageForSearch();
        BuildUnifiedSearchResults();
        if (parsed.Term.Length >= 2 && !_searchStationsLoaded)
        {
            SearchFooterText.Text = _searchStationsLoading ? "Searching the FM directory…" : "Music results are instant · FM results are loading…";
            _searchDebounceTimer.Start();
        }
        else if (!_searchStationsLoading)
        {
            SearchFooterText.Text = "Tip: try artist:, album:, song:, or fm:";
        }
    }

    private void GlobalSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ClearUnifiedSearch(keepFocus: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && SearchResults.Count > 0)
        {
            e.Handled = true;
            _ = ActivateSearchResultAsync(SearchResults[0]);
        }
        else if (e.Key == Key.Down && SearchResults.Count > 0)
        {
            GlobalSearchPopup.IsOpen = true;
            SearchResultsList.SelectedIndex = 0;
            SearchResultsList.Focus();
            e.Handled = true;
        }
    }

    private void SearchResultsList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ClearUnifiedSearch(keepFocus: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && SearchResultsList.SelectedItem is SearchResult result)
        {
            e.Handled = true;
            _ = ActivateSearchResultAsync(result);
        }
        else if (e.Key == Key.Up && SearchResultsList.SelectedIndex <= 0)
        {
            GlobalSearchBox.Focus();
            GlobalSearchBox.CaretIndex = GlobalSearchBox.Text.Length;
            e.Handled = true;
        }
    }

    private async void SearchResult_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: SearchResult result })
            await ActivateSearchResultAsync(result);
    }

    private void ClearSearchButton_Click(object sender, RoutedEventArgs e) => ClearUnifiedSearch(keepFocus: true);

    private void ClearUnifiedSearch(bool keepFocus)
    {
        _searchDebounceTimer.Stop();
        GlobalSearchPopup.IsOpen = false;
        GlobalSearchBox.Clear();
        if (keepFocus)
        {
            GlobalSearchBox.Focus();
            Keyboard.Focus(GlobalSearchBox);
        }
    }

    private void UpdateCurrentPageForSearch()
    {
        if (_currentSection == "Music")
        {
            if (_musicMode is "artists" or "albums") ShowMusicMode(_musicMode);
            else if (_musicMode == "device-results") ApplyMusicTrackFilter(_deviceTracks);
            else if (_musicMode != "devices") ApplyMusicTrackFilter(_allTracks);
        }
        else if (_currentSection == "FM" && _allStations.Count > 0)
        {
            ApplyRadioFilter();
        }
    }

    private void BuildUnifiedSearchResults()
    {
        var parsed = ParseSearch(GlobalSearchBox.Text);
        if (string.IsNullOrWhiteSpace(parsed.Term))
        {
            SearchResults.ReplaceAll([]);
            SearchEmptyPanel.Visibility = Visibility.Collapsed;
            GlobalSearchPopup.IsOpen = false;
            SearchSummaryText.Text = "Music + FM";
            return;
        }

        var results = new List<SearchResult>();
        var includeTracks = parsed.Scope is SearchScope.All or SearchScope.Music or SearchScope.Song;
        var includeArtists = parsed.Scope is SearchScope.All or SearchScope.Music or SearchScope.Artist;
        var includeAlbums = parsed.Scope is SearchScope.All or SearchScope.Music or SearchScope.Album;
        var includeRadio = parsed.Scope is SearchScope.All or SearchScope.Radio;
        var perCategory = parsed.Scope == SearchScope.All ? 6 : 12;

        if (includeTracks)
        {
            results.AddRange(_allTracks
                .Select(track => new
                {
                    Track = track,
                    Score = MatchScore(parsed.Term,
                        (track.DisplayTitle, 28),
                        (track.DisplayArtist, 18),
                        (track.DisplayAlbum, 12),
                        (track.Genre, 4))
                })
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Track.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
                .Take(perCategory)
                .Select(item => new SearchResult
                {
                    Kind = "Song",
                    Title = item.Track.DisplayTitle,
                    Subtitle = $"{item.Track.DisplayArtist} · {item.Track.DisplayAlbum}",
                    Icon = "\uE8D6",
                    ArtworkSource = item.Track.ArtworkPath,
                    Key = item.Track.Path,
                    Score = item.Score,
                    Track = item.Track
                }));
        }

        if (includeArtists)
        {
            results.AddRange(_allTracks
                .GroupBy(track => track.DisplayArtist, StringComparer.CurrentCultureIgnoreCase)
                .Select(group => new { Group = group, Score = MatchScore(parsed.Term, (group.Key, 24)) })
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Group.Key, StringComparer.CurrentCultureIgnoreCase)
                .Take(parsed.Scope == SearchScope.Artist ? 12 : 3)
                .Select(item => new SearchResult
                {
                    Kind = "Artist",
                    Title = item.Group.Key,
                    Subtitle = $"{item.Group.Count():N0} {(item.Group.Count() == 1 ? "song" : "songs")}",
                    Icon = "\uE77B",
                    ArtworkSource = item.Group.Select(track => track.ArtworkPath).FirstOrDefault(path => !string.IsNullOrWhiteSpace(path)) ?? string.Empty,
                    Key = item.Group.Key,
                    Score = item.Score
                }));
        }

        if (includeAlbums)
        {
            results.AddRange(_allTracks
                .GroupBy(track => track.DisplayAlbum, StringComparer.CurrentCultureIgnoreCase)
                .Select(group => new
                {
                    Group = group,
                    Score = MatchScore(parsed.Term, (group.Key, 24), (group.First().DisplayArtist, 8))
                })
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Group.Key, StringComparer.CurrentCultureIgnoreCase)
                .Take(parsed.Scope == SearchScope.Album ? 12 : 3)
                .Select(item => new SearchResult
                {
                    Kind = "Album",
                    Title = item.Group.Key,
                    Subtitle = $"{item.Group.First().DisplayArtist} · {item.Group.Count():N0} {(item.Group.Count() == 1 ? "song" : "songs")}",
                    Icon = "\uE93C",
                    ArtworkSource = item.Group.Select(track => track.ArtworkPath).FirstOrDefault(path => !string.IsNullOrWhiteSpace(path)) ?? string.Empty,
                    Key = item.Group.Key,
                    Score = item.Score
                }));
        }

        if (includeRadio)
        {
            var stations = _allStations.Concat(_searchStations)
                .Where(station => !string.IsNullOrWhiteSpace(station.Name))
                .GroupBy(station => string.IsNullOrWhiteSpace(station.Id) ? station.StreamUrl : station.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First());
            results.AddRange(stations
                .Select(station => new
                {
                    Station = station,
                    Score = MatchScore(parsed.Term,
                        (station.Name, 28),
                        (station.Country, 14),
                        (station.State, 12),
                        (station.Tags, 10),
                        (station.Language, 6))
                })
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Station.ClickCount)
                .Take(parsed.Scope == SearchScope.Radio ? 14 : 7)
                .Select(item => new SearchResult
                {
                    Kind = "FM",
                    Title = item.Station.Name,
                    Subtitle = JoinSearchDetail(item.Station.LocationText, item.Station.TechnicalText),
                    Icon = "\uE720",
                    ArtworkSource = item.Station.Favicon,
                    Key = item.Station.Id,
                    Score = item.Score,
                    Station = item.Station
                }));
        }

        var ordered = results
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Kind == "FM" ? 1 : 0)
            .ThenBy(result => result.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(16)
            .ToList();
        SearchResults.ReplaceAll(ordered);
        SearchResultsList.Visibility = ordered.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchEmptyPanel.Visibility = ordered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var musicCount = ordered.Count(result => result.Kind != "FM");
        var stationCount = ordered.Count - musicCount;
        SearchSummaryText.Text = stationCount > 0 ? $"{musicCount} music · {stationCount} FM" : $"{musicCount} music";
        SearchPopupBorder.Width = Math.Max(360, GlobalSearchShell.ActualWidth);
        GlobalSearchPopup.IsOpen = GlobalSearchBox.IsKeyboardFocusWithin || GlobalSearchPopup.IsOpen;
    }

    private async void SearchDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _searchDebounceTimer.Stop();
        var parsed = ParseSearch(GlobalSearchBox.Text);
        if (parsed.Term.Length >= 2 && !_searchStationsLoaded && !_searchStationsLoading)
            await EnsureSearchStationsAsync();
    }

    private async Task EnsureSearchStationsAsync()
    {
        var query = ParseSearch(GlobalSearchBox.Text).Term;
        if (query.Length < 2) return;
        _searchStationsLoading = true;
        SearchFooterText.Text = "Searching the free FM directory…";
        _searchRadioCancellation?.Cancel();
        _searchRadioCancellation?.Dispose();
        _searchRadioCancellation = new CancellationTokenSource();
        var token = _searchRadioCancellation.Token;
        try
        {
            var stations = await _radioService.SearchStationsAsync(query, _settings.StreamQuality, token);
            if (token.IsCancellationRequested || !ParseSearch(GlobalSearchBox.Text).Term.Equals(query, StringComparison.OrdinalIgnoreCase)) return;
            _searchStations = stations;
            _lastStationSearchQuery = query;
            _searchStationsLoaded = true;
            BuildUnifiedSearchResults();
            SearchFooterText.Text = "Tip: try artist:, album:, song:, or fm:";
        }
        catch (OperationCanceledException) { }
        catch
        {
            SearchFooterText.Text = "FM search is offline · local music search still works";
        }
        finally
        {
            _searchStationsLoading = false;
        }
    }

    private async Task ActivateSearchResultAsync(SearchResult result)
    {
        GlobalSearchPopup.IsOpen = false;
        GlobalSearchBox.Clear();

        if (result.Track is not null)
        {
            NavigateTop("Music");
            SelectPaneItem("songs");
            ShowMusicMode("songs");
            var index = _allTracks.FindIndex(track => track.Path.Equals(result.Track.Path, StringComparison.OrdinalIgnoreCase));
            SetQueue(_allTracks, index < 0 ? 0 : index);
            await PlayTrackAsync(result.Track, allowCrossfade: _isPlaying && !_isRadio);
            return;
        }

        if (result.Station is not null)
        {
            NavigateTop("FM");
            await PlayStationAsync(result.Station);
            return;
        }

        if (result.Kind is "Artist" or "Album")
        {
            NavigateTop("Music");
            var isArtist = result.Kind == "Artist";
            SelectPaneItem(isArtist ? "artists" : "albums");
            ShowMusicMode(isArtist ? "artists" : "albums");
            OpenLibraryGroup(new LibraryGroup
            {
                Name = result.Key,
                Kind = isArtist ? "artist" : "album",
                Count = _allTracks.Count(track => isArtist
                    ? track.DisplayArtist.Equals(result.Key, StringComparison.CurrentCultureIgnoreCase)
                    : track.DisplayAlbum.Equals(result.Key, StringComparison.CurrentCultureIgnoreCase)),
                Duration = TimeSpan.FromTicks(_allTracks.Where(track => isArtist
                    ? track.DisplayArtist.Equals(result.Key, StringComparison.CurrentCultureIgnoreCase)
                    : track.DisplayAlbum.Equals(result.Key, StringComparison.CurrentCultureIgnoreCase)).Sum(track => track.Duration.Ticks))
            });
        }
    }

    private static ParsedSearch ParseSearch(string? raw)
    {
        var text = raw?.Trim() ?? string.Empty;
        foreach (var prefix in new[]
                 {
                     ("artist:", SearchScope.Artist),
                     ("album:", SearchScope.Album),
                     ("music:", SearchScope.Music),
                     ("song:", SearchScope.Song),
                     ("radio:", SearchScope.Radio),
                     ("fm:", SearchScope.Radio)
                 })
        {
            if (text.StartsWith(prefix.Item1, StringComparison.OrdinalIgnoreCase))
                return new ParsedSearch(text[prefix.Item1.Length..].Trim(), prefix.Item2);
        }
        return new ParsedSearch(text, SearchScope.All);
    }

    private static int MatchScore(string query, params (string? Value, int Weight)[] fields)
    {
        if (string.IsNullOrWhiteSpace(query)) return 0;
        var best = 0;
        foreach (var (value, weight) in fields)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (value.Equals(query, StringComparison.CurrentCultureIgnoreCase)) best = Math.Max(best, 140 + weight);
            else if (value.StartsWith(query, StringComparison.CurrentCultureIgnoreCase)) best = Math.Max(best, 110 + weight);
            else if (value.Contains($" {query}", StringComparison.CurrentCultureIgnoreCase)) best = Math.Max(best, 92 + weight);
            else if (value.Contains(query, StringComparison.CurrentCultureIgnoreCase)) best = Math.Max(best, 70 + weight);
        }
        if (best > 0) return best;

        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length <= 1) return 0;
        var combined = string.Join(' ', fields.Select(field => field.Value).Where(value => !string.IsNullOrWhiteSpace(value)));
        return tokens.All(token => combined.Contains(token, StringComparison.CurrentCultureIgnoreCase)) ? 58 : 0;
    }

    private static string JoinSearchDetail(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first)) return string.IsNullOrWhiteSpace(second) ? "Live radio" : second;
        return string.IsNullOrWhiteSpace(second) ? first : $"{first} · {second}";
    }

    #endregion

    #region Library, home, and devices

    private async Task ScanLibraryAsync(bool showCompletion)
    {
        _libraryCancellation?.Cancel();
        _libraryCancellation?.Dispose();
        _libraryCancellation = new CancellationTokenSource();
        var token = _libraryCancellation.Token;
        ShowHeaderStatus("Scanning music…");
        var progress = new Progress<ScanProgress>(value =>
        {
            HeaderStatusText.Text = value.TracksLoaded > 0 ? $"{value.TracksLoaded:N0} songs found" : "Scanning music…";
        });

        try
        {
            var cached = _allTracks.Count > 0 ? _allTracks : AppStorage.LoadLibraryCache();
            var tracks = await LibraryService.ScanAsync(_settings.MediaFolders, cached, _settings.LiteMode, progress, token);
            if (token.IsCancellationRequested) return;
            UpdateLibrary(tracks);
            StartArtworkEnrichment(tracks);
            _ = Task.Run(() => AppStorage.SaveLibraryCache(tracks));
            if (showCompletion) ShowToast(tracks.Count == 1 ? "1 song is ready." : $"{tracks.Count:N0} songs are ready.", "\uE73E");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            ShowToast($"Library scan could not finish: {exception.Message}", "\uE7BA");
        }
        finally
        {
            if (!token.IsCancellationRequested) HideHeaderStatus();
        }
    }

    private void UpdateLibrary(IReadOnlyCollection<TrackItem> tracks)
    {
        _allTracks = tracks.ToList();
        var total = TimeSpan.FromTicks(_allTracks.Sum(t => t.Duration.Ticks));
        LibraryStatsText.Text = _allTracks.Count == 1 ? "1 song" : $"{_allTracks.Count:N0} songs";
        if (total.TotalHours >= 1) LibraryStatsText.Text += $"  ·  {(int)total.TotalHours} hr {total.Minutes} min";
        HeroTitle.Text = _allTracks.Count > 0 ? $"{_allTracks.Count:N0} songs, one beautiful library" : "Bring your library to life";
        HeroDescription.Text = _allTracks.Count > 0 ? "Pick up where you left off or discover a live station." : "Add a music folder or tune in to a live station.";
        if (_currentSection == "Music") ShowMusicMode(_musicMode);
        if (!string.IsNullOrWhiteSpace(GlobalSearchBox.Text)) BuildUnifiedSearchResults();
    }

    private void StartArtworkEnrichment(IReadOnlyCollection<TrackItem> tracks)
    {
        _artworkCancellation?.Cancel();
        _artworkCancellation?.Dispose();
        _artworkCancellation = new CancellationTokenSource();
        var token = _artworkCancellation.Token;
        var progress = new Progress<ArtworkUpdate>(update =>
        {
            foreach (var track in tracks.Where(track =>
                         track.DisplayArtist.Equals(update.Artist, StringComparison.CurrentCultureIgnoreCase) &&
                         track.DisplayAlbum.Equals(update.Album, StringComparison.CurrentCultureIgnoreCase)))
                track.ArtworkPath = update.ArtworkPath;

            foreach (var group in LibraryGroups)
            {
                if ((group.Kind == "artist" && group.Name.Equals(update.Artist, StringComparison.CurrentCultureIgnoreCase)) ||
                    (group.Kind == "album" && group.Name.Equals(update.Album, StringComparison.CurrentCultureIgnoreCase)))
                    group.ArtworkPath = update.ArtworkPath;
            }

            var recentUpdated = false;
            foreach (var recent in _settings.RecentPlays.Where(recent =>
                         !recent.IsRadio &&
                         recent.Artist.Equals(update.Artist, StringComparison.CurrentCultureIgnoreCase) &&
                         recent.Album.Equals(update.Album, StringComparison.CurrentCultureIgnoreCase)))
            {
                recent.ArtworkPath = update.ArtworkPath;
                recentUpdated = true;
            }
            if (recentUpdated)
            {
                RefreshRecentItems();
                SaveSoon();
            }

            if (_currentTrack is not null &&
                _currentTrack.DisplayArtist.Equals(update.Artist, StringComparison.CurrentCultureIgnoreCase) &&
                _currentTrack.DisplayAlbum.Equals(update.Album, StringComparison.CurrentCultureIgnoreCase))
                SetNowPlayingArtwork(update.ArtworkPath);
        });

        _ = EnrichArtworkAsync(tracks, progress, token);
    }

    private async Task EnrichArtworkAsync(IReadOnlyCollection<TrackItem> tracks, IProgress<ArtworkUpdate> progress, CancellationToken token)
    {
        try
        {
            await _artworkService.PopulateMissingArtworkAsync(tracks, _settings.LiteMode, progress, token);
            if (!token.IsCancellationRequested) await Task.Run(() => AppStorage.SaveLibraryCache(_allTracks), token);
        }
        catch (OperationCanceledException) { }
        catch
        {
            // Album art is optional; local playback must remain unaffected when the directory is offline.
        }
    }

    private async Task RefreshDevicesAsync()
    {
        if (_devicesRefreshing) return;
        _devicesRefreshing = true;
        try
        {
            var devices = await Task.Run(LibraryService.GetDevices);
            Devices.ReplaceAll(devices);
            MusicDevicesEmptyText.Visibility = Devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch
        {
            // A disconnected or inaccessible drive must not delay the rest of the UI.
        }
        finally
        {
            _devicesRefreshing = false;
        }
    }

    private void RefreshRecentItems()
    {
        _settings.RecentPlays = _settings.RecentPlays
            .Where(item => !string.IsNullOrWhiteSpace(item.Path))
            .OrderByDescending(item => item.PlayedAt)
            .Take(30)
            .ToList();
        RecentItems.ReplaceAll(_settings.RecentPlays.Take(12));
        RecentEmptyText.Visibility = RecentItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HomeRecentList.Visibility = RecentItems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowMusicMode(string mode)
    {
        _musicMode = mode;
        UpdateMusicContentLayout(mode);
        MusicEmptyPanel.Visibility = Visibility.Collapsed;
        MusicSongList.Visibility = Visibility.Collapsed;
        GroupScroll.Visibility = Visibility.Collapsed;
        MusicDevicesScroll.Visibility = Visibility.Collapsed;

        switch (mode)
        {
            case "artists":
                MusicPageTitle.Text = "Artists";
                MusicPageSubtitle.Text = "The voices and creators in your library.";
                SetGroups(_allTracks.GroupBy(t => t.DisplayArtist, StringComparer.CurrentCultureIgnoreCase), "artist");
                break;
            case "albums":
                MusicPageTitle.Text = "Albums";
                MusicPageSubtitle.Text = "Records and collections, organized for you.";
                SetGroups(_allTracks.GroupBy(t => t.DisplayAlbum, StringComparer.CurrentCultureIgnoreCase), "album");
                break;
            case "devices":
                MusicPageTitle.Text = "Devices";
                MusicPageSubtitle.Text = "Play music from discovered local, removable and network drives.";
                _ = RefreshDevicesAsync();
                MusicDevicesScroll.Visibility = Visibility.Visible;
                break;
            case "device-results":
                ApplyMusicTrackFilter(_deviceTracks);
                break;
            default:
                _musicMode = "songs";
                MusicPageTitle.Text = "All Songs";
                MusicPageSubtitle.Text = "Your complete music library.";
                ApplyMusicTrackFilter(_allTracks);
                break;
        }
    }

    private void UpdateMusicContentLayout(string mode)
    {
        // Artists, Albums, and Devices use two equal visual surfaces. Track lists
        // retain the long visualizer span while reserving enough width for readable titles.
        var equalSplit = mode is "artists" or "albums" or "devices";
        MusicVisualizerColumn.Width = new GridLength(1, GridUnitType.Star);
        MusicListColumn.Width = equalSplit
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(300, GridUnitType.Pixel);
    }

    private void SetGroups(IEnumerable<IGrouping<string, TrackItem>> source, string kind)
    {
        var search = ParseSearch(GlobalSearchBox.Text);
        var query = search.Scope == SearchScope.Radio ? string.Empty : search.Term;
        var groups = source
            .Where(group => string.IsNullOrWhiteSpace(query) || group.Key.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .Select(group => new LibraryGroup
            {
                Name = group.Key,
                Kind = kind,
                Count = group.Count(),
                Duration = TimeSpan.FromTicks(group.Sum(track => track.Duration.Ticks)),
                ArtworkPath = group.Select(track => track.ArtworkPath).FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path)) ?? string.Empty
            })
            .OrderBy(group => group.Name.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase))
            .ThenBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        LibraryGroups.ReplaceAll(groups);
        GroupScroll.Visibility = groups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MusicEmptyPanel.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyMusicTrackFilter(IEnumerable<TrackItem> source)
    {
        var search = ParseSearch(GlobalSearchBox.Text);
        var query = search.Scope == SearchScope.Radio ? string.Empty : search.Term;
        var filtered = source.Where(track =>
            string.IsNullOrWhiteSpace(query) || (search.Scope switch
            {
                SearchScope.Artist => track.DisplayArtist.Contains(query, StringComparison.CurrentCultureIgnoreCase),
                SearchScope.Album => track.DisplayAlbum.Contains(query, StringComparison.CurrentCultureIgnoreCase),
                SearchScope.Song => track.DisplayTitle.Contains(query, StringComparison.CurrentCultureIgnoreCase),
                _ => track.DisplayTitle.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                     track.DisplayArtist.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                     track.DisplayAlbum.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                     track.Genre.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            })).ToList();
        VisibleTracks.ReplaceAll(filtered);
        MusicSongList.Visibility = filtered.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MusicEmptyPanel.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void AddFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a folder that contains music",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FolderName)) return;
        if (_settings.MediaFolders.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase))
        {
            ShowToast("That folder is already in your library.");
            return;
        }
        _settings.MediaFolders.Add(dialog.FolderName);
        UpdateFolderList();
        AppStorage.SaveSettings(_settings);
        NavigateTop("Music");
        await ScanLibraryAsync(showCompletion: true);
    }

    private async void RemoveFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: string folder }) return;
        var answer = System.Windows.MessageBox.Show(
            $"Stop scanning this folder?\n\n{folder}\n\nNo music files will be deleted.",
            "Remove media folder",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        _settings.MediaFolders.RemoveAll(path => path.Equals(folder, StringComparison.OrdinalIgnoreCase));
        UpdateFolderList();
        AppStorage.SaveSettings(_settings);
        await ScanLibraryAsync(showCompletion: true);
    }

    private async void RescanButton_Click(object sender, RoutedEventArgs e) => await ScanLibraryAsync(showCompletion: true);

    private async void DeviceCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: DriveItem drive }) return;
        if (!drive.IsReady)
        {
            ShowToast("Windows reports that this drive is not ready.", "\uE7BA");
            return;
        }

        NavigateTop("Music");
        SelectPaneItem("devices");
        _deviceCancellation?.Cancel();
        _deviceCancellation?.Dispose();
        _deviceCancellation = new CancellationTokenSource();
        var token = _deviceCancellation.Token;
        MusicPageTitle.Text = drive.Name;
        MusicPageSubtitle.Text = $"Discovering music on {drive.RootPath}";
        MusicDevicesScroll.Visibility = Visibility.Collapsed;
        MusicSongList.Visibility = Visibility.Collapsed;
        MusicEmptyPanel.Visibility = Visibility.Visible;
        ShowHeaderStatus($"Scanning {drive.Name}…");
        var progress = new Progress<ScanProgress>(p => HeaderStatusText.Text = $"{p.TracksLoaded:N0} songs on {drive.Name}");
        try
        {
            _deviceTracks = await LibraryService.ScanDriveAsync(drive.RootPath, _settings.LiteMode, progress, token);
            if (token.IsCancellationRequested) return;
            _musicMode = "device-results";
            UpdateMusicContentLayout(_musicMode);
            MusicPageSubtitle.Text = _deviceTracks.Count == 1 ? "1 song found on this drive." : $"{_deviceTracks.Count:N0} songs found on this drive.";
            ApplyMusicTrackFilter(_deviceTracks);
            StartArtworkEnrichment(_allTracks.Concat(_deviceTracks).ToList());
            ShowToast(_deviceTracks.Count == 0 ? "No supported music files were found." : $"Found {_deviceTracks.Count:N0} songs on {drive.Name}.");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            ShowToast($"Could not scan this drive: {exception.Message}", "\uE7BA");
        }
        finally
        {
            if (!token.IsCancellationRequested) HideHeaderStatus();
        }
    }

    private void GroupCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: LibraryGroup group }) OpenLibraryGroup(group);
    }

    private void OpenLibraryGroup(LibraryGroup group)
    {
        var tracks = group.Kind == "artist"
            ? _allTracks.Where(track => track.DisplayArtist.Equals(group.Name, StringComparison.CurrentCultureIgnoreCase)).ToList()
            : _allTracks.Where(track => track.DisplayAlbum.Equals(group.Name, StringComparison.CurrentCultureIgnoreCase)).ToList();
        MusicPageTitle.Text = group.Name;
        MusicPageSubtitle.Text = $"{group.CountText}  ·  {group.DurationText}";
        VisibleTracks.ReplaceAll(tracks);
        GroupScroll.Visibility = Visibility.Collapsed;
        MusicEmptyPanel.Visibility = tracks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MusicSongList.Visibility = tracks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HomeOpenMusic_Click(object sender, RoutedEventArgs e) => NavigateTop("Music");

    private void HomeOpenLocalFm_Click(object sender, RoutedEventArgs e)
    {
        _radioMode = "local";
        NavigateTop("FM");
    }

    private async void HomeStationRecommendation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: RadioStation station }) await PlayStationAsync(station);
        e.Handled = true;
    }

    private void OpenPlayerSettings_Click(object sender, RoutedEventArgs e)
    {
        _settingsDestination = "player";
        NavigateTop("Settings");
    }

    private async void PlaySomethingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_allTracks.Count > 0)
        {
            var random = Random.Shared.Next(_allTracks.Count);
            SetQueue(_allTracks, random);
            await PlayTrackAsync(_allTracks[random], allowCrossfade: false);
        }
        else
        {
            NavigateTop("Music");
            ShowToast("Add a media folder to start your library.");
        }
    }

    #endregion

    #region Radio

    private async Task LoadRadioStationsAsync(bool local, bool forceRefresh)
    {
        _radioCancellation?.Cancel();
        _radioCancellation?.Dispose();
        _radioCancellation = new CancellationTokenSource();
        var token = _radioCancellation.Token;
        _radioMode = local ? "local" : "worldwide";
        SelectPaneItem(_radioMode);
        RadioPageTitle.Text = local ? "Local FM" : "Worldwide FM";
        RadioPageSubtitle.Text = local
            ? "Finding healthy public streams near your approximate location."
            : _settings.LiteMode
                ? "Scanning the worldwide free directory in pages (Lite Mode safety limit: 5,000)."
                : "Scanning the complete healthy, free worldwide directory in pages.";
        RadioLoadingTitle.Text = local ? "Finding nearby stations…" : "Scanning worldwide radio…";
        RadioLoadingText.Text = "Connecting to the public Radio Browser directory";
        RadioLoadingPanel.Visibility = Visibility.Visible;
        RadioEmptyPanel.Visibility = Visibility.Collapsed;
        RadioStationList.Visibility = Visibility.Collapsed;
        RadioCountText.Text = "Scanning…";
        _allStations = [];
        VisibleStations.ReplaceAll([]);

        var progress = new Progress<RadioScanProgress>(scan =>
        {
            if (token.IsCancellationRequested) return;
            RadioLoadingText.Text = scan.Message;
            RadioCountText.Text = scan.Message;
            if (local || scan.NewStations.Count == 0) return;
            _allStations.AddRange(scan.NewStations);
            var search = ParseSearch(GlobalSearchBox.Text);
            var stationQuery = search.Scope is SearchScope.Music or SearchScope.Song or SearchScope.Artist or SearchScope.Album
                ? string.Empty
                : search.Term;
            if (string.IsNullOrWhiteSpace(stationQuery))
            {
                // Preserve already-realized rows and append only the new page instead
                // of repeatedly copying the complete worldwide catalog on the UI thread.
                VisibleStations.AddRange(scan.NewStations);
                RadioLoadingPanel.Visibility = Visibility.Collapsed;
                RadioEmptyPanel.Visibility = Visibility.Collapsed;
                RadioStationList.Visibility = Visibility.Visible;
            }
            else
            {
                ApplyRadioFilter();
            }
            RadioCountText.Text = scan.Message;
        });

        try
        {
            var stations = local
                ? await _radioService.ScanLocalStationsAsync(_settings.StreamQuality, forceRefresh, progress, token)
                : await _radioService.ScanWorldwideStationsAsync(_settings.StreamQuality, forceRefresh, _settings.LiteMode ? 5000 : null, progress, token);
            if (token.IsCancellationRequested) return;
            _allStations = stations;
            if (local)
                RadioPageSubtitle.Text = $"Healthy public streams near {_radioService.LocalLocationName}, plus every available station in {_radioService.LocalCountryName}.";
            ApplyRadioFilter();
            if (!string.IsNullOrWhiteSpace(GlobalSearchBox.Text)) BuildUnifiedSearchResults();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (token.IsCancellationRequested) return;
            _allStations = [];
            VisibleStations.ReplaceAll([]);
            RadioLoadingPanel.Visibility = Visibility.Collapsed;
            RadioStationList.Visibility = Visibility.Collapsed;
            RadioEmptyPanel.Visibility = Visibility.Visible;
            RadioEmptyTitle.Text = "The station list is offline";
            RadioEmptyDescription.Text = "Check your internet connection, then choose Refresh stations. Your local music remains fully available.";
            RadioCountText.Text = "Offline";
            ShowToast($"FM list unavailable: {FriendlyNetworkMessage(exception)}", "\uE7BA");
        }
    }

    private void ApplyRadioFilter()
    {
        var search = ParseSearch(GlobalSearchBox.Text);
        var query = search.Scope is SearchScope.Music or SearchScope.Song or SearchScope.Artist or SearchScope.Album ? string.Empty : search.Term;
        var filtered = _allStations.Where(station =>
            string.IsNullOrWhiteSpace(query) ||
            station.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            station.Country.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            station.State.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            station.Tags.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            station.Language.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
        VisibleStations.ReplaceAll(filtered);
        RadioLoadingPanel.Visibility = Visibility.Collapsed;
        RadioEmptyPanel.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RadioStationList.Visibility = filtered.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RadioEmptyTitle.Text = string.IsNullOrWhiteSpace(query) ? "No healthy stations found" : "No stations match your search";
        RadioEmptyDescription.Text = string.IsNullOrWhiteSpace(query) ? "Refresh the list or try the other FM collection." : "Try a station name, country, city or genre.";
        RadioCountText.Text = filtered.Count == 1 ? "1 station" : $"{filtered.Count:N0} stations";
    }

    private async void RefreshRadioButton_Click(object sender, RoutedEventArgs e)
    {
        AppStorage.ClearRadioCache();
        _radioService.InvalidateHomeRecommendations();
        _searchRadioCancellation?.Cancel();
        _searchStations = [];
        _searchStationsLoaded = false;
        _searchStationsLoading = false;
        if (_currentSection == "FM")
            await LoadRadioStationsAsync(_radioMode == "local", forceRefresh: true);
        else
            ShowToast("FM station cache cleared. The list will refresh when you open FM.", "\uE72C");
    }

    private async void StationPlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: RadioStation station })
        {
            RadioStationList.SelectedItem = station;
            await PlayStationAsync(station);
        }
        e.Handled = true;
    }

    private async void RadioStationList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindVisualAncestor<Button>(source) is not null) return;
        var station = e.OriginalSource is DependencyObject origin
            ? (ItemsControl.ContainerFromElement(RadioStationList, origin) as ListBoxItem)?.DataContext as RadioStation
            : null;
        station ??= RadioStationList.SelectedItem as RadioStation;
        if (station is null) return;
        RadioStationList.SelectedItem = station;
        await PlayStationAsync(station);
        e.Handled = true;
    }

    private async Task PlayStationAsync(RadioStation station, bool preferOriginalUrl = false)
    {
        _playCancellation?.Cancel();
        _playCancellation?.Dispose();
        _playCancellation = new CancellationTokenSource();
        var token = _playCancellation.Token;
        try
        {
            ShowToast(preferOriginalUrl ? $"Trying {station.Name}'s alternate stream…" : $"Tuning {station.Name}…", "\uE720");
            var source = preferOriginalUrl
                ? new RadioStation { Url = station.Url, UrlResolved = string.Empty, LastCheckOk = 1, Name = station.Name }
                : station;
            var streamUrl = await _radioService.ResolveStreamUrlAsync(source, token);
            if (token.IsCancellationRequested) return;
            if (!Uri.TryCreate(streamUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw new InvalidOperationException("The station supplied an invalid stream address.");

            if (_currentTrack is not null) _currentTrack.IsPlaying = false;
            _audio.Stop();
            try { RadioMedia.Close(); } catch { }

            _currentStation = station;
            _currentTrack = null;
            _currentRadioUri = uri;
            _isRadio = true;
            _isPlaying = false;
            UpdateMusicTransportControls();
            _radioUsesMediaElement = false;
            _radioFallbackAttempted = false;
            _radioOriginalAttempted = preferOriginalUrl;
            _crossfadeTriggered = false;

            try
            {
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
                startup.CancelAfter(TimeSpan.FromSeconds(12));
                await _audio.PlayStreamAsync(uri.AbsoluteUri, startup.Token);
                if (token.IsCancellationRequested)
                {
                    _audio.Stop();
                    return;
                }
                _isPlaying = true;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                StartRadioMediaFallback(uri);
            }
            catch (Exception)
            {
                StartRadioMediaFallback(uri);
            }

            UpdateNowPlaying(station.Name, string.IsNullOrWhiteSpace(station.LocationText) ? "Live FM radio" : station.LocationText, station.Name, station.Favicon);
            PlaybackSlider.IsEnabled = false;
            PlaybackSlider.Value = 0;
            CurrentTimeText.Text = "LIVE";
            TotalTimeText.Text = station.Bitrate > 0 ? $"{station.Bitrate}k" : "FM";
            UpdatePlayPauseVisual();
            if (!_radioUsesMediaElement) AddRecentStation(station, streamUrl);
            _ = _radioService.RegisterClickAsync(station);
            if (!_radioUsesMediaElement) ShowToast($"Live now: {station.Name}", "\uE768");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            _isPlaying = false;
            UpdatePlayPauseVisual();
            ShowToast($"This station could not be played: {FriendlyNetworkMessage(exception)}", "\uE7BA");
        }
    }

    private void StartRadioMediaFallback(Uri uri)
    {
        _audio.Stop();
        try { RadioMedia.Close(); } catch { }
        RadioMedia.Source = uri;
        RadioMedia.Volume = _settings.Volume;
        _radioUsesMediaElement = true;
        _radioFallbackAttempted = true;
        _isPlaying = true;
        _audio.StartOutputCaptureAnalysis();
        RadioMedia.Play();
    }

    private void RadioMedia_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (!_isRadio || !_radioUsesMediaElement) return;
        _isPlaying = true;
        UpdatePlayPauseVisual();
        if (_currentStation is not null)
        {
            AddRecentStation(_currentStation, _currentRadioUri?.AbsoluteUri);
            ShowToast($"Live now: {_currentStation.Name}", "\uE768");
        }
    }

    private async void RadioMedia_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (!_isRadio || !_radioUsesMediaElement) return;
        var station = _currentStation;
        if (!_radioOriginalAttempted && station is not null &&
            Uri.TryCreate(station.Url, UriKind.Absolute, out var original) &&
            original.Scheme is "http" or "https" &&
            !original.AbsoluteUri.Equals(_currentRadioUri?.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
        {
            _radioOriginalAttempted = true;
            await PlayStationAsync(station, preferOriginalUrl: true);
            return;
        }
        _audio.StopOutputCaptureAnalysis();
        _isPlaying = false;
        UpdatePlayPauseVisual();
        ShowToast("This broadcaster's stream is currently offline or uses an unsupported format. Try another station.", "\uE7BA");
    }

    #endregion

    #region Playback

    private async void TrackPlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: TrackItem track }) return;
        MusicSongList.SelectedItem = track;
        SetQueue(VisibleTracks.Count > 0 ? VisibleTracks : _allTracks, track);
        await PlayTrackAsync(track, allowCrossfade: _isPlaying && !_isRadio);
        e.Handled = true;
    }

    private async void MusicSongList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindVisualAncestor<Button>(source) is not null) return;
        var track = e.OriginalSource is DependencyObject origin
            ? (ItemsControl.ContainerFromElement(MusicSongList, origin) as ListBoxItem)?.DataContext as TrackItem
            : null;
        track ??= MusicSongList.SelectedItem as TrackItem;
        if (track is null) return;
        MusicSongList.SelectedItem = track;
        SetQueue(VisibleTracks.Count > 0 ? VisibleTracks : _allTracks, track);
        await PlayTrackAsync(track, allowCrossfade: _isPlaying && !_isRadio);
        e.Handled = true;
    }

    private async void HomeRecentList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindVisualAncestor<Button>(source) is not null) return;
        var recent = e.OriginalSource is DependencyObject origin
            ? (ItemsControl.ContainerFromElement(HomeRecentList, origin) as ListBoxItem)?.DataContext as RecentPlay
            : null;
        recent ??= HomeRecentList.SelectedItem as RecentPlay;
        if (recent is null) return;
        HomeRecentList.SelectedItem = recent;
        await PlayRecentAsync(recent);
        e.Handled = true;
    }

    private async void RecentPlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: RecentPlay recent }) await PlayRecentAsync(recent);
        e.Handled = true;
    }

    private async Task PlayRecentAsync(RecentPlay recent)
    {
        if (recent.Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || recent.Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            await PlayStationAsync(new RadioStation { Name = recent.Title, Url = recent.Path, UrlResolved = recent.Path, Country = recent.Artist, Tags = recent.Album, LastCheckOk = 1 });
            return;
        }
        var track = _allTracks.FirstOrDefault(item => item.Path.Equals(recent.Path, StringComparison.OrdinalIgnoreCase)) ?? recent.ToTrack();
        if (!File.Exists(track.Path))
        {
            ShowToast("That music file is no longer available at its original location.", "\uE7BA");
            return;
        }
        var index = _allTracks.FindIndex(item => item.Path.Equals(track.Path, StringComparison.OrdinalIgnoreCase));
        SetQueue(index >= 0 ? _allTracks : [track], index >= 0 ? index : 0);
        await PlayTrackAsync(track, allowCrossfade: _isPlaying && !_isRadio);
    }

    private async Task PlayTrackAsync(TrackItem track, bool allowCrossfade, bool addToRecent = true)
    {
        if (!File.Exists(track.Path))
        {
            ShowToast("The selected file is unavailable. Reconnect its drive or rescan the library.", "\uE7BA");
            return;
        }

        _playCancellation?.Cancel();
        _playCancellation?.Dispose();
        _playCancellation = new CancellationTokenSource();
        var token = _playCancellation.Token;
        var canFade = allowCrossfade && _settings.CrossfadeEnabled && !_isRadio && _currentTrack is not null;
        var fade = canFade ? TimeSpan.FromSeconds(_settings.CrossfadeSeconds) : TimeSpan.Zero;

        try
        {
            if (_isRadio && _radioUsesMediaElement)
            {
                try { RadioMedia.Close(); } catch { }
            }
            var previous = _currentTrack;
            await _audio.PlayFileAsync(track.Path, fade, token);
            if (token.IsCancellationRequested) return;
            if (previous is not null && !ReferenceEquals(previous, track)) previous.IsPlaying = false;
            track.IsPlaying = true;
            _currentTrack = track;
            _currentStation = null;
            _currentRadioUri = null;
            _radioUsesMediaElement = false;
            _radioFallbackAttempted = false;
            _radioOriginalAttempted = false;
            _isRadio = false;
            _isPlaying = true;
            UpdateMusicTransportControls();
            _crossfadeTriggered = false;
            var actualDuration = _audio.Duration;
            if (track.Duration <= TimeSpan.Zero && actualDuration > TimeSpan.Zero) track.Duration = actualDuration;
            UpdateNowPlaying(track.DisplayTitle, track.DisplayArtist, track.DisplayAlbum, track.ArtworkPath);
            PlaybackSlider.IsEnabled = true;
            UpdatePlayPauseVisual();
            if (addToRecent) AddRecentTrack(track);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            _audio.Stop();
            _isPlaying = false;
            UpdatePlayPauseVisual();
            ShowToast($"Chistara could not play this file: {exception.Message}", "\uE7BA");
        }
    }

    private void SetQueue(IEnumerable<TrackItem> tracks, TrackItem selected)
    {
        var list = tracks.ToList();
        var index = list.FindIndex(item => item.Path.Equals(selected.Path, StringComparison.OrdinalIgnoreCase));
        SetQueue(list, index < 0 ? 0 : index);
    }

    private void SetQueue(IEnumerable<TrackItem> tracks, int selectedIndex)
    {
        _orderedQueue = tracks.Where(track => !string.IsNullOrWhiteSpace(track.Path)).ToList();
        if (_orderedQueue.Count == 0)
        {
            _queue = [];
            _queueIndex = -1;
            return;
        }

        var boundedIndex = Math.Clamp(selectedIndex, 0, _orderedQueue.Count - 1);
        RebuildActiveQueue(_orderedQueue[boundedIndex]);
    }

    private void RebuildActiveQueue(TrackItem? selected)
    {
        if (_orderedQueue.Count == 0)
        {
            _queue = [];
            _queueIndex = -1;
            return;
        }

        var selectedIndex = selected is null
            ? 0
            : _orderedQueue.FindIndex(track => track.Path.Equals(selected.Path, StringComparison.OrdinalIgnoreCase));
        selectedIndex = selectedIndex < 0 ? 0 : selectedIndex;
        var current = _orderedQueue[selectedIndex];

        if (!_shuffleEnabled)
        {
            _queue = _orderedQueue.ToList();
            _queueIndex = selectedIndex;
            return;
        }

        var remaining = _orderedQueue
            .Where(track => !track.Path.Equals(current.Path, StringComparison.OrdinalIgnoreCase))
            .ToList();
        for (var index = remaining.Count - 1; index > 0; index--)
        {
            var swapIndex = Random.Shared.Next(index + 1);
            (remaining[index], remaining[swapIndex]) = (remaining[swapIndex], remaining[index]);
        }
        _queue = [current, .. remaining];
        _queueIndex = 0;
    }

    private void ShuffleButton_Click(object sender, RoutedEventArgs e)
    {
        _shuffleEnabled = !_shuffleEnabled;
        _settings.ShuffleEnabled = _shuffleEnabled;
        var current = _currentTrack ?? (_queueIndex >= 0 && _queueIndex < _queue.Count ? _queue[_queueIndex] : null);
        RebuildActiveQueue(current);
        UpdateMusicTransportControls();
        SaveSoon();
        ShowToast(_shuffleEnabled ? "Shuffle is on." : "Shuffle is off.", "\uE8B1");
    }

    private void RepeatModeButton_Click(object sender, RoutedEventArgs e)
    {
        RepeatModePopup.IsOpen = !RepeatModePopup.IsOpen;
    }

    private void RepeatModeOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: string mode } ||
            !Enum.TryParse<MusicRepeatMode>(mode, true, out var repeatMode)) return;

        _repeatMode = repeatMode;
        _settings.RepeatMode = repeatMode.ToString();
        RepeatModePopup.IsOpen = false;
        UpdateMusicTransportControls();
        SaveSoon();
        ShowToast(repeatMode switch
        {
            MusicRepeatMode.All => "Repeat all is on.",
            MusicRepeatMode.One => "Repeat one is on.",
            _ => "Repeat is off."
        }, "\uE8EE");
    }

    private void UpdateMusicTransportControls()
    {
        if (ShuffleButton is null || RepeatModeButton is null) return;
        var showMusicOptions = !_isRadio && (_currentTrack is not null || _currentSection == "Music");
        var visibility = showMusicOptions ? Visibility.Visible : Visibility.Collapsed;
        ShuffleButton.Visibility = visibility;
        RepeatModeButton.Visibility = visibility;
        if (!showMusicOptions) RepeatModePopup.IsOpen = false;

        ShuffleButton.Tag = _shuffleEnabled ? "active" : null;
        ShuffleButton.ToolTip = _shuffleEnabled ? "Shuffle on" : "Shuffle off";
        RepeatModeButton.Tag = _repeatMode == MusicRepeatMode.Off ? null : "active";
        RepeatModeText.Text = _repeatMode == MusicRepeatMode.One ? "\uE8ED" : "\uE8EE";
        RepeatModeButton.ToolTip = _repeatMode switch
        {
            MusicRepeatMode.All => "Repeat all — choose repeat mode",
            MusicRepeatMode.One => "Repeat one — choose repeat mode",
            _ => "Repeat off — choose repeat mode"
        };
        RepeatOffOption.Tag = _repeatMode == MusicRepeatMode.Off ? "selected" : null;
        RepeatAllOption.Tag = _repeatMode == MusicRepeatMode.All ? "selected" : null;
        RepeatOneOption.Tag = _repeatMode == MusicRepeatMode.One ? "selected" : null;
    }

    private async void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRadio)
        {
            await StepRadioAsync(-1);
            return;
        }
        if (_audio.Position > TimeSpan.FromSeconds(4))
        {
            _audio.Seek(TimeSpan.Zero);
            return;
        }
        if (_queue.Count == 0) return;
        if (_queueIndex <= 0)
        {
            if (_repeatMode != MusicRepeatMode.All)
            {
                _audio.Seek(TimeSpan.Zero);
                return;
            }
            _queueIndex = _queue.Count - 1;
        }
        else
        {
            _queueIndex--;
        }
        await PlayTrackAsync(_queue[_queueIndex], allowCrossfade: false);
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        await PlayNextAsync(allowCrossfade: _settings.CrossfadeEnabled, automatic: false);
    }

    private async Task PlayNextAsync(bool allowCrossfade, bool automatic)
    {
        if (_isRadio)
        {
            await StepRadioAsync(1);
            return;
        }
        if (_queue.Count == 0) return;

        if (automatic && _repeatMode == MusicRepeatMode.One)
        {
            var repeatIndex = Math.Clamp(_queueIndex, 0, _queue.Count - 1);
            await PlayTrackAsync(_queue[repeatIndex], allowCrossfade: false, addToRecent: false);
            return;
        }

        var nextIndex = _queueIndex + 1;
        if (nextIndex >= _queue.Count)
        {
            if (_repeatMode == MusicRepeatMode.All)
            {
                nextIndex = 0;
            }
            else
            {
                if (automatic) CompleteMusicPlayback();
                return;
            }
        }

        _queueIndex = nextIndex;
        await PlayTrackAsync(_queue[_queueIndex], allowCrossfade);
    }

    private void CompleteMusicPlayback()
    {
        _isPlaying = false;
        _crossfadeTriggered = false;
        if (_currentTrack is not null) _currentTrack.IsPlaying = false;
        UpdatePlayPauseVisual();
    }

    private async Task StepRadioAsync(int direction)
    {
        if (_currentStation is null || VisibleStations.Count == 0) return;
        var index = VisibleStations.ToList().FindIndex(station => station.Id == _currentStation.Id && station.Name == _currentStation.Name);
        index = index < 0 ? 0 : (index + direction + VisibleStations.Count) % VisibleStations.Count;
        await PlayStationAsync(VisibleStations[index]);
    }

    private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentTrack is null && _currentStation is null)
        {
            PlaySomethingButton_Click(sender, e);
            return;
        }

        if (_isRadio)
        {
            if (_radioUsesMediaElement)
            {
                if (_isPlaying)
                {
                    RadioMedia.Pause();
                    _audio.StopOutputCaptureAnalysis();
                }
                else
                {
                    _audio.StartOutputCaptureAnalysis();
                    RadioMedia.Play();
                }
            }
            else
            {
                if (_isPlaying) _audio.Pause();
                else _audio.Resume();
            }
            _isPlaying = !_isPlaying;
            UpdatePlayPauseVisual();
            return;
        }

        if (_isPlaying)
        {
            _audio.Pause();
            _isPlaying = false;
            UpdatePlayPauseVisual();
            return;
        }

        var duration = _audio.Duration > TimeSpan.Zero ? _audio.Duration : _currentTrack!.Duration;
        var reachedEnd = duration > TimeSpan.Zero && _audio.Position >= duration - TimeSpan.FromMilliseconds(250);
        if (reachedEnd)
        {
            await PlayTrackAsync(_currentTrack!, allowCrossfade: false, addToRecent: false);
            return;
        }

        _audio.Resume();
        _currentTrack!.IsPlaying = true;
        _isPlaying = true;
        UpdatePlayPauseVisual();
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (_isRadio || _currentTrack is null) return;
        var position = _audio.Position;
        var duration = _audio.Duration > TimeSpan.Zero ? _audio.Duration : _currentTrack.Duration;
        if (!_userSeeking && duration.TotalSeconds > 0)
        {
            PlaybackSlider.Maximum = duration.TotalSeconds;
            PlaybackSlider.Value = Math.Clamp(position.TotalSeconds, 0, duration.TotalSeconds);
        }
        CurrentTimeText.Text = FormatTime(position);
        TotalTimeText.Text = FormatTime(duration);

        if (_isPlaying && _settings.CrossfadeEnabled && !_crossfadeTriggered && _queue.Count > 1 &&
            _repeatMode != MusicRepeatMode.One && (_repeatMode == MusicRepeatMode.All || _queueIndex < _queue.Count - 1) &&
            duration > TimeSpan.Zero)
        {
            var remaining = duration - position;
            if (remaining > TimeSpan.Zero && remaining <= TimeSpan.FromSeconds(_settings.CrossfadeSeconds + 0.15))
            {
                _crossfadeTriggered = true;
                _ = PlayNextAsync(allowCrossfade: true, automatic: true);
            }
        }
    }

    private void PlaybackSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _userSeeking = true;

    private void PlaybackSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isRadio) _audio.Seek(TimeSpan.FromSeconds(PlaybackSlider.Value));
        _userSeeking = false;
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumeText is null) return;
        var value = Math.Clamp(e.NewValue, 0, 1);
        VolumeText.Text = $"{value * 100:0}%";
        if (value > 0.001) _volumeBeforeMute = value;
        _audio.Volume = (float)value;
        if (RadioMedia is not null) RadioMedia.Volume = value;
        if (MuteButton is not null) MuteButton.Content = value <= 0.001 ? "\uE74F" : value < 0.45 ? "\uE993" : "\uE995";
        if (_initializing) return;
        _settings.Volume = value;
        SaveSoon();
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        VolumeSlider.Value = VolumeSlider.Value <= 0.001 ? Math.Max(0.1, _volumeBeforeMute) : 0;
    }

    private void Audio_PlaybackEnded(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            if (!_isRadio)
            {
                await PlayNextAsync(allowCrossfade: false, automatic: true);
                return;
            }
            if (!_radioUsesMediaElement && !_radioFallbackAttempted && _currentRadioUri is not null)
            {
                try
                {
                    StartRadioMediaFallback(_currentRadioUri);
                    UpdatePlayPauseVisual();
                    ShowToast("Reconnecting the live stream…", "\uE720");
                }
                catch
                {
                    _isPlaying = false;
                    UpdatePlayPauseVisual();
                }
            }
        });
    }

    private void Audio_PlaybackError(object? sender, Exception exception)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_isRadio && !_radioUsesMediaElement && !_radioFallbackAttempted && _currentRadioUri is not null)
            {
                try
                {
                    StartRadioMediaFallback(_currentRadioUri);
                    UpdatePlayPauseVisual();
                    ShowToast("The station changed format, so Chistara switched playback engines…", "\uE720");
                    return;
                }
                catch { }
            }
            _isPlaying = false;
            UpdatePlayPauseVisual();
            ShowToast($"Playback stopped: {exception.Message}", "\uE7BA");
        });
    }

    private void UpdateNowPlaying(string title, string artist, string albumSeed, string artworkSource = "")
    {
        NowTitleText.Text = title;
        NowArtistText.Text = artist;
        NowAlbumInitials.Text = TrackItem.InitialsFrom(title);
        NowAlbumTile.SetResourceReference(Border.BackgroundProperty, "AccentMediumBrush");
        SetNowPlayingArtwork(artworkSource);
    }

    private void SetNowPlayingArtwork(string? source)
    {
        var image = LoadArtworkSource(source);
        NowAlbumImage.Source = image;
        HomeHeroImage.Source = image;
    }

    private static BitmapSource? LoadArtworkSource(string? source) => ArtworkImageCache.Load(source, 360);

    private void UpdatePlayPauseVisual()
    {
        PlayPauseText.Text = _isPlaying ? "\uE769" : "\uE768";
        PlayPauseButton.ToolTip = _isPlaying ? "Pause" : "Play";
        MusicVisualizerPanel.IsPlaybackActive = _isPlaying;
        RadioVisualizerPanel.IsPlaybackActive = _isPlaying;
        UpdateHomeDiscAnimation();
    }

    private void UpdateHomeDiscAnimation()
    {
        if (_isPlaying && !_homeDiscSpinning)
        {
            var start = _homeDiscRotation.Angle;
            _homeDiscRotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(start, start + 360, TimeSpan.FromSeconds(9.5))
            {
                RepeatBehavior = RepeatBehavior.Forever
            });
            _homeDiscSpinning = true;
        }
        else if (!_isPlaying && _homeDiscSpinning)
        {
            var angle = _homeDiscRotation.Angle % 360;
            _homeDiscRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            _homeDiscRotation.Angle = angle;
            _homeDiscSpinning = false;
        }
    }

    private void AddRecentTrack(TrackItem track)
    {
        var item = new RecentPlay
        {
            Path = track.Path,
            Title = track.DisplayTitle,
            Artist = track.DisplayArtist,
            Album = track.DisplayAlbum,
            ArtworkPath = track.ArtworkPath,
            DurationTicks = track.Duration.Ticks,
            PlayedAt = DateTimeOffset.Now
        };
        AddRecent(item);
    }

    private void AddRecentStation(RadioStation station, string? playableUrl = null)
    {
        _settings.RecentPlays.RemoveAll(item => item.IsRadio && item.Title.Equals(station.Name, StringComparison.CurrentCultureIgnoreCase));
        AddRecent(new RecentPlay
        {
            Path = string.IsNullOrWhiteSpace(playableUrl) ? station.StreamUrl : playableUrl,
            Title = station.Name,
            Artist = string.IsNullOrWhiteSpace(station.LocationText) ? "Live FM radio" : station.LocationText,
            Album = station.TagsText,
            ArtworkPath = station.Favicon,
            PlayedAt = DateTimeOffset.Now
        });
    }

    private void AddRecent(RecentPlay item)
    {
        _settings.RecentPlays.RemoveAll(existing => existing.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase));
        _settings.RecentPlays.Insert(0, item);
        if (_settings.RecentPlays.Count > 30) _settings.RecentPlays.RemoveRange(30, _settings.RecentPlays.Count - 30);
        RefreshRecentItems();
        SaveSoon();
    }

    private static string FormatTime(TimeSpan value) => value.TotalHours >= 1
        ? value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
        : value.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    #endregion

    #region Settings

    private void ApplySettingsToControls()
    {
        VolumeSlider.Value = _settings.Volume;
        EqualizerToggle.IsChecked = _settings.EqualizerEnabled;
        BassSlider.Value = _settings.BassGain;
        MidSlider.Value = _settings.MidGain;
        TrebleSlider.Value = _settings.TrebleGain;
        ReverbToggle.IsChecked = _settings.ReverbEnabled;
        ReverbSlider.Value = _settings.ReverbMix;
        CrossfadeToggle.IsChecked = _settings.CrossfadeEnabled;
        CrossfadeSlider.Value = _settings.CrossfadeSeconds;
        LiteModeToggle.IsChecked = _settings.LiteMode;
        StreamQualityCombo.SelectedIndex = _settings.StreamQuality switch { "Data saver" => 0, "High" => 2, _ => 1 };
        var visualizerStyle = Enum.TryParse<VisualizationStyle>(_settings.VisualizerStyle, out var savedStyle)
            ? savedStyle
            : VisualizationStyle.Waves;
        MusicVisualizerPanel.SelectedStyle = visualizerStyle;
        RadioVisualizerPanel.SelectedStyle = visualizerStyle;
        MusicVisualizerPanel.LiteMode = _settings.LiteMode;
        RadioVisualizerPanel.LiteMode = _settings.LiteMode;
        UpdateEffectLabels();
        UpdateFolderList();
    }

    private void VisualizerPanel_StyleChanged(object? sender, VisualizationStyleChangedEventArgs e)
    {
        if (_initializing) return;
        _settings.VisualizerStyle = e.Style.ToString();
        MusicVisualizerPanel.SelectedStyle = e.Style;
        RadioVisualizerPanel.SelectedStyle = e.Style;
        SaveSoon();
    }

    private void UpdateFolderList() => MediaFolderList.ItemsSource = _settings.MediaFolders.ToList();

    private void UpdateSettingsSelectionVisuals()
    {
        ModeDarkButton.Tag = _settings.ThemeMode == "Dark" ? "selected" : null;
        ModeLightButton.Tag = _settings.ThemeMode == "Light" ? "selected" : null;
        ModeSystemButton.Tag = _settings.ThemeMode == "System" ? "selected" : null;
        var accentButtons = new[] { AccentSystem, AccentRed, AccentPink, AccentMatcha, AccentPurple, AccentCyan, AccentLime, AccentRgb };
        foreach (var button in accentButtons)
            button.Tag = string.Equals(button.CommandParameter?.ToString(), _settings.Accent, StringComparison.OrdinalIgnoreCase) ? "selected" : null;
    }

    private void ModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: string mode }) return;
        _settings.ThemeMode = mode;
        ApplyThemeAndBackdrop();
        UpdateSettingsSelectionVisuals();
        SaveSoon();
    }

    private void AccentButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: string accent }) return;
        _settings.Accent = accent;
        ApplyThemeAndBackdrop();
        UpdateSettingsSelectionVisuals();
        SaveSoon();
    }

    private void ApplyThemeAndBackdrop()
    {
        _isDark = ThemeService.Apply(_settings);
        BackdropService.Apply(this, _isDark, _settings.LiteMode);
    }

    private void EffectToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || EqualizerToggle is null) return;
        _settings.EqualizerEnabled = EqualizerToggle.IsChecked == true;
        _settings.ReverbEnabled = ReverbToggle.IsChecked == true;
        _settings.CrossfadeEnabled = CrossfadeToggle.IsChecked == true;
        ApplyAudioEffects();
        SaveSoon();
    }

    private void EffectSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing || BassValueText is null) return;
        _settings.BassGain = (float)BassSlider.Value;
        _settings.MidGain = (float)MidSlider.Value;
        _settings.TrebleGain = (float)TrebleSlider.Value;
        _settings.ReverbMix = (float)ReverbSlider.Value;
        _settings.CrossfadeSeconds = CrossfadeSlider.Value;
        UpdateEffectLabels();
        ApplyAudioEffects();
        SaveSoon();
    }

    private void UpdateEffectLabels()
    {
        BassValueText.Text = $"{BassSlider.Value:+0;-0;0} dB";
        MidValueText.Text = $"{MidSlider.Value:+0;-0;0} dB";
        TrebleValueText.Text = $"{TrebleSlider.Value:+0;-0;0} dB";
        ReverbValueText.Text = $"{ReverbSlider.Value * 100:0}%";
        CrossfadeValueText.Text = $"{CrossfadeSlider.Value:0.#} s";
    }

    private void ApplyAudioEffects()
    {
        _audio.ConfigureEqualizer(_settings.EqualizerEnabled, _settings.BassGain, _settings.MidGain, _settings.TrebleGain);
        _audio.ConfigureReverb(_settings.ReverbEnabled, _settings.ReverbMix);
    }

    private async void StreamQualityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || StreamQualityCombo.SelectedItem is not ComboBoxItem selected) return;
        _settings.StreamQuality = selected.Content?.ToString() ?? "Balanced";
        _radioService.InvalidateHomeRecommendations();
        SaveSoon();
        if (_currentSection == "FM") await LoadRadioStationsAsync(_radioMode == "local", forceRefresh: false);
    }

    private void LiteModeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || LiteModeToggle is null) return;
        _settings.LiteMode = LiteModeToggle.IsChecked == true;
        MusicVisualizerPanel.LiteMode = _settings.LiteMode;
        RadioVisualizerPanel.LiteMode = _settings.LiteMode;
        ApplyThemeAndBackdrop();
        SaveSoon();
        ShowToast(_settings.LiteMode ? "Lite mode is on. It will cap large scans and use Mica." : "Lite mode is off. Full Acrylic is active.");
    }

    private async void ResetSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var answer = System.Windows.MessageBox.Show(
            "Restore every Chistara setting to its default?\n\nThis clears preferences, history and caches. It does not delete, rename or modify any music file.",
            "Restore Chistara settings",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        _initializing = true;
        _artworkCancellation?.Cancel();
        _radioCancellation?.Cancel();
        _searchRadioCancellation?.Cancel();
        _homeRecommendationsCancellation?.Cancel();
        _radioService.InvalidateHomeRecommendations();
        AppStorage.ClearAllSettingsAndCaches();
        ArtworkImageCache.Clear();
        _settings = AppSettings.CreateDefault();
        _paneExpanded = _settings.PaneExpanded;
        _shuffleEnabled = _settings.ShuffleEnabled;
        _repeatMode = MusicRepeatMode.Off;
        RebuildActiveQueue(_currentTrack);
        ApplySettingsToControls();
        UpdateMusicTransportControls();
        ApplyPaneState(animate: false);
        ApplyThemeAndBackdrop();
        UpdateSettingsSelectionVisuals();
        _audio.Volume = (float)_settings.Volume;
        _audio.ConfigureEqualizer(_settings.EqualizerEnabled, _settings.BassGain, _settings.MidGain, _settings.TrebleGain);
        _audio.ConfigureReverb(_settings.ReverbEnabled, _settings.ReverbMix);
        _initializing = false;
        RefreshRecentItems();
        AppStorage.SaveSettings(_settings);
        ShowToast("Chistara settings were restored. Your music files were untouched.", "\uE72C");
        await ScanLibraryAsync(showCompletion: false);
    }

    #endregion

    #region Helpers

    private void ShowHeaderStatus(string message)
    {
        HeaderStatusText.Text = message;
        HeaderStatusPill.Visibility = Visibility.Visible;
    }

    private void HideHeaderStatus() => HeaderStatusPill.Visibility = Visibility.Collapsed;

    private void ShowToast(string message, string icon = "\uE946")
    {
        ToastText.Text = message;
        ToastIcon.Text = icon;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void SaveSoon()
    {
        if (_initializing) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private static string FriendlyNetworkMessage(Exception exception)
    {
        if (exception is HttpRequestException) return "check your internet connection and try again.";
        if (exception is TaskCanceledException) return "the request timed out.";
        return exception.Message;
    }

    private static T? FindVisualAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T match) return match;
            try { current = VisualTreeHelper.GetParent(current); }
            catch { current = LogicalTreeHelper.GetParent(current); }
        }
        return null;
    }

    private static bool IsPositionVisible(double left, double top) =>
        left > SystemParameters.VirtualScreenLeft - 200 &&
        left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
        top > SystemParameters.VirtualScreenTop - 50 &&
        top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 50;

    #endregion
}
