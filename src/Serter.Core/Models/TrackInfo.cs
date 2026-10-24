using Serter.Core.Music;

namespace Serter.Core.Models;

/// <summary>The two sides of the mixer the app follows.</summary>
public enum Deck
{
    A,
    B,
}

/// <summary>A track as shown in the app: artist, title, genre, BPM and key.</summary>
public sealed record TrackInfo
{
    public required string Artist { get; init; }
    public required string Title { get; init; }
    public string? Genre { get; init; }
    public double? Bpm { get; init; }

    /// <summary>The key exactly as the source reported it (e.g. "Am", "8A", "C♯ minor").</summary>
    public string? Key { get; init; }

    public CamelotKey? Camelot => CamelotKey.TryParse(Key, out var key) ? key : null;

    /// <summary>Normalised "artist|title" used to de-duplicate tracks between sources.</summary>
    public string MatchKey => TrackText.MatchKey(Artist, Title);

    public override string ToString() => $"{Artist} - {Title}";
}

/// <summary>What is loaded on a deck right now.</summary>
public sealed record DeckTrack(Deck Deck, TrackInfo Track, DateTimeOffset LoadedAt);
