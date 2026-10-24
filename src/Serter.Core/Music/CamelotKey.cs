using System.Text.RegularExpressions;

namespace Serter.Core.Music;

/// <summary>
/// A key on the Camelot wheel (1A–12A minor, 1B–12B major). Parses the formats DJ software and
/// music services use: musical ("Am", "F#m", "Db", "C♯ minor"), Camelot ("8A") and Open Key ("1m", "1d").
/// </summary>
public readonly partial record struct CamelotKey(int Number, bool IsMajor)
{
    // Index = pitch class (C=0 … B=11).
    private static readonly int[] MajorByPitch = [8, 3, 10, 5, 12, 7, 2, 9, 4, 11, 6, 1];
    private static readonly int[] MinorByPitch = [5, 12, 7, 2, 9, 4, 11, 6, 1, 8, 3, 10];
    private static readonly string[] MajorNames = ["", "B", "F♯", "D♭", "A♭", "E♭", "B♭", "F", "C", "G", "D", "A", "E"];
    private static readonly string[] MinorNames = ["", "A♭m", "E♭m", "B♭m", "Fm", "Cm", "Gm", "Dm", "Am", "Em", "Bm", "F♯m", "C♯m"];

    public char Letter => IsMajor ? 'B' : 'A';

    /// <summary>"8A"</summary>
    public string Code => $"{Number}{Letter}";

    /// <summary>"Am"</summary>
    public string MusicalName => IsMajor ? MajorNames[Number] : MinorNames[Number];

    public override string ToString() => Code;

    public static CamelotKey FromPitchClass(int pitchClass, bool isMajor)
    {
        var pc = ((pitchClass % 12) + 12) % 12;
        return new CamelotKey(isMajor ? MajorByPitch[pc] : MinorByPitch[pc], isMajor);
    }

    public static bool TryParse(string? text, out CamelotKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var s = text.Trim()
            .Replace('♯', '#')
            .Replace('♭', 'b')
            .Replace("sharp", "#", StringComparison.OrdinalIgnoreCase)
            .Replace("flat", "b", StringComparison.OrdinalIgnoreCase);

        var camelot = CamelotPattern().Match(s);
        if (camelot.Success)
        {
            var n = int.Parse(camelot.Groups[1].Value);
            if (n is < 1 or > 12)
                return false;
            key = new CamelotKey(n, char.ToUpperInvariant(camelot.Groups[2].Value[0]) == 'B');
            return true;
        }

        var openKey = OpenKeyPattern().Match(s);
        if (openKey.Success)
        {
            var n = int.Parse(openKey.Groups[1].Value);
            if (n is < 1 or > 12)
                return false;
            // Open Key 1d = C major = Camelot 8B; the wheels are offset by 7.
            key = new CamelotKey(((n + 6) % 12) + 1, char.ToLowerInvariant(openKey.Groups[2].Value[0]) == 'd');
            return true;
        }

        var musical = MusicalPattern().Match(s);
        if (musical.Success)
        {
            var pitch = char.ToUpperInvariant(musical.Groups[1].Value[0]) switch
            {
                'C' => 0,
                'D' => 2,
                'E' => 4,
                'F' => 5,
                'G' => 7,
                'A' => 9,
                'B' => 11,
                _ => -1,
            };
            if (pitch < 0)
                return false;
            var accidental = musical.Groups[2].Value;
            if (accidental == "#")
                pitch++;
            else if (accidental == "b")
                pitch--;

            var quality = musical.Groups[3].Value.Trim().ToLowerInvariant();
            var isMinor = quality is "m" or "min" or "minor" or "-";
            key = FromPitchClass(pitch, !isMinor);
            return true;
        }

        return false;
    }

    /// <summary>How well a mix from this key into <paramref name="other"/> works, 0–1, with a short label.</summary>
    public KeyCompatibility CompatibilityWith(CamelotKey other)
    {
        var up = ((other.Number - Number) % 12 + 12) % 12;
        var distance = Math.Min(up, 12 - up);
        var sameMode = IsMajor == other.IsMajor;

        if (distance == 0 && sameMode)
            return new KeyCompatibility(1.0, "Same key");
        if (distance == 0)
            return new KeyCompatibility(0.9, "Relative key");
        if (distance == 1 && sameMode)
            return new KeyCompatibility(0.9, up == 1 ? "+1 (energy up)" : "-1 (energy down)");
        if (distance == 1)
            return new KeyCompatibility(0.55, "Diagonal");
        if (up == 2 && sameMode)
            return new KeyCompatibility(0.6, "+2 (energy boost)");
        if (up == 7 && sameMode)
            return new KeyCompatibility(0.5, "+7 (semitone up)");
        if (distance == 2 && sameMode)
            return new KeyCompatibility(0.35, "-2");
        return new KeyCompatibility(0.05, "Key clash");
    }

    [GeneratedRegex(@"^(\d{1,2})\s*([ABab])$")]
    private static partial Regex CamelotPattern();

    [GeneratedRegex(@"^(\d{1,2})\s*([mMdD])$")]
    private static partial Regex OpenKeyPattern();

    [GeneratedRegex(@"^([A-Ga-g])\s*(#|b)?\s*(m|min|minor|maj|major|-|)$", RegexOptions.IgnoreCase)]
    private static partial Regex MusicalPattern();
}

public readonly record struct KeyCompatibility(double Score, string Label);
