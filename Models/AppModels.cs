using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Chistara.Models;

public sealed class TrackItem : INotifyPropertyChanged
{
    private static readonly char[] InitialSeparators = [' ', '-', '_', '.', '&'];
    private bool _isPlaying;
    private string _artworkPath = string.Empty;

    public string Path { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public string Source { get; set; } = "Library";
    public string ArtworkPath
    {
        get => _artworkPath;
        set
        {
            var normalized = value ?? string.Empty;
            if (_artworkPath == normalized) return;
            _artworkPath = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasArtwork));
        }
    }

    [JsonIgnore] public bool HasArtwork => !string.IsNullOrWhiteSpace(ArtworkPath) && File.Exists(ArtworkPath);

    [JsonIgnore]
    public bool IsAvailable => !string.IsNullOrWhiteSpace(Path) && File.Exists(Path);

    [JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? System.IO.Path.GetFileNameWithoutExtension(Path) : Title;

    [JsonIgnore]
    public string DisplayArtist => string.IsNullOrWhiteSpace(Artist) ? "Unknown artist" : Artist;

    [JsonIgnore]
    public string DisplayAlbum => string.IsNullOrWhiteSpace(Album) ? "Unknown album" : Album;

    [JsonIgnore]
    public string ListSubtitle => $"{DisplayArtist}  ·  {DisplayAlbum}";

    [JsonIgnore]
    public string DurationText => Duration <= TimeSpan.Zero
        ? "—:—"
        : Duration.TotalHours >= 1
            ? Duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : Duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    [JsonIgnore]
    public string Initials => InitialsFrom(DisplayTitle);

    [JsonIgnore]
    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (_isPlaying == value) return;
            _isPlaying = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public static string InitialsFrom(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "♪";
        var parts = text.Split(InitialSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "♪";
        return string.Concat(parts.Take(2).Select(p => char.ToUpperInvariant(p[0])));
    }
}

public sealed class RecentPlay
{
    public string Path { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string ArtworkPath { get; set; } = string.Empty;
    public long DurationTicks { get; set; }
    public DateTimeOffset PlayedAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore] public string DisplayArtist => string.IsNullOrWhiteSpace(Artist) ? "Unknown artist" : Artist;
    [JsonIgnore] public bool IsRadio => Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public string KindText => IsRadio ? "FM RADIO" : "MUSIC";
    [JsonIgnore] public string KindIcon => IsRadio ? "\uE720" : "\uE8D6";
    [JsonIgnore] public string Initials => TrackItem.InitialsFrom(Title);
    [JsonIgnore] public string PlayedText => FormatRelative(PlayedAt);
    [JsonIgnore]
    public string DurationText
    {
        get
        {
            if (DurationTicks <= 0) return "—:—";
            var duration = TimeSpan.FromTicks(DurationTicks);
            return duration.TotalHours >= 1
                ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
                : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);
        }
    }

    public TrackItem ToTrack() => new()
    {
        Path = Path,
        Title = Title,
        Artist = Artist,
        Album = Album,
        ArtworkPath = ArtworkPath,
        Duration = DurationTicks > 0 ? TimeSpan.FromTicks(DurationTicks) : TimeSpan.Zero,
        Source = "Recent"
    };

    private static string FormatRelative(DateTimeOffset value)
    {
        var span = DateTimeOffset.Now - value;
        if (span.TotalMinutes < 1) return "Just now";
        if (span.TotalHours < 1) return $"{Math.Max(1, (int)span.TotalMinutes)} min ago";
        if (span.TotalDays < 1) return $"{Math.Max(1, (int)span.TotalHours)} hr ago";
        if (span.TotalDays < 7) return $"{Math.Max(1, (int)span.TotalDays)} d ago";
        return value.LocalDateTime.ToString("MMM d", CultureInfo.CurrentCulture);
    }
}

public sealed class DriveItem
{
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB"];

    public string RootPath { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }
    public bool IsReady { get; init; }

    public string Initials => string.IsNullOrWhiteSpace(Name) ? "D" : TrackItem.InitialsFrom(Name);
    public string CapacityText => IsReady ? $"{FormatBytes(FreeBytes)} free of {FormatBytes(TotalBytes)}" : "Drive is not ready";
    public double UsedFraction => TotalBytes <= 0 ? 0 : Math.Clamp(1d - (double)FreeBytes / TotalBytes, 0, 1);

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "0 B";
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < ByteUnits.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return string.Format(CultureInfo.CurrentCulture, "{0:0.#} {1}", size, ByteUnits[unit]);
    }
}

