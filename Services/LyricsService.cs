using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using SpotifyLyricsOverlay.Wpf.Models;

namespace SpotifyLyricsOverlay.Wpf.Services;

public sealed class LyricsService
{
    private const int MaxCacheEntries = 500;
    private static readonly TimeSpan LyricsTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan MissingTtl = TimeSpan.FromHours(6);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly SettingsStore _store;

    public LyricsService(SettingsStore store)
    {
        _store = store;
    }

    public async Task<LyricsResult> GetLyricsAsync(TrackInfo track)
    {
        var cache = await _store.ReadAsync<Dictionary<string, CacheEntry>>("lyrics-cache.json") ?? new();
        if (cache.TryGetValue(track.Id, out var cached) && !cached.IsExpired())
        {
            return cached.Result;
        }

        var query = new Dictionary<string, string>
        {
            ["track_name"] = track.Name,
            ["artist_name"] = string.IsNullOrWhiteSpace(track.FirstArtist) ? track.Artist : track.FirstArtist,
            ["duration"] = Math.Round(track.DurationMs / 1000d).ToString("0")
        };
        if (!string.IsNullOrWhiteSpace(track.Album)) query["album_name"] = track.Album;

        using var response = await _http.GetAsync($"https://lrclib.net/api/get?{ToForm(query)}");
        if (!response.IsSuccessStatusCode)
        {
            var missing = new LyricsResult();
            if (response.StatusCode == HttpStatusCode.NotFound) await CacheAsync(cache, track.Id, missing, MissingTtl);
            return missing;
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var root = doc.RootElement;

        if (!IsLikelyMatch(track, root))
        {
            var missing = new LyricsResult();
            await CacheAsync(cache, track.Id, missing, MissingTtl);
            return missing;
        }

        var result = new LyricsResult();
        var synced = root.TryGetProperty("syncedLyrics", out var syncedEl) ? syncedEl.GetString() : null;
        var plain = root.TryGetProperty("plainLyrics", out var plainEl) ? plainEl.GetString() : null;

        if (!string.IsNullOrWhiteSpace(synced))
        {
            result.Source = "synced";
            result.Lines = ParseLrc(synced);
        }
        else if (!string.IsNullOrWhiteSpace(plain))
        {
            result.Source = "plain";
            result.Plain = plain.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        await CacheAsync(cache, track.Id, result, result.Source == "none" ? MissingTtl : LyricsTtl);
        return result;
    }

    private async Task CacheAsync(Dictionary<string, CacheEntry> cache, string trackId, LyricsResult result, TimeSpan ttl)
    {
        cache[trackId] = new CacheEntry
        {
            SavedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            TtlMs = (long)ttl.TotalMilliseconds,
            Result = result
        };

        foreach (var key in cache.OrderBy(pair => pair.Value.SavedAt).Take(Math.Max(0, cache.Count - MaxCacheEntries)).Select(pair => pair.Key).ToList())
        {
            cache.Remove(key);
        }

        await _store.WriteAsync("lyrics-cache.json", cache);
    }

    private static bool IsLikelyMatch(TrackInfo track, JsonElement data)
    {
        if (!data.TryGetProperty("duration", out var duration) || duration.ValueKind == JsonValueKind.Null) return true;
        var returned = duration.GetDouble();
        return Math.Abs(returned - track.DurationMs / 1000d) <= 5;
    }

    private static List<LyricLine> ParseLrc(string text)
    {
        var lines = new List<LyricLine>();
        foreach (var raw in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            var matches = Regex.Matches(raw, @"\[(\d{1,2}):(\d{2})(?:\.(\d{1,3}))?\]");
            if (matches.Count == 0) continue;
            var lyric = Regex.Replace(raw, @"\[[^\]]+\]", "").Trim();
            foreach (Match match in matches)
            {
                var minutes = int.Parse(match.Groups[1].Value);
                var seconds = int.Parse(match.Groups[2].Value);
                var fraction = match.Groups[3].Success ? match.Groups[3].Value.PadRight(3, '0') : "000";
                lines.Add(new LyricLine(minutes * 60000 + seconds * 1000 + int.Parse(fraction), lyric));
            }
        }

        return lines.OrderBy(line => line.Time).ToList();
    }

    private static string ToForm(Dictionary<string, string> values)
    {
        return string.Join('&', values.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    }

    public sealed class CacheEntry
    {
        public long SavedAt { get; set; }
        public long TtlMs { get; set; }
        public LyricsResult Result { get; set; } = new();

        public bool IsExpired()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - SavedAt >= TtlMs;
        }
    }
}
