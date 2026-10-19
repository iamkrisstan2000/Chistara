using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chistara.Models;

namespace Chistara.Services;

public sealed class RadioService : IDisposable
{
    private const int WorldwidePageSize = 2000;
    private const int NearbyRadiusMeters = 500_000;
    private static readonly char[] PlaylistLineSeparators = ['\r', '\n'];
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };
    private ApproximateLocation? _lastLocation;
    private List<RadioStation> _homeLocalPool = [];
    private List<RadioStation> _homeWorldwidePool = [];
    private DateTimeOffset _homeLocalPoolAt = DateTimeOffset.MinValue;
    private DateTimeOffset _homeWorldwidePoolAt = DateTimeOffset.MinValue;

    public RadioService()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://all.api.radio-browser.info/"),
            Timeout = TimeSpan.FromSeconds(35)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Chistara/1.0.0 (Windows 11 music and radio player)");
    }

    public string LocalCountryCode => !string.IsNullOrWhiteSpace(_lastLocation?.CountryCode)
        ? _lastLocation.CountryCode.ToUpperInvariant()
        : GetSystemCountryCode();

    public string LocalCountryName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_lastLocation?.Country)) return _lastLocation.Country;
            try { return new RegionInfo(LocalCountryCode).EnglishName; }
            catch { return LocalCountryCode; }
        }
    }

    public string LocalLocationName => !string.IsNullOrWhiteSpace(_lastLocation?.DisplayName)
        ? _lastLocation.DisplayName
        : LocalCountryName;

    public async Task<List<RadioStation>> ScanLocalStationsAsync(
        string quality,
        bool forceRefresh,
        IProgress<RadioScanProgress>? progress,
        CancellationToken token)
    {
        progress?.Report(new RadioScanProgress { Message = "Locating nearby free stations…" });
        var location = await ResolveApproximateLocationAsync(forceRefresh, token).ConfigureAwait(false);
        _lastLocation = location;
        var countryCode = !string.IsNullOrWhiteSpace(location.CountryCode) ? location.CountryCode.ToUpperInvariant() : GetSystemCountryCode();
        var cacheKey = $"local-{countryCode}";

        if (!forceRefresh)
        {
            var cached = await LoadCacheAsync(cacheKey, allowExpired: false, token).ConfigureAwait(false);
            if (cached.Count > 0)
            {
                ApplyDistances(cached, location);
                progress?.Report(new RadioScanProgress { StationsFound = cached.Count, PagesScanned = 1, Message = $"{cached.Count:N0} free local streams ready" });
                return SortLocal(cached, quality, location);
            }
        }

        progress?.Report(new RadioScanProgress { Message = $"Scanning every free stream in {LocalCountryName}…" });
        var countryEndpoint = "json/stations/search?" + BuildQuery(new Dictionary<string, string>
        {
            ["countrycode"] = countryCode,
            ["hidebroken"] = "true",
            ["order"] = "clickcount",
            ["reverse"] = "true",
            ["limit"] = "100000"
        });

        var countryTask = FetchStationPageAsync(countryEndpoint, token);
        Task<List<RadioStation>>? nearbyTask = null;
        if (location.HasCoordinates)
        {
            var nearbyEndpoint = "json/stations/search?" + BuildQuery(new Dictionary<string, string>
            {
                ["geo_lat"] = location.Latitude.ToString("0.######", CultureInfo.InvariantCulture),
                ["geo_long"] = location.Longitude.ToString("0.######", CultureInfo.InvariantCulture),
                ["geo_distance"] = NearbyRadiusMeters.ToString(CultureInfo.InvariantCulture),
                ["hidebroken"] = "true",
                ["order"] = "clickcount",
                ["reverse"] = "true",
                ["limit"] = "100000"
            });
            nearbyTask = FetchStationPageAsync(nearbyEndpoint, token);
        }

        List<RadioStation> stations;
        var pageCount = 1;
        try
        {
            var pages = new List<List<RadioStation>> { await countryTask.ConfigureAwait(false) };
            if (nearbyTask is not null)
            {
                try
                {
                    pages.Add(await nearbyTask.ConfigureAwait(false));
                    pageCount++;
                }
                catch when (!token.IsCancellationRequested)
                {
                    // The complete country list still provides a useful local catalog when a mirror lacks geo search.
                }
            }
            stations = NormalizeStations(pages.SelectMany(page => page));
            if (stations.Count > 0) await SaveCacheAsync(cacheKey, stations, token).ConfigureAwait(false);
        }
        catch when (!token.IsCancellationRequested)
        {
            var stale = await LoadCacheAsync(cacheKey, allowExpired: true, token).ConfigureAwait(false);
            if (stale.Count == 0) throw;
            stations = stale;
        }

        ApplyDistances(stations, location);
        progress?.Report(new RadioScanProgress { StationsFound = stations.Count, PagesScanned = pageCount, Message = $"{stations.Count:N0} free local streams found" });
        return SortLocal(stations, quality, location);
    }

    public async Task<List<RadioStation>> ScanWorldwideStationsAsync(
        string quality,
        bool forceRefresh,
        int? maximumStations,
        IProgress<RadioScanProgress>? progress,
        CancellationToken token)
    {
        if (!forceRefresh)
        {
            var cached = await LoadCacheAsync("worldwide-all", allowExpired: false, token).ConfigureAwait(false);
            if (cached.Count > 0)
            {
                var cachedResult = maximumStations is > 0 ? cached.Take(maximumStations.Value).ToList() : cached;
                progress?.Report(new RadioScanProgress { StationsFound = cachedResult.Count, PagesScanned = 1, Message = $"{cachedResult.Count:N0} free worldwide streams ready" });
                return SortWorldwide(cachedResult, quality);
            }
        }

        var all = new List<RadioStation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var offset = 0;
        var pageNumber = 0;
        var completedCatalog = false;
        progress?.Report(new RadioScanProgress { Message = "Scanning the worldwide free radio directory…" });

        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var requested = maximumStations is > 0
                    ? Math.Min(WorldwidePageSize, maximumStations.Value - all.Count)
                    : WorldwidePageSize;
                if (requested <= 0) break;

                var endpoint = "json/stations/search?" + BuildQuery(new Dictionary<string, string>
                {
                    ["hidebroken"] = "true",
                    ["order"] = "clickcount",
                    ["reverse"] = "true",
                    ["offset"] = offset.ToString(CultureInfo.InvariantCulture),
                    ["limit"] = requested.ToString(CultureInfo.InvariantCulture)
                });
                var rawPage = await FetchStationPageAsync(endpoint, token).ConfigureAwait(false);
                pageNumber++;
                var added = new List<RadioStation>();
                foreach (var station in rawPage.Where(IsUsable))
                {
                    var key = StationKey(station);
                    if (!seen.Add(key)) continue;
                    all.Add(station);
                    added.Add(station);
                }
                progress?.Report(new RadioScanProgress
                {
                    StationsFound = all.Count,
                    PagesScanned = pageNumber,
                    Message = $"Scanning worldwide… {all.Count:N0} free streams found",
                    NewStations = added
                });

                if (rawPage.Count < requested)
                {
                    completedCatalog = true;
                    break;
                }
                if (pageNumber > 1 && added.Count == 0)
                    throw new InvalidDataException("The radio directory did not advance to the next page.");
                offset += requested;
                if (maximumStations is > 0 && all.Count >= maximumStations.Value) break;
            }

            if (all.Count > 0 && completedCatalog)
                await SaveCacheAsync("worldwide-all", all, token).ConfigureAwait(false);
        }
        catch when (!token.IsCancellationRequested)
        {
            if (all.Count == 0)
            {
                var stale = await LoadCacheAsync("worldwide-all", allowExpired: true, token).ConfigureAwait(false);
                if (stale.Count == 0) throw;
                all = maximumStations is > 0 ? stale.Take(maximumStations.Value).ToList() : stale;
            }
        }

        progress?.Report(new RadioScanProgress { StationsFound = all.Count, PagesScanned = pageNumber, Message = $"{all.Count:N0} free worldwide streams found" });
        return SortWorldwide(NormalizeStations(all), quality);
    }

    public async Task<List<RadioStation>> SearchStationsAsync(string query, string quality, CancellationToken token)
    {
        query = query.Trim();
        if (query.Length < 2) return [];
        var common = new Dictionary<string, string>
        {
            ["hidebroken"] = "true",
            ["order"] = "clickcount",
            ["reverse"] = "true",
            ["limit"] = "100"
        };
        var endpoints = new List<string>();
        foreach (var field in new[] { "name", "tag", "country", "language" })
        {
            var parameters = new Dictionary<string, string>(common) { [field] = query };
            endpoints.Add("json/stations/search?" + BuildQuery(parameters));
        }

        try
        {
            var pages = await Task.WhenAll(endpoints.Select(endpoint => FetchStationPageAsync(endpoint, token))).ConfigureAwait(false);
            return SortWorldwide(NormalizeStations(pages.SelectMany(page => page)), quality).Take(180).ToList();
        }
        catch when (!token.IsCancellationRequested)
        {
            return [];
        }
    }

    public async Task<List<RadioStation>> GetHomeRecommendationsAsync(bool local, string quality, int count, CancellationToken token)
    {
        count = Math.Clamp(count, 1, 6);
        if (local)
        {
            var location = await ResolveApproximateLocationAsync(forceRefresh: false, token).ConfigureAwait(false);
            _lastLocation = location;
            if (_homeLocalPool.Count == 0 || DateTimeOffset.UtcNow - _homeLocalPoolAt > TimeSpan.FromMinutes(30))
            {
                var countryCode = !string.IsNullOrWhiteSpace(location.CountryCode) ? location.CountryCode.ToUpperInvariant() : GetSystemCountryCode();
                var endpoints = new List<string>
                {
                    "json/stations/search?" + BuildQuery(new Dictionary<string, string>
                    {
                        ["countrycode"] = countryCode,
                        ["hidebroken"] = "true",
                        ["order"] = "clickcount",
                        ["reverse"] = "true",
                        ["limit"] = "80"
                    })
                };
                if (location.HasCoordinates)
                {
                    endpoints.Add("json/stations/search?" + BuildQuery(new Dictionary<string, string>
                    {
                        ["geo_lat"] = location.Latitude.ToString("0.######", CultureInfo.InvariantCulture),
                        ["geo_long"] = location.Longitude.ToString("0.######", CultureInfo.InvariantCulture),
                        ["geo_distance"] = NearbyRadiusMeters.ToString(CultureInfo.InvariantCulture),
                        ["hidebroken"] = "true",
                        ["order"] = "clickcount",
                        ["reverse"] = "true",
                        ["limit"] = "80"
                    }));
                }

                var pages = await Task.WhenAll(endpoints.Select(endpoint => FetchStationPageSafeAsync(endpoint, token))).ConfigureAwait(false);
                var pool = NormalizeStations(pages.SelectMany(page => page));
                if (pool.Count == 0)
                    pool = await LoadCacheAsync($"local-{countryCode}", allowExpired: true, token).ConfigureAwait(false);
                ApplyDistances(pool, location);
                _homeLocalPool = SortLocal(pool, quality, location).Take(80).ToList();
                _homeLocalPoolAt = DateTimeOffset.UtcNow;
            }
            return PickRecommendations(_homeLocalPool, count);
        }

        if (_homeWorldwidePool.Count == 0 || DateTimeOffset.UtcNow - _homeWorldwidePoolAt > TimeSpan.FromMinutes(30))
        {
            var cached = await LoadCacheAsync("worldwide-all", allowExpired: false, token).ConfigureAwait(false);
            if (cached.Count == 0)
            {
                var endpoint = "json/stations/search?" + BuildQuery(new Dictionary<string, string>
                {
                    ["hidebroken"] = "true",
                    ["order"] = "clickcount",
                    ["reverse"] = "true",
                    ["limit"] = "160"
                });
                cached = await FetchStationPageSafeAsync(endpoint, token).ConfigureAwait(false);
            }
            _homeWorldwidePool = SortWorldwide(NormalizeStations(cached), quality).Take(120).ToList();
            _homeWorldwidePoolAt = DateTimeOffset.UtcNow;
        }
        return PickRecommendations(_homeWorldwidePool, count);
    }

    public void InvalidateHomeRecommendations()
    {
        _homeLocalPool = [];
        _homeWorldwidePool = [];
        _homeLocalPoolAt = DateTimeOffset.MinValue;
        _homeWorldwidePoolAt = DateTimeOffset.MinValue;
    }

    public async Task<string> ResolveStreamUrlAsync(RadioStation station, CancellationToken token)
    {
        var url = station.StreamUrl?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return url;
        var path = uri.AbsolutePath.ToLowerInvariant();
        var isHls = path.EndsWith(".m3u8", StringComparison.Ordinal);
        var isPlaylist = isHls || path.EndsWith(".m3u", StringComparison.Ordinal) || path.EndsWith(".pls", StringComparison.Ordinal);
        if (!isPlaylist) return uri.AbsoluteUri;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("Icy-MetaData", "0");
            request.Headers.TryAddWithoutValidation("Accept", "audio/*, application/vnd.apple.mpegurl, audio/x-mpegurl, audio/x-scpls, */*;q=0.5");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var finalUri = response.RequestMessage?.RequestUri ?? uri;
            if (isHls) return finalUri.AbsoluteUri;

            var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            foreach (var rawLine in text.Split(PlaylistLineSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                if (line.StartsWith('#') || line.StartsWith(';') || line.StartsWith("[playlist]", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.StartsWith("File", StringComparison.OrdinalIgnoreCase) && line.Contains('='))
                    line = line[(line.IndexOf('=') + 1)..].Trim();
                if (!Uri.TryCreate(line, UriKind.Absolute, out var stream))
                    Uri.TryCreate(finalUri, line, out stream);
                if (stream is not null && stream.Scheme is "http" or "https") return stream.AbsoluteUri;
            }
            return finalUri.AbsoluteUri;
        }
        catch (OperationCanceledException) { throw; }
        catch { return uri.AbsoluteUri; }
    }

    public async Task RegisterClickAsync(RadioStation station)
    {
        if (string.IsNullOrWhiteSpace(station.Id)) return;
        try { using var response = await _http.GetAsync($"json/url/{Uri.EscapeDataString(station.Id)}").ConfigureAwait(false); }
        catch { }
    }

    private async Task<ApproximateLocation> ResolveApproximateLocationAsync(bool forceRefresh, CancellationToken token)
    {
        var path = Path.Combine(AppStorage.RadioCacheFolder, "approximate-location.json");
        if (!forceRefresh)
        {
            try
            {
                if (File.Exists(path))
                {
                    var cached = JsonSerializer.Deserialize<ApproximateLocation>(await File.ReadAllTextAsync(path, token).ConfigureAwait(false), _json);
                    if (cached is not null && DateTimeOffset.UtcNow - cached.ResolvedAt < TimeSpan.FromHours(24)) return cached;
                }
            }
            catch { }
        }

        try
        {
            using var response = await _http.GetAsync("https://ipwho.is/", HttpCompletionOption.ResponseContentRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var result = await JsonSerializer.DeserializeAsync<IpLocationResponse>(stream, _json, token).ConfigureAwait(false);
            if (result is { Success: true } && result.Latitude is >= -90 and <= 90 && result.Longitude is >= -180 and <= 180)
            {
                var location = new ApproximateLocation
                {
                    City = result.City ?? string.Empty,
                    Region = result.Region ?? string.Empty,
                    Country = result.Country ?? string.Empty,
                    CountryCode = result.CountryCode ?? GetSystemCountryCode(),
                    Latitude = result.Latitude,
                    Longitude = result.Longitude,
                    ResolvedAt = DateTimeOffset.UtcNow
                };
                try
                {
                    Directory.CreateDirectory(AppStorage.RadioCacheFolder);
                    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(location, _json), token).ConfigureAwait(false);
                }
                catch { }
                return location;
            }
        }
        catch when (!token.IsCancellationRequested) { }

        var countryCode = GetSystemCountryCode();
        string country;
        try { country = new RegionInfo(countryCode).EnglishName; }
        catch { country = countryCode; }
        return new ApproximateLocation { CountryCode = countryCode, Country = country, ResolvedAt = DateTimeOffset.UtcNow };
    }

    private async Task<List<RadioStation>> FetchStationPageSafeAsync(string endpoint, CancellationToken token)
    {
        try { return await FetchStationPageAsync(endpoint, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { return []; }
    }

    private static List<RadioStation> PickRecommendations(IEnumerable<RadioStation> stations, int count)
    {
        var pool = stations.Take(Math.Max(24, count * 12)).ToList();
        if (pool.Count <= count) return pool;
        var withArtwork = pool.Where(station => station.Favicon.StartsWith("//", StringComparison.Ordinal) ||
                                                (Uri.TryCreate(station.Favicon, UriKind.Absolute, out var art) && art.Scheme is "http" or "https"))
            .OrderBy(_ => Random.Shared.Next())
            .Take(count)
            .ToList();
        if (withArtwork.Count < count)
        {
            var used = withArtwork.Select(StationKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            withArtwork.AddRange(pool.Where(station => !used.Contains(StationKey(station))).OrderBy(_ => Random.Shared.Next()).Take(count - withArtwork.Count));
        }
        return withArtwork;
    }

    private async Task<List<RadioStation>> FetchStationPageAsync(string endpoint, CancellationToken token)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var response = await _http.GetAsync(endpoint, HttpCompletionOption.ResponseContentRead, token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                return await JsonSerializer.DeserializeAsync<List<RadioStation>>(stream, _json, token).ConfigureAwait(false) ?? [];
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                lastError = exception;
                await Task.Delay(TimeSpan.FromMilliseconds(400 * (attempt + 1)), token).ConfigureAwait(false);
            }
        }
        throw lastError ?? new HttpRequestException("The radio directory did not respond.");
    }

    private static List<RadioStation> NormalizeStations(IEnumerable<RadioStation> stations) => stations
        .Where(IsUsable)
        .GroupBy(StationKey, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.OrderByDescending(station => station.ClickCount).First())
        .ToList();

    private static bool IsUsable(RadioStation station) =>
        station.LastCheckOk != 0 &&
        !string.IsNullOrWhiteSpace(station.Name) &&
        Uri.TryCreate(station.StreamUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https";

    private static string StationKey(RadioStation station) =>
        !string.IsNullOrWhiteSpace(station.Id) ? station.Id : station.StreamUrl;

    private static List<RadioStation> SortWorldwide(IEnumerable<RadioStation> stations, string quality) => stations
        .OrderBy(station => QualityScore(station, quality))
        .ThenByDescending(station => station.ClickCount)
        .ThenBy(station => station.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    private static List<RadioStation> SortLocal(IEnumerable<RadioStation> stations, string quality, ApproximateLocation location) => stations
        .OrderBy(station => LocalBucket(station, location))
        .ThenBy(station => station.DistanceKm ?? double.MaxValue)
        .ThenBy(station => QualityScore(station, quality))
        .ThenByDescending(station => station.ClickCount)
        .ThenBy(station => station.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    private static int LocalBucket(RadioStation station, ApproximateLocation location)
    {
        if (station.DistanceKm is >= 0 and <= NearbyRadiusMeters / 1000d) return 0;
        if (!string.IsNullOrWhiteSpace(location.City) && station.State.Contains(location.City, StringComparison.CurrentCultureIgnoreCase)) return 1;
        if (!string.IsNullOrWhiteSpace(location.Region) && station.State.Contains(location.Region, StringComparison.CurrentCultureIgnoreCase)) return 1;
        return 2;
    }

    private static int QualityScore(RadioStation station, string quality) => quality switch
    {
        "Data saver" => station.Bitrate <= 0 ? 100 : Math.Abs(station.Bitrate - 64),
        "High" => station.Bitrate <= 0 ? 500 : -station.Bitrate,
        _ => station.Bitrate <= 0 ? 180 : Math.Abs(station.Bitrate - 128)
    };

    private static void ApplyDistances(IEnumerable<RadioStation> stations, ApproximateLocation location)
    {
        if (!location.HasCoordinates) return;
        foreach (var station in stations)
        {
            if (station.GeoLatitude is not (>= -90 and <= 90) || station.GeoLongitude is not (>= -180 and <= 180)) continue;
            station.DistanceKm = HaversineKm(location.Latitude, location.Longitude, station.GeoLatitude.Value, station.GeoLongitude.Value);
        }
    }

    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadius = 6371.0088;
        var dLat = DegreesToRadians(lat2 - lat1);
        var dLon = DegreesToRadians(lon2 - lon1);
        var first = DegreesToRadians(lat1);
        var second = DegreesToRadians(lat2);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(first) * Math.Cos(second) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        a = Math.Clamp(a, 0, 1);
        return earthRadius * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double DegreesToRadians(double value) => value * Math.PI / 180d;

    private static string BuildQuery(IEnumerable<KeyValuePair<string, string>> values) =>
        string.Join("&", values.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

    private async Task<List<RadioStation>> LoadCacheAsync(string key, bool allowExpired, CancellationToken token)
    {
        try
        {
            var path = CachePath(key);
            if (!File.Exists(path)) return [];
            await using var stream = File.OpenRead(path);
            var cache = await JsonSerializer.DeserializeAsync<StationCache>(stream, _json, token).ConfigureAwait(false);
            if (cache is null) return [];
            if (!allowExpired && DateTimeOffset.UtcNow - cache.SavedAt > TimeSpan.FromHours(12)) return [];
            return NormalizeStations(cache.Stations);
        }
        catch (OperationCanceledException) { throw; }
        catch { return []; }
    }

    private async Task SaveCacheAsync(string key, List<RadioStation> stations, CancellationToken token)
    {
        try
        {
            Directory.CreateDirectory(AppStorage.RadioCacheFolder);
            var path = CachePath(key);
            var temporary = path + ".tmp";
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, new StationCache { SavedAt = DateTimeOffset.UtcNow, Stations = stations }, _json, token).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        catch (OperationCanceledException) { throw; }
        catch { }
    }

    private static string CachePath(string key) => Path.Combine(AppStorage.RadioCacheFolder, $"{key}.json");

    private static string GetSystemCountryCode()
    {
        try
        {
            var code = RegionInfo.CurrentRegion.TwoLetterISORegionName;
            return string.IsNullOrWhiteSpace(code) || code.Equals("IV", StringComparison.OrdinalIgnoreCase) ? "PH" : code.ToUpperInvariant();
        }
        catch { return "PH"; }
    }

    public void Dispose() => _http.Dispose();

    private sealed class StationCache
    {
        public DateTimeOffset SavedAt { get; set; }
        public List<RadioStation> Stations { get; set; } = [];
    }

    private sealed class IpLocationResponse
    {
        [JsonPropertyName("success")] public bool Success { get; set; }
        [JsonPropertyName("city")] public string? City { get; set; }
        [JsonPropertyName("region")] public string? Region { get; set; }
        [JsonPropertyName("country")] public string? Country { get; set; }
        [JsonPropertyName("country_code")] public string? CountryCode { get; set; }
        [JsonPropertyName("latitude")] public double Latitude { get; set; }
        [JsonPropertyName("longitude")] public double Longitude { get; set; }
    }
}
