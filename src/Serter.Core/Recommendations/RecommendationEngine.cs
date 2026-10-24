using System.Collections.Concurrent;
using Serter.Core.Models;
using Serter.Core.Music;
using Serter.Core.Sources;

namespace Serter.Core.Recommendations;

public sealed record Recommendation(
    TrackInfo Track,
    double Score,
    string KeyLabel,
    double KeyScore,
    string BpmLabel,
    double BpmScore,
    bool GenreMatch,
    string Source);

public sealed record RecommendationResult(TrackInfo Seed, IReadOnlyList<Recommendation> Tracks, IReadOnlyList<string> Notes);

public sealed record RecommendationOptions
{
    public int MaxResults { get; init; } = 20;
    public double BpmTolerancePercent { get; init; } = 6;
    public bool HideKeyClashes { get; init; } = false;
    public bool HidePlayedThisSession { get; init; } = true;

    /// <summary>Cap on BPM/key lookups per load, to stay well inside GetSongBPM's 3000/hour.</summary>
    public int MaxLookups { get; init; } = 40;
}

/// <summary>
/// Builds a list of tracks that would mix well with the seed: gathers similar tracks from Deezer,
/// Last.fm, GetSongBPM and the DJ's own Serato history, fills in BPM/key/genre, then ranks them.
/// </summary>
public sealed class RecommendationEngine
{
    private readonly DeezerClient _deezer;
    private readonly GetSongBpmClient? _getSongBpm;
    private readonly LastFmClient? _lastFm;
    private readonly RecommendationOptions _options;

    public RecommendationEngine(DeezerClient deezer, GetSongBpmClient? getSongBpm, LastFmClient? lastFm, RecommendationOptions options)
    {
        _deezer = deezer;
        _getSongBpm = getSongBpm;
        _lastFm = lastFm;
        _options = options;
    }

    private sealed class Candidate
    {
        public required TrackInfo Track;
        public double Similarity;
        public required string Source;
        public long DeezerAlbumId;
    }

    /// <param name="sessionHistory">Tracks loaded this session (hidden if the option says so).</param>
    /// <param name="knownTracks">Tracks from the DJ's Serato history, which carry Serato's own BPM/key analysis.</param>
    public async Task<RecommendationResult> RecommendAsync(
        TrackInfo seed,
        IReadOnlyList<TrackInfo> sessionHistory,
        IReadOnlyList<TrackInfo> knownTracks,
        CancellationToken ct)
    {
        var notes = new List<string>();
        var known = new Dictionary<string, TrackInfo>();
        foreach (var t in knownTracks)
            known.TryAdd(t.MatchKey, t);

        seed = await CompleteSeedAsync(seed, ct);
        var seedKey = seed.Camelot;

        var candidates = new ConcurrentDictionary<string, Candidate>();
        void Add(TrackInfo track, double similarity, string source, long albumId = 0)
        {
            if (string.IsNullOrWhiteSpace(track.Artist) || string.IsNullOrWhiteSpace(track.Title))
                return;
            candidates.AddOrUpdate(
                track.MatchKey,
                _ => new Candidate { Track = track, Similarity = similarity, Source = source, DeezerAlbumId = albumId },
                (_, existing) =>
                {
                    // Seen by more than one source: a good sign, nudge it up and keep the best data.
                    existing.Similarity = Math.Min(1, Math.Max(existing.Similarity, similarity) + 0.1);
                    existing.Track = Merge(existing.Track, track);
                    if (existing.DeezerAlbumId == 0)
                        existing.DeezerAlbumId = albumId;
                    return existing;
                });
        }

        var gathering = new List<Task>
        {
            GatherFromDeezerAsync(seed, Add, ct),
            GatherFromLastFmAsync(seed, Add, ct),
            GatherFromTempoAsync(seed, Add, ct),
        };
        GatherFromHistory(seed, knownTracks, Add);
        await Task.WhenAll(gathering);

        // Drop the seed itself, and anything already played tonight.
        var played = _options.HidePlayedThisSession ? sessionHistory.Select(t => t.MatchKey).ToHashSet() : [];
        candidates.TryRemove(seed.MatchKey, out _);
        foreach (var key in played)
            candidates.TryRemove(key, out _);

        if (candidates.IsEmpty)
        {
            notes.Add("No similar tracks found online for this one – try another track or check your internet connection.");
            return new RecommendationResult(seed, [], notes);
        }

        // Fill in what we know from Serato history first (free and exact), then look the rest up.
        foreach (var c in candidates.Values)
        {
            if (known.TryGetValue(c.Track.MatchKey, out var fromHistory))
                c.Track = Merge(fromHistory, c.Track);
        }

        var toLookUp = candidates.Values
            .Where(c => c.Track.Bpm is null || c.Track.Camelot is null)
            .OrderByDescending(c => c.Similarity)
            .Take(_options.MaxLookups)
            .ToList();
        if (_getSongBpm is null && toLookUp.Count > 0)
            notes.Add("Add a free GetSongBPM key in Settings to see BPM and key for suggested tracks.");

        await Parallel.ForEachAsync(toLookUp, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (c, token) =>
        {
            if (_getSongBpm is not null)
            {
                var info = await _getSongBpm.LookupAsync(c.Track.Artist, c.Track.Title, token);
                if (info is not null)
                    c.Track = Merge(c.Track, info);
            }
            if (c.Track.Bpm is null && c.Source.StartsWith("Deezer", StringComparison.Ordinal))
            {
                var dz = await _deezer.FindTrackAsync(c.Track.Artist, c.Track.Title, token);
                if (dz is not null)
                {
                    var bpm = dz.Bpm ?? await _deezer.TrackBpmAsync(dz.Id, token);
                    if (bpm is not null)
                        c.Track = c.Track with { Bpm = bpm };
                }
            }
        });

        await FillGenresAsync(candidates.Values.Where(c => c.Track.Genre is null).ToList(), ct);

        var ranked = candidates.Values
            .Select(c => Score(seed, seedKey, c))
            .Where(r => !_options.HideKeyClashes || r.KeyLabel != "Key clash")
            .OrderByDescending(r => r.Score)
            .Take(_options.MaxResults)
            .ToList();

        return new RecommendationResult(seed, ranked, notes);
    }

