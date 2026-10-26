using System.Diagnostics;
using Chistara.Models;

namespace Chistara.Services;

public static class LibraryService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".wma", ".aac", ".m4a", ".flac", ".ogg", ".aiff", ".aif"
    };

    public static IReadOnlyList<DriveItem> GetDevices()
    {
        var devices = new List<DriveItem>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Network)) continue;
                var ready = drive.IsReady;
                var label = ready && !string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.VolumeLabel : drive.Name.TrimEnd('\\');
                devices.Add(new DriveItem
                {
                    RootPath = drive.RootDirectory.FullName,
                    Name = label,
                    Type = drive.DriveType switch
                    {
                        DriveType.Removable => "Removable drive",
                        DriveType.Network => "Network drive",
                        _ => "Local drive"
                    },
                    TotalBytes = ready ? drive.TotalSize : 0,
                    FreeBytes = ready ? drive.AvailableFreeSpace : 0,
                    IsReady = ready
                });
            }
            catch
            {
                devices.Add(new DriveItem
                {
                    RootPath = drive.Name,
                    Name = drive.Name.TrimEnd('\\'),
                    Type = "Drive",
                    IsReady = false
                });
            }
        }
        return devices.OrderByDescending(d => d.Type == "Removable drive")
            .ThenByDescending(d => d.Type == "Network drive")
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static Task<List<TrackItem>> ScanAsync(
        IEnumerable<string> folders,
        IReadOnlyCollection<TrackItem>? cachedTracks,
        bool liteMode,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var roots = folders
                .Where(Directory.Exists)
                .Select(NormalizePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var cache = (cachedTracks ?? [])
                .Where(t => !string.IsNullOrWhiteSpace(t.Path))
                .GroupBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var result = new List<TrackItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var filesSeen = 0;
            var maxTracks = liteMode ? 2500 : 50000;
            var discoveredAtUtc = DateTime.UtcNow;
            var lastProgressTimestamp = Stopwatch.GetTimestamp();

            foreach (var root in roots)
            {
                foreach (var path in EnumerateAudioFiles(root, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    filesSeen++;
                    if (!seen.Add(path)) continue;

                    TrackItem track;
                    var dateAdded = discoveredAtUtc;
                    try
                    {
                        var modified = System.IO.File.GetLastWriteTimeUtc(path);
                        cache.TryGetValue(path, out var cached);
                        if (cached?.DateAddedUtc is { } cachedAdded && cachedAdded != default)
                            dateAdded = cachedAdded;
                        if (cached is not null && Math.Abs((cached.ModifiedUtc - modified).TotalSeconds) < 1)
                        {
                            cached.DateAddedUtc = dateAdded;
                            track = cached;
                        }
                        else
                        {
                            track = ReadTrack(path, modified, dateAdded);
                        }
                    }
                    catch
                    {
                        track = CreateFallbackTrack(path, DateTime.MinValue, dateAdded);
                    }

                    result.Add(track);
                    if (result.Count % 25 == 0)
                    {
                        var now = Stopwatch.GetTimestamp();
                        if ((now - lastProgressTimestamp) / (double)Stopwatch.Frequency >= 0.12)
                        {
                            progress?.Report(new ScanProgress
                            {
                                FilesSeen = filesSeen,
                                TracksLoaded = result.Count,
                                CurrentFolder = root
                            });
                            lastProgressTimestamp = now;
                        }
                    }
                    if (result.Count >= maxTracks) break;
                }
                if (result.Count >= maxTracks) break;
            }

            progress?.Report(new ScanProgress
            {
                FilesSeen = filesSeen,
                TracksLoaded = result.Count,
                CurrentFolder = string.Empty
            });

            return result.OrderBy(t => t.DisplayArtist, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(t => t.DisplayAlbum, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(t => t.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }, cancellationToken);
    }

    public static Task<List<TrackItem>> ScanDriveAsync(
        string root,
        bool liteMode,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken) =>
        ScanAsync([root], null, liteMode, progress, cancellationToken);

    private static TrackItem ReadTrack(string path, DateTime modifiedUtc, DateTime dateAddedUtc)
    {
        try
        {
            using var media = TagLib.File.Create(path);
            var tag = media.Tag;
            var artist = FirstUseful(tag.FirstAlbumArtist, tag.FirstPerformer, tag.JoinedPerformers);
            var album = string.IsNullOrWhiteSpace(tag.Album) ? "Unknown album" : tag.Album.Trim();
            var artworkPath = ArtworkService.FindCachedArtwork(artist, album) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(artworkPath))
            {
                try
                {
                    var picture = tag.Pictures.FirstOrDefault();
                    if (picture?.Data?.Data is { Length: > 127 } imageData)
                        artworkPath = ArtworkService.SaveEmbeddedArtwork(artist, album, imageData) ?? string.Empty;
                }
                catch { }
            }

            return new TrackItem
            {
                Path = path,
                Title = string.IsNullOrWhiteSpace(tag.Title) ? System.IO.Path.GetFileNameWithoutExtension(path) : tag.Title.Trim(),
                Artist = artist,
                Album = album,
                Genre = tag.Genres.FirstOrDefault()?.Trim() ?? string.Empty,
                Duration = media.Properties.Duration,
                DateAddedUtc = dateAddedUtc,
                ModifiedUtc = modifiedUtc,
                Source = "Library",
                ArtworkPath = artworkPath
            };
        }
        catch
        {
            return CreateFallbackTrack(path, modifiedUtc, dateAddedUtc);
        }
    }

    private static string FirstUseful(params string?[] candidates) =>
        candidates.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "Unknown artist";

    private static TrackItem CreateFallbackTrack(string path, DateTime modifiedUtc, DateTime dateAddedUtc)
    {
        var album = new DirectoryInfo(System.IO.Path.GetDirectoryName(path) ?? string.Empty).Name is { Length: > 0 } folder ? folder : "Unknown album";
        return new TrackItem
        {
            Path = path,
            Title = System.IO.Path.GetFileNameWithoutExtension(path),
            Artist = "Unknown artist",
            Album = album,
            ArtworkPath = ArtworkService.FindCachedArtwork("Unknown artist", album) ?? string.Empty,
            DateAddedUtc = dateAddedUtc,
            ModifiedUtc = modifiedUtc,
            Source = "Library"
        };
    }

    private static IEnumerable<string> EnumerateAudioFiles(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = pending.Pop();
            if (!visited.Add(folder)) continue;

            List<string> files;
            try { files = Directory.EnumerateFiles(folder).ToList(); }
            catch { continue; }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (SupportedExtensions.Contains(System.IO.Path.GetExtension(file))) yield return file;
            }

            List<string> directories;
            try { directories = Directory.EnumerateDirectories(folder).ToList(); }
            catch { continue; }

            foreach (var directory in directories)
            {
                try
                {
                    var attributes = System.IO.File.GetAttributes(directory);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.System) != 0) continue;
                    pending.Push(directory);
                }
                catch { }
            }
        }
    }

    private static string NormalizePath(string path)
    {
        try { return System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar); }
        catch { return path.Trim(); }
    }
}
