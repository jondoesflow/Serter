using Serter.Core;
using Serter.Core.Models;
using Serter.Core.Music;
using Serter.Core.Recommendations;

namespace Serter.Core.Tests;

public class MixScoringTests
{
    [Fact]
    public void Same_bpm_scores_top()
    {
        var match = MixScoring.CompareBpm(124, 124, 6);
        Assert.Equal(1.0, match.Score, 3);
        Assert.Equal("Same tempo", match.Label);
    }

    [Fact]
    public void Half_time_is_recognised()
    {
        var match = MixScoring.CompareBpm(174, 87, 6);
        Assert.True(match.Score > 0.8);
        Assert.Contains("half-time", match.Label);
    }

    [Fact]
    public void Far_bpm_scores_low()
    {
        Assert.True(MixScoring.CompareBpm(124, 100, 6).Score < 0.1);
    }

    [Fact]
    public void Unknown_bpm_is_neutral()
    {
        Assert.Equal(0.3, MixScoring.CompareBpm(124, null, 6).Score);
    }

    [Theory]
    [InlineData("Deep House", "House, Electronic", 1.0)]
    [InlineData("Tech House", "Techno", 0.0)]
    [InlineData("Drum & Bass", "Drum And Bass", 1.0)]
    [InlineData("Hip-Hop", "Rap", 1.0)]
    [InlineData(null, "House", 0.5)]
    public void Genre_overlap(string? a, string? b, double expected)
    {
        Assert.Equal(expected, MixScoring.CompareGenre(a, b));
    }

    [Theory]
    [InlineData("Daft Punk", "One More Time (Radio Edit)", "daft punk", "One More Time")]
    [InlineData("Fred again.. & Swedish House Mafia", "Turn On The Lights again..", "Fred again..", "Turn On The Lights again..")]
    [InlineData("Artist feat. Singer", "Song - Extended Mix", "Artist", "Song")]
    public void Match_keys_ignore_versions_and_features(string artist, string title, string otherArtist, string otherTitle)
    {
        Assert.Equal(TrackText.MatchKey(otherArtist, otherTitle), TrackText.MatchKey(artist, title));
    }

    [Fact]
    public void Copy_format_uses_artist_and_title()
    {
        var settings = new AppSettings { CopyFormat = "{artist} - {title}" };
        var track = new TrackInfo { Artist = "Bicep", Title = "Glue" };
        Assert.Equal("Bicep - Glue", settings.FormatForCopy(track));
        Assert.Equal("Bicep Glue", new AppSettings().FormatForCopy(track));
    }

    [Fact]
    public void Merge_keeps_primary_and_fills_blanks()
    {
        var primary = new TrackInfo { Artist = "A", Title = "T", Bpm = 122 };
        var fallback = new TrackInfo { Artist = "A", Title = "T", Bpm = 120, Key = "Am", Genre = "House" };
        var merged = RecommendationEngine.Merge(primary, fallback);
        Assert.Equal(122, merged.Bpm);
        Assert.Equal("Am", merged.Key);
        Assert.Equal("House", merged.Genre);
    }
}
