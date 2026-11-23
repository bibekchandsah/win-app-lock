using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AppLock.WindowIsland.Modules.Lyrics;

/// <summary>
/// Lyrics for one track, already reduced to plain display lines (timestamps stripped).
/// </summary>
public sealed class LyricsResult
{
    public string TrackName { get; }
    public string ArtistName { get; }
    public IReadOnlyList<string> Lines { get; }

    public LyricsResult(string? plain, string? synced, string trackName, string artistName)
    {
        TrackName  = trackName;
        ArtistName = artistName;
        Lines      = BuildLines(plain, synced);
    }

    private static readonly Regex TimestampRegex =
        new(@"^\s*\[\d{1,2}:\d{2}(?:[.:]\d{1,3})?\]\s*", RegexOptions.Compiled);

    private static IReadOnlyList<string> BuildLines(string? plain, string? synced)
    {
        // Prefer the unsynced text; fall back to the synced sheet with its timestamps removed.
        string? text = !string.IsNullOrWhiteSpace(plain) ? plain : synced;
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();

        var lines = new List<string>();
        foreach (var raw in text.Replace("\r", string.Empty).Split('\n'))
        {
            var line = TimestampRegex.Replace(raw, string.Empty).Trim();
            if (line.Length > 0) lines.Add(line);
        }
        return lines;
    }
}

/// <summary>
/// Looks lyrics up on LRCLIB (https://lrclib.net), the free community database with no API key.
/// One lookup per track, cached for the session; every failure is swallowed so the player simply
/// renders without a lyrics panel.
/// </summary>
public sealed class LrclibLyricsService
{
    private const string BaseUrl = "https://lrclib.net/api";

