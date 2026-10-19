using System.Windows.Media.Imaging;

namespace Chistara.Services;

/// <summary>
/// Small UI-image cache shared by recycled lists and now-playing surfaces. Artwork
/// is decoded near its rendered size, frozen when possible, and bounded so a large
/// library cannot grow the working set indefinitely.
/// </summary>
public static class ArtworkImageCache
{
    private const int MaximumEntries = 192;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, CacheEntry> Entries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> Recency = new();

    public static BitmapSource? Load(string? source, int decodePixelWidth = 192)
    {
        source = NormalizeSource(source);
        if (source is null) return null;
        decodePixelWidth = Math.Clamp(decodePixelWidth, 32, 512);
        var key = $"{decodePixelWidth}|{source}";

        lock (Gate)
        {
            if (Entries.TryGetValue(key, out var cached))
            {
                Recency.Remove(cached.Node);
                Recency.AddFirst(cached.Node);
                return cached.Image;
            }
        }

        BitmapImage image;
        try
        {
            var remote = Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
            if (!remote && !File.Exists(source)) return null;

            image = new BitmapImage();
            image.BeginInit();
            image.DecodePixelWidth = decodePixelWidth;
            image.CacheOption = remote ? BitmapCacheOption.OnDemand : BitmapCacheOption.OnLoad;
            if (remote)
                image.UriCachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.CacheIfAvailable);
            image.UriSource = remote ? uri! : new Uri(source, UriKind.Absolute);
            image.EndInit();
            if (image.CanFreeze) image.Freeze();
        }
        catch
        {
            return null;
        }

        lock (Gate)
        {
            if (Entries.TryGetValue(key, out var raced))
            {
                Recency.Remove(raced.Node);
                Recency.AddFirst(raced.Node);
                return raced.Image;
            }

            var node = Recency.AddFirst(key);
            Entries[key] = new CacheEntry(image, node);
            while (Entries.Count > MaximumEntries && Recency.Last is { } oldest)
            {
                Recency.RemoveLast();
                Entries.Remove(oldest.Value);
            }
        }
        return image;
    }

    public static void Clear()
    {
        lock (Gate)
        {
            Entries.Clear();
            Recency.Clear();
        }
    }

    private static string? NormalizeSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        source = source.Trim();
        if (source.StartsWith("//", StringComparison.Ordinal)) return "https:" + source;
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            return uri.AbsoluteUri;
        try { return Path.GetFullPath(source); }
        catch { return source; }
    }

    private sealed record CacheEntry(BitmapSource Image, LinkedListNode<string> Node);
}