public sealed class LibraryGroup : INotifyPropertyChanged
{
    private string _artworkPath = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public int Count { get; init; }
    public TimeSpan Duration { get; init; }
    public string ArtworkPath
    {
        get => _artworkPath;
        set
        {
            var normalized = value ?? string.Empty;
            if (_artworkPath == normalized) return;
            _artworkPath = normalized;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ArtworkPath)));
        }
    }
    public string Initials => TrackItem.InitialsFrom(Name);
    public string CountText => $"{Count:N0} {(Count == 1 ? "song" : "songs")}";
    public string DurationText => Duration.TotalHours >= 1 ? $"{(int)Duration.TotalHours} hr {Duration.Minutes} min" : $"{Math.Max(1, (int)Duration.TotalMinutes)} min";
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class PaneItem : INotifyPropertyChanged
{
    private bool _isSelected;
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Icon { get; init; } = string.Empty;
    public string Hint { get; init; } = string.Empty;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class SearchResult
{
    public string Kind { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public string Icon { get; init; } = "\uE721";
    public string ArtworkSource { get; init; } = string.Empty;
    public string Key { get; init; } = string.Empty;
    public int Score { get; init; }
    public TrackItem? Track { get; init; }
    public RadioStation? Station { get; init; }
}

public sealed class ScanProgress
{
    public int FilesSeen { get; init; }
    public int TracksLoaded { get; init; }
    public string CurrentFolder { get; init; } = string.Empty;
}

public sealed class RadioStation
{
    [JsonPropertyName("stationuuid")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("url_resolved")] public string UrlResolved { get; set; } = string.Empty;
    [JsonPropertyName("homepage")] public string HomePage { get; set; } = string.Empty;
    [JsonPropertyName("favicon")] public string Favicon { get; set; } = string.Empty;
    [JsonPropertyName("tags")] public string Tags { get; set; } = string.Empty;
    [JsonPropertyName("country")] public string Country { get; set; } = string.Empty;
    [JsonPropertyName("countrycode")] public string CountryCode { get; set; } = string.Empty;
    [JsonPropertyName("state")] public string State { get; set; } = string.Empty;
    [JsonPropertyName("language")] public string Language { get; set; } = string.Empty;
    [JsonPropertyName("codec")] public string Codec { get; set; } = string.Empty;
    [JsonPropertyName("bitrate")] public int Bitrate { get; set; }
    [JsonPropertyName("votes")] public int Votes { get; set; }
    [JsonPropertyName("clickcount")] public int ClickCount { get; set; }
    [JsonPropertyName("lastcheckok")] public int LastCheckOk { get; set; }
    [JsonPropertyName("geo_lat")] public double? GeoLatitude { get; set; }
    [JsonPropertyName("geo_long")] public double? GeoLongitude { get; set; }

    [JsonIgnore] public double? DistanceKm { get; set; }
    [JsonIgnore] public string Initials => TrackItem.InitialsFrom(Name);
    [JsonIgnore] public string StreamUrl => string.IsNullOrWhiteSpace(UrlResolved) ? Url : UrlResolved;
    [JsonIgnore] public string LocationText => string.Join(" · ", new[] { DistanceKm is >= 0 ? $"{DistanceKm:0.#} km away" : null, State, Country }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.CurrentCultureIgnoreCase));
    [JsonIgnore] public string TechnicalText => string.Join(" · ", new[] { Codec?.ToUpperInvariant(), Bitrate > 0 ? $"{Bitrate} kbps" : null, FirstLanguage() }.Where(s => !string.IsNullOrWhiteSpace(s)));
    [JsonIgnore] public string TagsText => string.IsNullOrWhiteSpace(Tags) ? "Live radio" : string.Join(", ", Tags.Split(',').Take(3));
    [JsonIgnore] public string ListSubtitle => string.Join("  ·  ", new[] { TagsText, LocationText, TechnicalText }.Where(s => !string.IsNullOrWhiteSpace(s)));

    private string FirstLanguage() => string.IsNullOrWhiteSpace(Language) ? string.Empty : Language.Split(',')[0].Trim();
}

public sealed class ApproximateLocation
{
    public string City { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string CountryCode { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public DateTimeOffset ResolvedAt { get; set; } = DateTimeOffset.UtcNow;
    [JsonIgnore] public bool HasCoordinates => Latitude is >= -90 and <= 90 && Longitude is >= -180 and <= 180 && (Math.Abs(Latitude) > 0.0001 || Math.Abs(Longitude) > 0.0001);
    [JsonIgnore] public string DisplayName => string.Join(", ", new[] { City, Region, Country }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.CurrentCultureIgnoreCase));
}

public sealed class RadioScanProgress
{
    public int StationsFound { get; init; }
    public int PagesScanned { get; init; }
    public string Message { get; init; } = string.Empty;
    [JsonIgnore] public IReadOnlyList<RadioStation> NewStations { get; init; } = Array.Empty<RadioStation>();
}

public sealed class ArtworkUpdate
{
    public string Artist { get; init; } = string.Empty;
    public string Album { get; init; } = string.Empty;
    public string ArtworkPath { get; init; } = string.Empty;
}

public sealed class AppSettings
{
    public string ThemeMode { get; set; } = "System";
    public string Accent { get; set; } = "System";
    public List<string> MediaFolders { get; set; } = [];
    public bool EqualizerEnabled { get; set; }
    public float BassGain { get; set; }
    public float MidGain { get; set; }
    public float TrebleGain { get; set; }
    public bool ReverbEnabled { get; set; }
    public float ReverbMix { get; set; } = 0.18f;
    public bool CrossfadeEnabled { get; set; }
    public double CrossfadeSeconds { get; set; } = 1;
    public bool ShuffleEnabled { get; set; }
    public string RepeatMode { get; set; } = "Off";
    public string StreamQuality { get; set; } = "Balanced";
    public bool LiteMode { get; set; }
    public string VisualizerStyle { get; set; } = "Waves";
    public double Volume { get; set; } = 0.72;
    public string LastSection { get; set; } = "Home";
    public List<RecentPlay> RecentPlays { get; set; } = [];
    public double WindowWidth { get; set; } = 1128;
    public double WindowHeight { get; set; } = 796;
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public bool PaneExpanded { get; set; } = true;

    public static AppSettings CreateDefault()
    {
        var settings = new AppSettings();
        var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (!string.IsNullOrWhiteSpace(music) && Directory.Exists(music))
            settings.MediaFolders.Add(music);
        return settings;
    }
}