    private readonly HttpClient _http;
    private readonly Dictionary<string, LyricsResult?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public LrclibLyricsService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        // LRCLIB asks clients to identify themselves.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AppLock-WindowIsland/1.0 (+https://lrclib.net)");
    }

    public async Task<LyricsResult?> GetAsync(string? title, string? artist, double durationSeconds)
    {
        var track = CleanTrack(title);
        if (string.IsNullOrWhiteSpace(track)) return null;

        var cleanedArtist = CleanArtist(artist);
        string key = cleanedArtist + "|" + track;
        if (_cache.TryGetValue(key, out var cached)) return cached;

        LyricsResult? result = null;
        try
        {
            var pairs = CandidatePairs(title, track, cleanedArtist);

            // /get matches the duration closely, so a slightly different master 404s; retry without it
            // before falling back to the fuzzy search. Pairs are tried most specific first, and the
            // fuzzy search only runs when neither exact combination knows the track.
            foreach (var (candidateTrack, candidateArtist) in pairs)
            {
                result = await GetExactAsync(candidateTrack, candidateArtist, durationSeconds).ConfigureAwait(false)
                      ?? await GetExactAsync(candidateTrack, candidateArtist, 0).ConfigureAwait(false);
                if (result != null) break;
            }

            if (result == null)
            {
                foreach (var (candidateTrack, candidateArtist) in pairs)
                {
                    result = await SearchAsync(candidateTrack, candidateArtist).ConfigureAwait(false);
                    if (result != null) break;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[Island] lyrics lookup failed: " + ex.Message);
        }

        _cache[key] = result;
        return result;
    }

    /// <summary>
    /// The (track, artist) pairs to try, most specific first. Video titles read
    /// "Artist - Track (Official Video)" while the media session's artist is the channel
    /// ("Good Vibes"), so the split taken from the title is often the only pair LRCLIB knows.
    /// </summary>
    private static List<(string Track, string Artist)> CandidatePairs(string? title, string track, string artist)
    {
        var pairs = new List<(string Track, string Artist)> { (track, artist) };
        string text = title ?? string.Empty;

        foreach (var separator in new[] { " - ", " \u2013 ", " \u2014 ", " -" })
        {
            int at = text.IndexOf(separator, StringComparison.Ordinal);
            if (at <= 0) continue;

            string left  = CleanArtist(text[..at]);
            string right = CleanTrack(text[(at + separator.Length)..]);
            if (left.Length == 0 || right.Length == 0) continue;

            var pair = (right, left);
            if (!pairs.Contains(pair)) pairs.Add(pair);
            break;
        }

        return pairs;
    }

    private async Task<LyricsResult?> GetExactAsync(string track, string artist, double durationSeconds)
    {
        string url = $"{BaseUrl}/get?artist_name={Uri.EscapeDataString(artist)}" +
                     $"&track_name={Uri.EscapeDataString(track)}";
        if (durationSeconds > 0)
            url += $"&duration={(int)Math.Round(durationSeconds)}";

        return await ReadRecordAsync(url, track, artist).ConfigureAwait(false);
    }

    private async Task<LyricsResult?> SearchAsync(string track, string artist)
    {
        string query = string.IsNullOrWhiteSpace(artist) ? track : $"{track} {artist}";
        string url = $"{BaseUrl}/search?q={Uri.EscapeDataString(query)}";
        return await ReadRecordAsync(url, track, artist).ConfigureAwait(false);
    }

    private async Task<LyricsResult?> ReadRecordAsync(string url, string track, string artist)
    {
        using var response = await _http.GetAsync(url).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);

        // /search returns an array; /get returns a single object.
        JsonElement record = doc.RootElement;
        if (record.ValueKind == JsonValueKind.Array)
        {
            // Search results are not ranked usefully (covers and tributes come first), so score them.
            JsonElement? best = null;
            int bestScore = int.MinValue;
            foreach (var candidate in record.EnumerateArray())
            {
                if (candidate.TryGetProperty("instrumental", out var instrumental) && instrumental.GetBoolean())
                    continue;
                if (!HasLyrics(candidate)) continue;

                int score = Score(candidate, track, artist);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }
            if (best is null) return null;
            record = best.Value;
        }

        if (record.ValueKind != JsonValueKind.Object) return null;
        if (record.TryGetProperty("instrumental", out var isInstrumental) && isInstrumental.GetBoolean())
            return null;

        string? plain  = record.TryGetProperty("plainLyrics",  out var p) ? p.GetString() : null;
        string? synced = record.TryGetProperty("syncedLyrics", out var s) ? s.GetString() : null;
        if (string.IsNullOrWhiteSpace(plain) && string.IsNullOrWhiteSpace(synced)) return null;

        return new LyricsResult(plain, synced, track, artist);
    }

    /// <summary>Prefers an exact artist match, then a matching title, over look-alike covers.</summary>
    private static int Score(JsonElement record, string track, string artist)
    {
        string candidateTrack  = record.TryGetProperty("trackName",  out var t) ? (t.GetString()  ?? string.Empty) : string.Empty;
        string candidateArtist = record.TryGetProperty("artistName", out var a) ? (a.GetString() ?? string.Empty) : string.Empty;

        int score = 0;
        if (!string.IsNullOrWhiteSpace(artist))
        {
            if (candidateArtist.Equals(artist, StringComparison.OrdinalIgnoreCase)) score += 4;
            else if (candidateArtist.Contains(artist, StringComparison.OrdinalIgnoreCase)) score += 2;
        }
        if (candidateTrack.Equals(track, StringComparison.OrdinalIgnoreCase)) score += 4;
        else if (candidateTrack.Contains(track, StringComparison.OrdinalIgnoreCase)) score += 1;
        return score;
    }

    private static bool HasLyrics(JsonElement record) =>
        (record.TryGetProperty("plainLyrics",  out var p) && !string.IsNullOrWhiteSpace(p.GetString())) ||
        (record.TryGetProperty("syncedLyrics", out var s) && !string.IsNullOrWhiteSpace(s.GetString()));

    /// <summary>Drops the "(Official Video)" style decorations LRCLIB does not index.</summary>
    private static string CleanTrack(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;

        string cleaned = Regex.Replace(title, @"[\(\[\{][^\)\]\}]*[\)\]\}]", " ");
        cleaned = Regex.Replace(cleaned, @"\b(official|lyrics?|lyric video|audio|visuali[sz]er|hd|hq|mv|m/v)\b",
                                " ", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ").Trim(' ', '-', '–', '—', '|');
        return cleaned.Length > 0 ? cleaned : title.Trim();
    }

    private static string CleanArtist(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return string.Empty;
        string cleaned = Regex.Replace(artist, @"\s*-\s*Topic$", string.Empty, RegexOptions.IgnoreCase);
        return cleaned.Trim();
    }
}
