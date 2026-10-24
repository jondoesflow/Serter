using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Serter.Core.Music;

/// <summary>Helpers for comparing and cleaning up artist / title text from different services.</summary>
public static partial class TrackText
{
    /// <summary>Lower-case, accent-free, punctuation-free form of "artist|title" for de-duplication.</summary>
    public static string MatchKey(string artist, string title) =>
        $"{Normalise(PrimaryArtist(artist))}|{Normalise(StripVersionInfo(title))}";

    /// <summary>"Artist A feat. Artist B" / "A, B" / "A & B" -> "Artist A".</summary>
    public static string PrimaryArtist(string artist)
    {
        var parts = ArtistSplit().Split(artist);
        return parts.Length > 0 && parts[0].Trim().Length > 0 ? parts[0].Trim() : artist.Trim();
    }

    /// <summary>Removes "(Original Mix)", "[Remastered 2011]", "- Radio Edit" etc. so lookups match better.</summary>
    public static string StripVersionInfo(string title)
    {
        var cleaned = Brackets().Replace(title, " ");
        cleaned = DashSuffix().Replace(cleaned, "");
        cleaned = Feat().Replace(cleaned, "");
        cleaned = Whitespace().Replace(cleaned, " ").Trim();
        return cleaned.Length > 0 ? cleaned : title.Trim();
    }

    public static string Normalise(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(c))
                sb.Append(c);
            else if (c == '&')
                sb.Append("and");
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"\s*(?:,|&|\bx\b|\bfeat\.?|\bft\.?|\bfeaturing\b|\bvs\.?|\bwith\b|;|/)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ArtistSplit();

    [GeneratedRegex(@"[\(\[][^\)\]]*[\)\]]")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"\s+-\s+.*(?:mix|edit|remaster(?:ed)?|version|remix|dub|live|extended|radio|original).*$", RegexOptions.IgnoreCase)]
    private static partial Regex DashSuffix();

    [GeneratedRegex(@"\s+(?:feat\.?|ft\.?|featuring)\s+.*$", RegexOptions.IgnoreCase)]
    private static partial Regex Feat();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
