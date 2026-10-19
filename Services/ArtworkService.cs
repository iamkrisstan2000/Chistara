using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chistara.Models;

namespace Chistara.Services;

public sealed class ArtworkService : IDisposable
{
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _musicBrainzGate = new(1, 1);
    private DateTimeOffset _lastMusicBrainzRequest = DateTimeOffset.MinValue;

    public ArtworkService()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 8
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(22) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Chistara/1.0.0 (Windows desktop music player; https://arena.ai)");
    }

    public static string? FindCachedArtwork(string artist, string album)
    {
        try
        {
            var path = CachePath(artist, album);
            return File.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    public static string? SaveEmbeddedArtwork(string artist, string album, byte[] data)
    {
        if (data.Length < 128 || string.IsNullOrWhiteSpace(album)) return null;
        try
        {
            Directory.CreateDirectory(AppStorage.ArtworkFolder);
            var path = CachePath(artist, album);
            if (!File.Exists(path))
            {
                var temporary = path + ".tmp";
                File.WriteAllBytes(temporary, data);
                File.Move(temporary, path, true);
            }
            var marker = MissingMarkerPath(artist, album);
            if (File.Exists(marker)) File.Delete(marker);
            return path;
        }
        catch { return null; }
    }

    public async Task PopulateMissingArtworkAsync(
        IReadOnlyCollection<TrackItem> tracks,
        bool liteMode,
        IProgress<ArtworkUpdate>? progress,
        CancellationToken token)
    {
        var groups = tracks
            .Where(track => !string.IsNullOrWhiteSpace(track.DisplayAlbum) &&
                            !track.DisplayAlbum.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase) &&
                            !track.DisplayArtist.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase))
            .GroupBy(track => $"{track.DisplayArtist}\u001f{track.DisplayAlbum}", StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Artist = group.First().DisplayArtist,
                Album = group.First().DisplayAlbum,
                Tracks = group.ToList()
            })
            .OrderByDescending(group => group.Tracks.Count)
            .ThenBy(group => group.Artist, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(group => group.Album, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var onlineLookups = 0;
        foreach (var group in groups)
        {
            token.ThrowIfCancellationRequested();
            var existing = group.Tracks.Select(track => track.ArtworkPath)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
            existing ??= FindCachedArtwork(group.Artist, group.Album);
            if (!string.IsNullOrWhiteSpace(existing))
            {
                progress?.Report(new ArtworkUpdate { Artist = group.Artist, Album = group.Album, ArtworkPath = existing });
                continue;
            }

            if (HasFreshMissingMarker(group.Artist, group.Album)) continue;
            if (liteMode && onlineLookups >= 30) break;
            onlineLookups++;
            var lookup = await DownloadFromCoverArtArchiveAsync(group.Artist, group.Album, token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(lookup.Path))
                progress?.Report(new ArtworkUpdate { Artist = group.Artist, Album = group.Album, ArtworkPath = lookup.Path });
            else if (lookup.Definitive)
                WriteMissingMarker(group.Artist, group.Album);
        }
    }

    private async Task<(string? Path, bool Definitive)> DownloadFromCoverArtArchiveAsync(string artist, string album, CancellationToken token)
    {
        try
        {
            await _musicBrainzGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var wait = TimeSpan.FromMilliseconds(1100) - (DateTimeOffset.UtcNow - _lastMusicBrainzRequest);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, token).ConfigureAwait(false);

                var query = $"release:\"{EscapeLucene(album)}\" AND artist:\"{EscapeLucene(artist)}\"";
                var url = "https://musicbrainz.org/ws/2/release/?fmt=json&limit=5&query=" + Uri.EscapeDataString(query);
                _lastMusicBrainzRequest = DateTimeOffset.UtcNow;
                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return (null, false);
                await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
                if (!json.RootElement.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array) return (null, false);

                var candidates = releases.EnumerateArray()
                    .Where(release => release.TryGetProperty("id", out _))
                    .Where(release => release.TryGetProperty("score", out var score) && score.TryGetInt32(out var value) && value >= 80)
                    .OrderByDescending(release => release.TryGetProperty("score", out var score) && score.TryGetInt32(out var value) ? value : 0)
                    .Take(4)
                    .Select(release => release.GetProperty("id").GetString())
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToList();
                if (candidates.Count == 0) return (null, true);

                var transientFailure = false;
                foreach (var id in candidates)
                {
                    token.ThrowIfCancellationRequested();
                    var artworkUrl = $"https://coverartarchive.org/release/{id}/front-250";
                    using var imageResponse = await _http.GetAsync(artworkUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                    if (!imageResponse.IsSuccessStatusCode)
                    {
                        if (imageResponse.StatusCode == HttpStatusCode.TooManyRequests || (int)imageResponse.StatusCode >= 500) transientFailure = true;
                        continue;
                    }
                    var mediaType = imageResponse.Content.Headers.ContentType?.MediaType ?? string.Empty;
                    if (!mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    {
                        transientFailure = true;
                        continue;
                    }
                    var declaredLength = imageResponse.Content.Headers.ContentLength;
                    if (declaredLength is > 8_000_000) continue;
                    var data = await imageResponse.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                    if (data.Length is < 128 or > 8_000_000) continue;
                    var saved = SaveEmbeddedArtwork(artist, album, data);
                    return (saved, !string.IsNullOrWhiteSpace(saved));
                }
                return (null, !transientFailure);
            }
            finally
            {
                _musicBrainzGate.Release();
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { return (null, false); }
    }

    private static string CachePath(string artist, string album) =>
        Path.Combine(AppStorage.ArtworkFolder, CacheKey(artist, album) + ".art");

    private static string MissingMarkerPath(string artist, string album) =>
        Path.Combine(AppStorage.ArtworkFolder, CacheKey(artist, album) + ".missing");

    private static string CacheKey(string artist, string album)
    {
        var normalized = $"{artist.Trim().ToUpperInvariant()}\n{album.Trim().ToUpperInvariant()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    private static string EscapeLucene(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static bool HasFreshMissingMarker(string artist, string album)
    {
        try
        {
            var path = MissingMarkerPath(artist, album);
            return File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromDays(30);
        }
        catch { return false; }
    }

    private static void WriteMissingMarker(string artist, string album)
    {
        try
        {
            Directory.CreateDirectory(AppStorage.ArtworkFolder);
            File.WriteAllText(MissingMarkerPath(artist, album), DateTimeOffset.UtcNow.ToString("O"));
        }
        catch { }
    }

    public void Dispose()
    {
        _http.Dispose();
        _musicBrainzGate.Dispose();
    }
}
