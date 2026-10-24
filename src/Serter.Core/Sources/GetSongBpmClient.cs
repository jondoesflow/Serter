using System.Text.Json.Nodes;
using Serter.Core.Models;
using Serter.Core.Music;

namespace Serter.Core.Sources;

/// <summary>
/// GetSongBPM (https://getsongbpm.com) – free API key, 3000 requests/hour. Supplies BPM and key
/// for suggested tracks, and lists of songs at a given tempo.
/// </summary>
public sealed class GetSongBpmClient
{
    private const string Base = "https://api.getsong.co";
    private readonly HttpJson _json;
    private readonly string _apiKey;

    public GetSongBpmClient(HttpJson json, string apiKey)
    {
        _json = json;
        _apiKey = apiKey;
    }

    /// <summary>Looks up BPM, key and genre for a track. Returns null if GetSongBPM doesn't know it.</summary>
    public async Task<TrackInfo?> LookupAsync(string artist, string title, CancellationToken ct)
    {
        var cleanTitle = TrackText.StripVersionInfo(title);
        var cleanArtist = TrackText.PrimaryArtist(artist);
        var lookup = $"song:{cleanTitle} artist:{cleanArtist}";
        var url = $"{Base}/search/?type=both&limit=10&lookup={Uri.EscapeDataString(lookup)}&api_key={_apiKey}";
        var node = await _json.GetAsync(url, $"gsb:search:{lookup}", ct);

        // Search is fuzzy: the first hit is often a different song, so insist on a name match.
        var wanted = TrackText.MatchKey(cleanArtist, cleanTitle);
        var hit = (node?["search"] as JsonArray).Items()
            .FirstOrDefault(h => TrackText.MatchKey(h["artist"].Str("name") ?? "", h.Str("title") ?? "") == wanted);
        return hit is null ? null : ToTrack(hit, h => h.Str("title"));
    }

    /// <summary>Popular songs at exactly this BPM (40–220).</summary>
    public async Task<IReadOnlyList<TrackInfo>> SongsAtTempoAsync(int bpm, CancellationToken ct)
    {
        bpm = Math.Clamp(bpm, 40, 220);
        var node = await _json.GetAsync($"{Base}/tempo/?bpm={bpm}&limit=250&api_key={_apiKey}", $"gsb:tempo:{bpm}", ct);
        return (node?["tempo"] as JsonArray).Items()
            .Select(h => ToTrack(h, n => n.Str("song_title")))
            .OfType<TrackInfo>()
            .ToList();
    }

    private static TrackInfo? ToTrack(JsonNode hit, Func<JsonNode, string?> title)
    {
        var name = title(hit);
        var artist = hit["artist"].Str("name");
        if (name is null || artist is null)
            return null;

        var genres = (hit["artist"]?["genres"] as JsonArray).Items()
            .Select(g => g.GetValue<string>())
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Take(2)
            .Select(g => char.ToUpperInvariant(g[0]) + g[1..])
            .ToList();

        var bpm = hit.Num("tempo");
        return new TrackInfo
        {
            Artist = artist,
            Title = name,
            Bpm = bpm is > 0 ? bpm : null,
            Key = hit.Str("key_of") ?? hit.Str("open_key"),
            Genre = genres.Count > 0 ? string.Join(", ", genres) : null,
        };
    }
}
