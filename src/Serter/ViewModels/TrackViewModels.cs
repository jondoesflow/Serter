using System.Collections.ObjectModel;
using System.Globalization;
using Serter.Core.Models;
using Serter.Core.Recommendations;

namespace Serter.ViewModels;

/// <summary>Display helpers shared by the loaded track and each suggestion.</summary>
public class TrackDisplay
{
    public TrackDisplay(TrackInfo track)
    {
        Track = track;
    }

    public TrackInfo Track { get; }
    public string Artist => Track.Artist;
    public string Title => Track.Title;
    public string Genre => string.IsNullOrWhiteSpace(Track.Genre) ? "Genre ?" : Track.Genre!;
    public string Bpm => Track.Bpm is > 0 ? Track.Bpm.Value.ToString(Track.Bpm % 1 == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) : "–";

    public string Key => Track.Camelot is { } k ? $"{k.Code} · {k.MusicalName}" : string.IsNullOrWhiteSpace(Track.Key) ? "–" : Track.Key!;
}

public sealed class RecommendationViewModel : TrackDisplay
{
    public RecommendationViewModel(Recommendation recommendation, int rank) : base(recommendation.Track)
    {
        Recommendation = recommendation;
        Rank = rank;
    }

    public Recommendation Recommendation { get; }
    public int Rank { get; }
    public string KeyLabel => Recommendation.KeyLabel;
    public string BpmLabel => Recommendation.BpmLabel;
    public string Source => Recommendation.Source;
    public bool GenreMatch => Recommendation.GenreMatch;
    public int ScorePercent => (int)Math.Round(Recommendation.Score * 100);

    /// <summary>Traffic lights for the row: Good (green), Ok (amber), Bad (red) or Unknown (grey).</summary>
    public string KeyQuality => Quality(Recommendation.KeyLabel == "Key ?", Recommendation.KeyScore);

    public string BpmQuality => Quality(Recommendation.BpmLabel == "BPM ?", Recommendation.BpmScore);

    private static string Quality(bool unknown, double score) =>
        unknown ? "Unknown" : score >= 0.8 ? "Good" : score >= 0.45 ? "Ok" : "Bad";
}

/// <summary>Everything the window shows for one deck.</summary>
public sealed class DeckViewModel : ObservableObject
{
    private TrackDisplay? _loaded;
    private bool _isBusy;
    private string _message = "Waiting for a track to be loaded in Serato…";
    private CancellationTokenSource? _cts;

    public DeckViewModel(Deck deck)
    {
        Deck = deck;
    }

    public Deck Deck { get; }
    public ObservableCollection<RecommendationViewModel> Suggestions { get; } = [];

    public TrackDisplay? Loaded
    {
        get => _loaded;
        set
        {
            if (Set(ref _loaded, value))
                OnPropertyChanged(nameof(HasTrack));
        }
    }

    public bool HasTrack => Loaded is not null;

    public bool IsBusy
    {
        get => _isBusy;
        set => Set(ref _isBusy, value);
    }

    public string Message
    {
        get => _message;
        set => Set(ref _message, value);
    }

    /// <summary>Cancels any in-flight lookup for this deck and hands out a token for the next one.</summary>
    public CancellationToken BeginLoad()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        return _cts.Token;
    }
}
