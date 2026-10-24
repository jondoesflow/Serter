using Serter.Core.Models;
using Serter.Core.Music;

namespace Serter.Core.Sources;

/// <summary>
/// ReccoBeats (https://reccobeats.com) – free, no API key. Returns tempo and key ("audio features")
/// for tracks identified by ISRC, which Deezer supplies. Used for BPM/key of suggested tracks.
/// </summary>
public sealed class ReccoBeatsClient
{
    private const string Base = "https://api.reccobeats.com/v1";
    private const int BatchSize = 25;
    private readonly HttpJson _json;

    public ReccoBeatsClient(HttpJson json)
    {
        _json = json;
    }

    /// <summary>BPM and key for each ISRC it knows. Several releases can share an ISRC, so values are combined.</summary>
    public async Task<IReadOnlyDictionary<string, TrackInfo>> AudioFeaturesAsync(IEnumerable<string> isrcs, CancellationToken ct)
    {
        var wanted = isrcs.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim().ToUpperInvariant()).Distinct().Order().ToList();
        var byIsrc = new Dictionary<string, List<(double Tempo, CamelotKey? Key)>>();

        foreach (var batch in wanted.Chunk(BatchSize))
        {
            var ids = string.Join(',', batch);
            var node = await _json.GetAsync($"{Base}/audio-features?ids={Uri.EscapeDataString(ids)}", $"rb:features:{ids}", ct);
            foreach (var item in node?["content"].Items() ?? [])
            {
                var isrc = item.Str("isrc")?.ToUpperInvariant();
                var tempo = item.Num("tempo");
                if (isrc is null || tempo is not > 0)
                    continue;
                var pitch = item.Num("key");
                var mode = item.Num("mode");
                CamelotKey? key = pitch is >= 0 and <= 11 && mode is not null
                    ? CamelotKey.FromPitchClass((int)pitch.Value, mode.Value >= 1)
                    : null;
                if (!byIsrc.TryGetValue(isrc, out var list))
                    byIsrc[isrc] = list = [];
                list.Add((tempo.Value, key));
            }
        }

        var result = new Dictionary<string, TrackInfo>();
        foreach (var (isrc, values) in byIsrc)
        {
            var tempos = values.Select(v => v.Tempo).Order().ToList();
            var median = tempos[tempos.Count / 2];
            var key = values.Where(v => v.Key is not null)
                .GroupBy(v => v.Key!.Value)
                .OrderByDescending(g => g.Count())
                .Select(g => (CamelotKey?)g.Key)
                .FirstOrDefault();
            result[isrc] = new TrackInfo
            {
                Artist = "",
                Title = "",
                Bpm = Math.Round(median, 1),
                Key = key?.MusicalName,
            };
        }
        return result;
    }
}
