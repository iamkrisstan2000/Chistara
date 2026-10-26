using System.Text.Json;
using Chistara.Models;

namespace Chistara.Services;

public static class AppStorage
{
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string LegacyChistaraMusicRoot = Path.Combine(LocalAppData, "Chistara Music");
    private static readonly string LegacyAuralisRoot = Path.Combine(LocalAppData, "Auralis");
    private static readonly string[] LegacyRoots = [LegacyChistaraMusicRoot, LegacyAuralisRoot];

    public static string RootFolder { get; } = Path.Combine(LocalAppData, "Chistara");

    static AppStorage() => MigrateLegacyStorage();

    public static string SettingsPath => Path.Combine(RootFolder, "settings.json");
    public static string LibraryCachePath => Path.Combine(RootFolder, "library-cache.json");
    public static string RadioCacheFolder => Path.Combine(RootFolder, "radio-cache");
    public static string ArtworkFolder => Path.Combine(RootFolder, "artwork");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static AppSettings LoadSettings()
    {
        try
        {
            Directory.CreateDirectory(RootFolder);
            if (!File.Exists(SettingsPath)) return AppSettings.CreateDefault();
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? AppSettings.CreateDefault();
            Normalize(settings);
            foreach (var recent in settings.RecentPlays)
                recent.ArtworkPath = MigrateStoredPath(recent.ArtworkPath);
            return settings;
        }
        catch
        {
            return AppSettings.CreateDefault();
        }
    }

    public static void SaveSettings(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(RootFolder);
            Normalize(settings);
            var temporary = SettingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporary, SettingsPath, true);
        }
        catch
        {
            // Settings are helpful, but playback must continue if the profile is read-only.
        }
    }

    public static List<TrackItem> LoadLibraryCache()
    {
        try
        {
            if (!File.Exists(LibraryCachePath)) return [];
            var tracks = JsonSerializer.Deserialize<List<TrackItem>>(File.ReadAllText(LibraryCachePath), JsonOptions) ?? [];
            foreach (var track in tracks)
            {
                track.ArtworkPath = MigrateStoredPath(track.ArtworkPath);
                NormalizeTrackDates(track);
            }
            return tracks.Where(t => !string.IsNullOrWhiteSpace(t.Path) && File.Exists(t.Path)).ToList();
        }
        catch
        {
            return [];
        }
    }

    public static void SaveLibraryCache(IReadOnlyCollection<TrackItem> tracks)
    {
        try
        {
            Directory.CreateDirectory(RootFolder);
            var temporary = LibraryCachePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(tracks, JsonOptions));
            File.Move(temporary, LibraryCachePath, true);
        }
        catch
        {
            // A cache miss only causes a rescan next launch.
        }
    }

    public static void ClearRadioCache()
    {
        try
        {
            if (Directory.Exists(RadioCacheFolder)) Directory.Delete(RadioCacheFolder, true);
        }
        catch { }
    }

    public static void ClearAllSettingsAndCaches()
    {
        try
        {
            if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
            if (File.Exists(LibraryCachePath)) File.Delete(LibraryCachePath);
            ClearRadioCache();
            if (Directory.Exists(ArtworkFolder)) Directory.Delete(ArtworkFolder, true);
        }
        catch { }
    }

    private static void MigrateLegacyStorage()
    {
        try
        {
            if (Directory.Exists(RootFolder)) return;
            var source = LegacyRoots.FirstOrDefault(Directory.Exists);
            if (source is null) return;

            Directory.Move(source, RootFolder);
            var newLog = Path.Combine(RootFolder, "chistara.log");
            foreach (var oldName in new[] { "chistara-music.log", "auralis.log" })
            {
                var oldLog = Path.Combine(RootFolder, oldName);
                if (File.Exists(oldLog) && !File.Exists(newLog)) File.Move(oldLog, newLog);
            }
        }
        catch
        {
            // A failed migration leaves the previous profile untouched and starts clean.
        }
    }

    private static string MigrateStoredPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        foreach (var legacyRoot in LegacyRoots)
        {
            var prefix = legacyRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return Path.Combine(RootFolder, path[prefix.Length..]);
        }
        return path;
    }

    private static void NormalizeTrackDates(TrackItem track)
    {
        try
        {
            if (track.ModifiedUtc == default && File.Exists(track.Path))
                track.ModifiedUtc = File.GetLastWriteTimeUtc(track.Path);
            if (track.DateAddedUtc == default && File.Exists(track.Path))
                track.DateAddedUtc = File.GetCreationTimeUtc(track.Path);
        }
        catch
        {
            // Missing date metadata only places the item after dated tracks.
        }
        if (track.DateAddedUtc == default) track.DateAddedUtc = track.ModifiedUtc;
    }

    private static void Normalize(AppSettings settings)
    {
        settings.ThemeMode = settings.ThemeMode is "Dark" or "Light" or "System" ? settings.ThemeMode : "System";
        string[] validAccents = ["System", "Red", "Pink", "Matcha", "Purple", "Cyan", "Lime", "RGB gradient"];
        settings.Accent = validAccents.FirstOrDefault(accent => accent.Equals(settings.Accent, StringComparison.OrdinalIgnoreCase)) ?? "System";
        settings.MediaFolders ??= [];
        settings.MediaFolders = settings.MediaFolders
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        settings.RecentPlays ??= [];
        settings.RecentPlays = settings.RecentPlays.Take(50).ToList();
        settings.Volume = Math.Clamp(settings.Volume, 0, 1);
        settings.BassGain = Math.Clamp(settings.BassGain, -12, 12);
        settings.MidGain = Math.Clamp(settings.MidGain, -12, 12);
        settings.TrebleGain = Math.Clamp(settings.TrebleGain, -12, 12);
        settings.ReverbMix = Math.Clamp(settings.ReverbMix, 0, 0.6f);
        settings.CrossfadeSeconds = Math.Clamp(settings.CrossfadeSeconds, 0.5, 2);
        settings.RepeatMode = settings.RepeatMode is "All" or "One" ? settings.RepeatMode : "Off";
        settings.VisualizerStyle = settings.VisualizerStyle is "Waves" or "ModernWaves" or "Topographic" or "SimpleSpectrum" or "CdDays"
            ? settings.VisualizerStyle
            : "Waves";
        settings.MusicSortOrder = settings.MusicSortOrder is "Alphabetical" or "RecentlyPlayed" or "DateAdded" or "DateModified"
            ? settings.MusicSortOrder
            : "Alphabetical";
        settings.RadioSortOrder = settings.RadioSortOrder is "Alphabetical" or "RecentlyPlayed" or "DateAdded"
            ? settings.RadioSortOrder
            : "Alphabetical";
        settings.WindowWidth = Math.Clamp(settings.WindowWidth, 1024, 2560);
        settings.WindowHeight = Math.Clamp(settings.WindowHeight, 600, 1600);
    }
}
