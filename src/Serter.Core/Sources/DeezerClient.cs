using System.Text.Json.Nodes;
using Serter.Core.Models;

namespace Serter.Core.Sources;

/// <summary>
/// Deezer's public API (free, no key). Used to find tracks in the same vein as the loaded one
/// (artist "radio" mixes and related artists' top tracks), genres, and ISRC codes for BPM/key lookups.
/// </summary>
public sealed class DeezerClient
{
    private const string Base = "https://api.deezer.com";
    private readonly HttpJson _json;

    public DeezerClient(HttpJson json)
    {
        _json = json;
    }

    public sealed record DeezerTrack(long Id, string Artist, long ArtistId, string Title, long AlbumId, double? Bpm, string? Isrc);

    public sealed record TrackDetails(string? Isrc, double? Bpm);

    public async Task<DeezerTrack?> FindTrackAsync(string artist, string title, CancellationToken ct)
    {
        var strict = $"artist:\"{artist}\" track:\"{title}\"";
        var node = await _json.GetAsync($"{Base}/search?limit=5&q={Uri.EscapeDataString(strict)}", $"dz:search:{strict}", ct);
        var hit = node?["data"].Items().FirstOrDefault();
        if (hit is null)
        {
            var loose = $"{artist} {title}";
            node = await _json.GetAsync($"{Base}/search?limit=5&q={Uri.EscapeDataString(loose)}", $"dz:search:{loose}", ct);
            hit = node?["data"].Items().FirstOrDefault();
        }
        return hit is null ? null : ToTrack(hit);
    }

    public async Task<long?> FindArtistIdAsync(string artist, CancellationToken ct)
    {
        var node = await _json.GetAsync($"{Base}/search/artist?limit=1&q={Uri.EscapeDataString(artist)}", $"dz:artist:{artist}", ct);
        var id = node?["data"].Items().FirstOrDefault().Num("id");
        return id is null ? null : (long)id;
    }

    /// <summary>Deezer's "artist radio": a mix of tracks by this artist and similar ones.</summary>
    public async Task<IReadOnlyList<DeezerTrack>> ArtistRadioAsync(long artistId, CancellationToken ct)
    {
        var node = await _json.GetAsync($"{Base}/artist/{artistId}/radio?limit=40", $"dz:radio:{artistId}", ct);
        return node?["data"].Items().Select(ToTrack).OfType<DeezerTrack>().ToList() ?? [];
    }

    public async Task<IReadOnlyList<long>> RelatedArtistsAsync(long artistId, int limit, CancellationToken ct)
    {
        var node = await _json.GetAsync($"{Base}/artist/{artistId}/related?limit={limit}", $"dz:related:{artistId}:{limit}", ct);
        return node?["data"].Items().Select(n => n.Num("id")).OfType<double>().Select(d => (long)d).ToList() ?? [];
    }

    public async Task<IReadOnlyList<DeezerTrack>> TopTracksAsync(long artistId, int limit, CancellationToken ct)
    {
        var node = await _json.GetAsync($"{Base}/artist/{artistId}/top?limit={limit}", $"dz:top:{artistId}:{limit}", ct);
        return node?["data"].Items().Select(ToTrack).OfType<DeezerTrack>().ToList() ?? [];
    }

    public async Task<string?> AlbumGenreAsync(long albumId, CancellationToken ct)
    {
        if (albumId <= 0)
            return null;
        var node = await _json.GetAsync($"{Base}/album/{albumId}", $"dz:album:{albumId}", ct);
        var names = node?["genres"]?["data"].Items().Select(g => g.Str("name")).OfType<string>().ToList();
        return names is { Count: > 0 } ? string.Join(", ", names) : null;
    }

    /// <summary>The full track object: its ISRC (for BPM/key lookups) and, occasionally, a BPM.</summary>
    public async Task<TrackDetails?> TrackDetailsAsync(long trackId, CancellationToken ct)
    {
        var node = await _json.GetAsync($"{Base}/track/{trackId}", $"dz:track:{trackId}", ct);
        if (node is null)
            return null;
        var bpm = node.Num("bpm");
        return new TrackDetails(node.Str("isrc"), bpm is > 0 ? bpm : null);
    }

    private static DeezerTrack? ToTrack(JsonNode node)
    {
        var id = node.Num("id");
        var title = node.Str("title");
        var artist = node["artist"].Str("name");
        if (id is null || title is null || artist is null)
            return null;
        var bpm = node.Num("bpm");
        return new DeezerTrack((long)id, artist, (long)(node["artist"].Num("id") ?? 0), title, (long)(node["album"].Num("id") ?? 0), bpm is > 0 ? bpm : null, node.Str("isrc"));
    }
}

public static class DeezerTrackExtensions
{
    public static TrackInfo ToTrackInfo(this DeezerClient.DeezerTrack track) => new()
    {
        Artist = track.Artist,
        Title = track.Title,
        Bpm = track.Bpm,
    };
}
