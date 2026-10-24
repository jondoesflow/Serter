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

    /// <summary>Cap on BPM/key lookups per load, so a load stays quick and inside the free APIs' limits.</summary>
    public int MaxLookups { get; init; } = 30;
}

/// <summary>
/// Builds a list of tracks that would mix well with the seed: gathers similar tracks from Deezer,
/// Last.fm, GetSongBPM and the DJ's own Serato history, fills in BPM/key (ReccoBeats, GetSongBPM)
/// and genre, then ranks them.
/// </summary>
public sealed class RecommendationEngine
{
    private readonly DeezerClient _deezer;
    private readonly ReccoBeatsClient _reccoBeats;
    private readonly GetSongBpmClient? _getSongBpm;
    private readonly LastFmClient? _lastFm;
    private readonly RecommendationOptions _options;

    public RecommendationEngine(
        DeezerClient deezer,
        ReccoBeatsClient reccoBeats,
        GetSongBpmClient? getSongBpm,
        LastFmClient? lastFm,
        RecommendationOptions options)
    {
        _deezer = deezer;
        _reccoBeats = reccoBeats;
        _getSongBpm = getSongBpm;
        _lastFm = lastFm;
        _options = options;
    }

    private sealed class Candidate
    {
        public required TrackInfo Track;
        public double Similarity;
        public required string Source;
        public long DeezerId;
        public long DeezerAlbumId;
        public string? Isrc;

        public bool NeedsBpmOrKey => Track.Bpm is null || Track.Camelot is null;
    }

    private delegate void AddCandidate(TrackInfo track, double similarity, string source, DeezerClient.DeezerTrack? deezer = null);

    /// <param name="sessionHistory">Tracks loaded this session (hidden if the option says so).</param>
    /// <param name="knownTracks">Tracks from the DJ's Serato history, which carry Serato's own BPM/key analysis.</param>
    /// <param name="onPartial">Called with a first ranking before BPM/key lookups finish, so the list appears quickly.</param>
    public async Task<RecommendationResult> RecommendAsync(
        TrackInfo seed,
        IReadOnlyList<TrackInfo> sessionHistory,
        IReadOnlyList<TrackInfo> knownTracks,
        CancellationToken ct,
        Action<RecommendationResult>? onPartial = null)
    {
        var notes = new List<string>();
        var known = new Dictionary<string, TrackInfo>();
        foreach (var t in knownTracks)
            known.TryAdd(t.MatchKey, t);

        seed = await CompleteSeedAsync(seed, ct);
        var seedKey = seed.Camelot;

        var candidates = new ConcurrentDictionary<string, Candidate>();
        void Add(TrackInfo track, double similarity, string source, DeezerClient.DeezerTrack? deezer = null)
        {
            if (string.IsNullOrWhiteSpace(track.Artist) || string.IsNullOrWhiteSpace(track.Title))
                return;
            candidates.AddOrUpdate(
                track.MatchKey,
                _ => new Candidate
                {
                    Track = track,
                    Similarity = similarity,
                    Source = source,
                    DeezerId = deezer?.Id ?? 0,
                    DeezerAlbumId = deezer?.AlbumId ?? 0,
                    Isrc = deezer?.Isrc,
                },
                (_, existing) =>
                {
                    // Seen by more than one source: a good sign, nudge it up and keep the best data.
                    existing.Similarity = Math.Min(1, Math.Max(existing.Similarity, similarity) + 0.1);
                    existing.Track = Merge(existing.Track, track);
                    if (deezer is not null && existing.DeezerId == 0)
                    {
                        existing.DeezerId = deezer.Id;
                        existing.DeezerAlbumId = deezer.AlbumId;
                        existing.Isrc ??= deezer.Isrc;
                    }
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

        onPartial?.Invoke(new RecommendationResult(seed, Rank(seed, seedKey, candidates.Values), ["Looking up BPM and key…"]));

        var toLookUp = candidates.Values
            .Where(c => c.NeedsBpmOrKey)
            .OrderByDescending(c => c.Similarity)
            .Take(_options.MaxLookups)
            .ToList();
        await LookUpBpmAndKeyAsync(toLookUp, ct);
        // Genre lookups cost a request each, so only fill them in for tracks in the running.
        var contenders = Rank(seed, seedKey, candidates.Values, _options.MaxResults + 10).Select(r => r.Track.MatchKey).ToHashSet();
        await FillGenresAsync(candidates.Values.Where(c => c.Track.Genre is null && contenders.Contains(c.Track.MatchKey)).ToList(), ct);

        return new RecommendationResult(seed, Rank(seed, seedKey, candidates.Values), notes);
    }

    private List<Recommendation> Rank(TrackInfo seed, CamelotKey? seedKey, IEnumerable<Candidate> candidates, int? take = null) =>
        candidates
            .Select(c => Score(seed, seedKey, c))
            .Where(r => !_options.HideKeyClashes || r.KeyLabel != "Key clash")
            .OrderByDescending(r => r.Score)
            .Take(take ?? _options.MaxResults)
            .ToList();

    /// <summary>
    /// ReccoBeats first (matched exactly by ISRC, no key needed), then GetSongBPM by name for anything
    /// still missing, then Deezer's own occasional BPM.
    /// </summary>
    private async Task LookUpBpmAndKeyAsync(IReadOnlyList<Candidate> candidates, CancellationToken ct)
    {
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct };
        double?[] deezerBpm = new double?[candidates.Count];

        await Parallel.ForEachAsync(Enumerable.Range(0, candidates.Count), parallel, async (i, token) =>
        {
            var c = candidates[i];
            if (c.DeezerId == 0)
            {
                var found = await _deezer.FindTrackAsync(TrackText.PrimaryArtist(c.Track.Artist), TrackText.StripVersionInfo(c.Track.Title), token);
                if (found is null || TrackText.MatchKey(found.Artist, found.Title) != c.Track.MatchKey)
                    return; // Not confident it's the same recording.
                c.DeezerId = found.Id;
                c.DeezerAlbumId = found.AlbumId;
                c.Isrc ??= found.Isrc;
            }
            if (c.Isrc is null)
            {
                var details = await _deezer.TrackDetailsAsync(c.DeezerId, token);
                c.Isrc = details?.Isrc;
                deezerBpm[i] = details?.Bpm;
            }
        });

        var features = await _reccoBeats.AudioFeaturesAsync(candidates.Select(c => c.Isrc).OfType<string>(), ct);
        foreach (var c in candidates)
        {
            if (c.Isrc is not null && features.TryGetValue(c.Isrc.ToUpperInvariant(), out var info))
                c.Track = Merge(c.Track, info);
        }

        if (_getSongBpm is not null)
        {
            await Parallel.ForEachAsync(candidates.Where(c => c.NeedsBpmOrKey), parallel, async (c, token) =>
            {
                var info = await _getSongBpm.LookupAsync(c.Track.Artist, c.Track.Title, token);
                if (info is not null)
                    c.Track = Merge(c.Track, info);
            });
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].Track.Bpm is null && deezerBpm[i] is { } bpm)
                candidates[i].Track = candidates[i].Track with { Bpm = bpm };
        }
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
        var needsBpmOrKey = seed.Bpm is null || seed.Camelot is null;
        if (needsBpmOrKey || seed.Genre is null)
        {
            var dz = await _deezer.FindTrackAsync(TrackText.PrimaryArtist(seed.Artist), TrackText.StripVersionInfo(seed.Title), ct);
            if (dz is not null && TrackText.MatchKey(dz.Artist, dz.Title) == seed.MatchKey)
            {
                if (needsBpmOrKey && dz.Isrc is not null)
                {
                    var features = await _reccoBeats.AudioFeaturesAsync([dz.Isrc], ct);
                    if (features.TryGetValue(dz.Isrc.ToUpperInvariant(), out var info))
                        seed = Merge(seed, info);
                }
                if (seed.Genre is null)
                    seed = seed with { Genre = await _deezer.AlbumGenreAsync(dz.AlbumId, ct) };
            }
        }
        if ((seed.Bpm is null || seed.Camelot is null || seed.Genre is null) && _getSongBpm is not null)
        {
            var info = await _getSongBpm.LookupAsync(seed.Artist, seed.Title, ct);
            if (info is not null)
                seed = Merge(seed, info);
        }
        if (seed.Genre is null && _lastFm is not null)
            seed = seed with { Genre = await _lastFm.ArtistGenreAsync(TrackText.PrimaryArtist(seed.Artist), ct) };
        return seed;
    }

