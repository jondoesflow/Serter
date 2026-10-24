namespace Serter.Core.Sources;

/// <summary>
/// Last.fm (free API key). Optional: gives "people who play this also play…" similar tracks and
/// genre tags, which noticeably improves suggestions for underground / club tracks.
/// </summary>
public sealed class LastFmClient
{
    private const string Base = "https://ws.audioscrobbler.com/2.0/";
    private static readonly HashSet<string> NonGenreTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "seen live", "favorites", "favourite", "favorite", "love", "loved", "awesome", "beautiful",
        "female vocalists", "male vocalists", "british", "american", "uk", "usa", "german", "french",
        "00s", "10s", "20s", "90s", "80s", "70s", "2010s", "2020s", "spotify", "my favorite",
    };

    private readonly HttpJson _json;
    private readonly string _apiKey;

    public LastFmClient(HttpJson json, string apiKey)
    {
        _json = json;
        _apiKey = apiKey;
    }

    public sealed record SimilarTrack(string Artist, string Title, double Match);

    public async Task<IReadOnlyList<SimilarTrack>> SimilarTracksAsync(string artist, string title, int limit, CancellationToken ct)
    {
        var url = $"{Base}?method=track.getsimilar&autocorrect=1&format=json&limit={limit}" +
                  $"&artist={Uri.EscapeDataString(artist)}&track={Uri.EscapeDataString(title)}&api_key={_apiKey}";
        var node = await _json.GetAsync(url, $"lf:similar:{artist}|{title}|{limit}", ct);
        return node?["similartracks"]?["track"].Items()
            .Select(t => new SimilarTrack(t["artist"].Str("name") ?? "", t.Str("name") ?? "", t.Num("match") ?? 0))
            .Where(t => t.Artist.Length > 0 && t.Title.Length > 0)
            .ToList() ?? [];
    }

    /// <summary>Most-used genre-like tags for an artist, e.g. "deep house, house".</summary>
    public async Task<string?> ArtistGenreAsync(string artist, CancellationToken ct)
    {
        var url = $"{Base}?method=artist.gettoptags&autocorrect=1&format=json&artist={Uri.EscapeDataString(artist)}&api_key={_apiKey}";
        var node = await _json.GetAsync(url, $"lf:artisttags:{artist}", ct);
        var tags = node?["toptags"]?["tag"].Items()
            .Select(t => t.Str("name"))
            .OfType<string>()
            .Where(t => !NonGenreTags.Contains(t) && !t.Any(char.IsDigit))
            .Take(2)
            .Select(Capitalise)
            .ToList();
        return tags is { Count: > 0 } ? string.Join(", ", tags) : null;
    }

    private static string Capitalise(string tag) =>
        string.Join(' ', tag.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
}
