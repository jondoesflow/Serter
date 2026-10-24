using Serter.Core.Models;
using Serter.Core.Music;

namespace Serter.Core.Recommendations;

/// <summary>Scores how well one track mixes into another on key, tempo and genre.</summary>
public static class MixScoring
{
    private static readonly HashSet<string> GenericGenreWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "music", "and", "the", "&", "n", "of",
    };

    public sealed record BpmMatch(double Score, string Label, double? EffectiveBpm);

    /// <summary>
    /// Compares tempos, allowing half/double time (a 87 BPM track can sit over a 174 BPM one).
    /// <paramref name="tolerancePercent"/> is how far the pitch fader can comfortably go.
    /// </summary>
    public static BpmMatch CompareBpm(double? seed, double? candidate, double tolerancePercent)
    {
        if (seed is not > 0 || candidate is not > 0)
            return new BpmMatch(0.3, "BPM ?", null);

        var options = new[] { (candidate.Value, ""), (candidate.Value * 2, " (half-time)"), (candidate.Value / 2, " (double-time)") };
        var (effective, note) = options.MinBy(o => Math.Abs(o.Item1 - seed.Value));
        var diffPercent = (effective - seed.Value) / seed.Value * 100;
        var abs = Math.Abs(diffPercent);
        var score = abs <= tolerancePercent ? 1 - (abs / tolerancePercent) * 0.6 : Math.Max(0, 0.4 - (abs - tolerancePercent) / 10);
        if (note.Length > 0)
            score *= 0.85;

        var label = abs < 0.5 ? $"Same tempo{note}" : $"{diffPercent:+0.0;-0.0}%{note}";
        return new BpmMatch(score, label, effective);
    }

    public static KeyCompatibility CompareKey(CamelotKey? seed, CamelotKey? candidate)
    {
        if (seed is null || candidate is null)
            return new KeyCompatibility(0.3, "Key ?");
        return seed.Value.CompatibilityWith(candidate.Value);
    }

    /// <summary>1 = shares a genre word, 0 = clearly different, 0.5 = unknown.</summary>
    public static double CompareGenre(string? seed, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(seed) || string.IsNullOrWhiteSpace(candidate))
            return 0.5;
        var a = GenreWords(seed);
        var b = GenreWords(candidate);
        if (a.Count == 0 || b.Count == 0)
            return 0.5;
        return a.Overlaps(b) ? 1.0 : 0.0;
    }

    public static HashSet<string> GenreWords(string genre) =>
        genre.ToLowerInvariant()
            .Split([' ', ',', '/', '-', '&', ';', '|'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Replace("'", ""))
            .Where(w => w.Length > 1 && !GenericGenreWords.Contains(w))
            .Select(w => w switch
            {
                "hiphop" or "rap" => "hip",
                "dnb" => "drum",
                "electronica" => "electronic",
                _ => w,
            })
            .ToHashSet();

    public static double Overall(double key, double bpm, double genre, double similarity) =>
        0.40 * key + 0.30 * bpm + 0.15 * genre + 0.15 * Math.Clamp(similarity, 0, 1);

    public static bool SameTrack(TrackInfo a, TrackInfo b) => a.MatchKey == b.MatchKey;
}