    private async Task GatherFromDeezerAsync(TrackInfo seed, AddCandidate add, CancellationToken ct)
    {
        var artist = TrackText.PrimaryArtist(seed.Artist);
        var track = await _deezer.FindTrackAsync(artist, TrackText.StripVersionInfo(seed.Title), ct);
        var artistId = track?.ArtistId is > 0 ? track.ArtistId : await _deezer.FindArtistIdAsync(artist, ct);
        if (artistId is not > 0)
            return;

        var radio = await _deezer.ArtistRadioAsync(artistId.Value, ct);
        for (var i = 0; i < radio.Count; i++)
            add(radio[i].ToTrackInfo(), 0.65 - i * 0.005, "Deezer radio", radio[i]);

        var related = await _deezer.RelatedArtistsAsync(artistId.Value, 6, ct);
        var tops = await Task.WhenAll(related.Select(id => _deezer.TopTracksAsync(id, 3, ct)));
        foreach (var top in tops)
            foreach (var t in top)
                add(t.ToTrackInfo(), 0.5, "Deezer related artist", t);
    }

    private async Task GatherFromLastFmAsync(TrackInfo seed, AddCandidate add, CancellationToken ct)
    {
        if (_lastFm is null)
            return;
        var similar = await _lastFm.SimilarTracksAsync(TrackText.PrimaryArtist(seed.Artist), TrackText.StripVersionInfo(seed.Title), 40, ct);
        foreach (var s in similar)
            add(new TrackInfo { Artist = s.Artist, Title = s.Title }, 0.55 + s.Match * 0.45, "Last.fm similar");
    }

    /// <summary>Popular songs at the same BPM – only kept when they share the seed's genre, otherwise it's noise.</summary>
    private async Task GatherFromTempoAsync(TrackInfo seed, AddCandidate add, CancellationToken ct)
    {
        if (_getSongBpm is null || seed.Bpm is not > 0 || string.IsNullOrWhiteSpace(seed.Genre))
            return;
        var bpm = (int)Math.Round(seed.Bpm.Value);
        foreach (var t in await _getSongBpm.SongsAtTempoAsync(bpm, ct))
        {
            if (MixScoring.CompareGenre(seed.Genre, t.Genre) >= 1)
                add(t, 0.3, "Same BPM (GetSongBPM)");
        }
    }

    /// <summary>Tracks the DJ has played before that suit this one – these are already in their Serato/TIDAL world.</summary>
    private void GatherFromHistory(TrackInfo seed, IReadOnlyList<TrackInfo> knownTracks, AddCandidate add)
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
                add(t, 0.45, "Your Serato history");
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