    private Recommendation Score(TrackInfo seed, CamelotKey? seedKey, Candidate c)
    {
        var key = MixScoring.CompareKey(seedKey, c.Track.Camelot);
        var bpm = MixScoring.CompareBpm(seed.Bpm, c.Track.Bpm, _options.BpmTolerancePercent);
        var genre = MixScoring.CompareGenre(seed.Genre, c.Track.Genre);
        var score = MixScoring.Overall(key.Score, bpm.Score, genre, c.Similarity);
        return new Recommendation(c.Track, score, key.Label, key.Score, bpm.Label, bpm.Score, genre >= 1, c.Source);
    }

    /// <summary>Serato usually knows BPM/key (it analyses streamed tracks too) – only look up what's missing.</summary>
    private async Task<TrackInfo> CompleteSeedAsync(TrackInfo seed, CancellationToken ct)
    {
        if ((seed.Bpm is null || seed.Camelot is null || seed.Genre is null) && _getSongBpm is not null)
        {
            var info = await _getSongBpm.LookupAsync(seed.Artist, seed.Title, ct);
            if (info is not null)
                seed = Merge(seed, info);
        }
        if (seed.Genre is null && _lastFm is not null)
            seed = seed with { Genre = await _lastFm.ArtistGenreAsync(TrackText.PrimaryArtist(seed.Artist), ct) };
        if (seed.Genre is null)
        {
            var dz = await _deezer.FindTrackAsync(TrackText.PrimaryArtist(seed.Artist), TrackText.StripVersionInfo(seed.Title), ct);
            if (dz is not null)
                seed = seed with { Genre = await _deezer.AlbumGenreAsync(dz.AlbumId, ct) };
        }
        return seed;
    }

