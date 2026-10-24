using Serter.Core.Models;

namespace Serter.Core.Serato;

/// <summary>A snapshot of what Serato currently has on its decks, plus this session's history.</summary>
public sealed record DeckSnapshot(
    IReadOnlyDictionary<Deck, DeckTrack> Decks,
    IReadOnlyList<TrackInfo> SessionHistory)
{
    public static DeckSnapshot Empty { get; } = new(new Dictionary<Deck, DeckTrack>(), []);
}

/// <summary>Somewhere we can read Serato's deck state from.</summary>
public interface IDeckSource
{
    string Description { get; }

    DeckSnapshot Read();

    /// <summary>Every track in the DJ's Serato history (all sessions), with Serato's BPM/key analysis.</summary>
    IReadOnlyList<TrackInfo> ReadAllHistory();
}

internal static class DeckMapping
{
    /// <summary>
    /// Serato numbers decks 1–4. In 2-deck mode 1 is left (A) and 2 is right (B); in 4-deck mode
    /// decks 1 and 3 sit on the left, 2 and 4 on the right, so odd → A and even → B.
    /// </summary>
    public static Deck? FromSeratoDeck(long deck) => deck switch
    {
        <= 0 => null,
        _ when deck % 2 == 1 => Deck.A,
        _ => Deck.B,
    };
}
