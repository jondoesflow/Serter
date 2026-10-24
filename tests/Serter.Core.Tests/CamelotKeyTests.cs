using Serter.Core.Music;

namespace Serter.Core.Tests;

public class CamelotKeyTests
{
    [Theory]
    [InlineData("Am", "8A")]
    [InlineData("A minor", "8A")]
    [InlineData("C", "8B")]
    [InlineData("C major", "8B")]
    [InlineData("F#m", "11A")]
    [InlineData("F♯m", "11A")]
    [InlineData("Gbm", "11A")]
    [InlineData("Bbm", "3A")]
    [InlineData("B♭m", "3A")]
    [InlineData("Db", "3B")]
    [InlineData("C#", "3B")]
    [InlineData("D#", "5B")]
    [InlineData("Ebm", "2A")]
    [InlineData("Abm", "1A")]
    [InlineData("B", "1B")]
    [InlineData("Bm", "10A")]
    [InlineData("E", "12B")]
    [InlineData("8A", "8A")]
    [InlineData("12b", "12B")]
    [InlineData("1m", "8A")]  // Open Key: 1m = A minor
    [InlineData("1d", "8B")]  // Open Key: 1d = C major
    [InlineData("2m", "9A")]  // Em
    [InlineData("4m", "11A")] // F#m
    [InlineData("10d", "5B")] // Eb major
    public void Parses_common_notations(string text, string expected)
    {
        Assert.True(CamelotKey.TryParse(text, out var key));
        Assert.Equal(expected, key.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("13A")]
    [InlineData("H")]
    [InlineData("unknown")]
    public void Rejects_garbage(string? text)
    {
        Assert.False(CamelotKey.TryParse(text, out _));
    }

    [Fact]
    public void Musical_names_round_trip()
    {
        for (var n = 1; n <= 12; n++)
        {
            foreach (var major in new[] { true, false })
            {
                var key = new CamelotKey(n, major);
                Assert.True(CamelotKey.TryParse(key.MusicalName, out var parsed), key.MusicalName);
                Assert.Equal(key, parsed);
            }
        }
    }

    [Theory]
    [InlineData("8A", "8A", 1.0)]
    [InlineData("8A", "8B", 0.9)]
    [InlineData("8A", "9A", 0.9)]
    [InlineData("8A", "7A", 0.9)]
    [InlineData("12A", "1A", 0.9)]
    [InlineData("8A", "10A", 0.6)]
    [InlineData("8A", "3A", 0.5)]
    [InlineData("8A", "2B", 0.05)]
    public void Scores_harmonic_mixes(string from, string to, double expected)
    {
        CamelotKey.TryParse(from, out var a);
        CamelotKey.TryParse(to, out var b);
        Assert.Equal(expected, a.CompatibilityWith(b).Score, 3);
    }
}