    private async Task GatherFromDeezerAsync(TrackInfo seed, Action<TrackInfo, double, string, long> add, CancellationToken ct)
    {
        var artist = TrackText.PrimaryArtist(seed.Artist);
        var track = await _deezer.FindTrackAsync(artist, TrackText.StripVersionInfo(seed.Title), ct);
        var artistId = track?.ArtistId is > 0 ? track.ArtistId : await _deezer.FindArtistIdAsync(artist, ct);
        if (artistId is not > 0)
            return;

        var radio = await _deezer.ArtistRadioAsync(artistId.Value, ct);
        for (var i = 0; i < radio.Count; i++)
            add(radio[i].ToTrackInfo(), 0.65 - i * 0.005, "Deezer radio", radio[i].AlbumId);

        var related = await _deezer.RelatedArtistsAsync(artistId.Value, 6, ct);
        var tops = await Task.WhenAll(related.Select(id => _deezer.TopTracksAsync(id, 3, ct)));
        foreach (var top in tops)
            foreach (var t in top)
                add(t.ToTrackInfo(), 0.5, "Deezer related artist", t.AlbumId);
    }

    private async Task GatherFromLastFmAsync(TrackInfo seed, Action<TrackInfo, double, string, long> add, CancellationToken ct)
    {
        if (_lastFm is null)
            return;
        var similar = await _lastFm.SimilarTracksAsync(TrackText.PrimaryArtist(seed.Artist), TrackText.StripVersionInfo(seed.Title), 40, ct);
        foreach (var s in similar)
            add(new TrackInfo { Artist = s.Artist, Title = s.Title }, 0.55 + s.Match * 0.45, "Last.fm similar", 0);
    }

    /// <summary>Popular songs at the same BPM – only kept when they share the seed's genre, otherwise it's noise.</summary>
    private async Task GatherFromTempoAsync(TrackInfo seed, Action<TrackInfo, double, string, long> add, CancellationToken ct)
    {
        if (_getSongBpm is null || seed.Bpm is not > 0 || string.IsNullOrWhiteSpace(seed.Genre))
            return;
        var bpm = (int)Math.Round(seed.Bpm.Value);
        foreach (var t in await _getSongBpm.SongsAtTempoAsync(bpm, ct))
        {
            if (MixScoring.CompareGenre(seed.Genre, t.Genre) >= 1)
                add(t, 0.3, "Same BPM (GetSongBPM)", 0);
        }
    }

    /// <summary>Tracks the DJ has played before that suit this one – these are already in their Serato/TIDAL world.</summary>
    private void GatherFromHistory(TrackInfo seed, IReadOnlyList<TrackInfo> knownTracks, Action<TrackInfo, double, string, long> add)
    {
        var seedKey = seed.Camelot;
        if (seedKey is null || seed.Bpm is not > 0)
            return;
        foreach (var t in knownTracks)
        {
            var key = MixScoring.CompareKey(seedKey, t.Camelot);
            var bpm = MixScoring.CompareBpm(seed.Bpm, t.Bpm, _options.BpmTolerancePercent);
            var genre = MixScoring.CompareGenre(seed.Genre, t.Genre);
            if (key.Score >= 0.9 && bpm.Score >= 0.6 && genre > 0)
                add(t, 0.45, "Your Serato history", 0);
        }
    }

    private async Task FillGenresAsync(IReadOnlyList<Candidate> missing, CancellationToken ct)
    {
        var byArtist = new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(missing, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (c, token) =>
        {
            string? genre = null;
            if (c.DeezerAlbumId > 0)
                genre = await _deezer.AlbumGenreAsync(c.DeezerAlbumId, token);
            if (genre is null && _lastFm is not null)
            {
                var artist = TrackText.PrimaryArtist(c.Track.Artist);
                if (!byArtist.TryGetValue(artist, out genre))
                {
                    genre = await _lastFm.ArtistGenreAsync(artist, token);
                    byArtist[artist] = genre;
                }
            }
            if (genre is not null)
                c.Track = c.Track with { Genre = genre };
        });
    }

    /// <summary>Keeps <paramref name="primary"/>'s values, filling blanks from <paramref name="fallback"/>.</summary>
    internal static TrackInfo Merge(TrackInfo primary, TrackInfo fallback) => primary with
    {
        Genre = primary.Genre ?? fallback.Genre,
        Bpm = primary.Bpm ?? fallback.Bpm,
        Key = primary.Camelot is not null ? primary.Key : fallback.Key ?? primary.Key,
    };
}
