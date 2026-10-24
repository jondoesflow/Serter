using System.Net.Http;
using System.Windows;
using Serter.Core;
using Serter.Core.Models;
using Serter.Core.Recommendations;
using Serter.Core.Serato;
using Serter.Core.Sources;

namespace Serter.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private static readonly HttpClient Http = CreateHttpClient();

    private readonly DeckMonitor _monitor;
    private RecommendationEngine _engine = null!;
    private IReadOnlyList<TrackInfo> _knownTracks = [];
    private DateTime _knownTracksLoadedAt = DateTime.MinValue;
    private string _status = "Looking for Serato…";
    private string _toast = "";
    private CancellationTokenSource? _toastCts;

    public MainViewModel(AppSettings settings)
    {
        Settings = settings;
        DeckA = new DeckViewModel(Deck.A);
        DeckB = new DeckViewModel(Deck.B);

        _monitor = new DeckMonitor(() => SeratoLocator.Locate(Settings.SeratoPathOverride), TimeSpan.FromSeconds(1.5));
        _monitor.TrackLoaded += (_, track) => OnUi(() => OnTrackLoaded(track));
        _monitor.StatusChanged += (_, status) => OnUi(() => Status = status);

        CopyCommand = new RelayCommand(p => Copy(p as TrackDisplay));
        SwitchDeckCommand = new RelayCommand(p => SelectedDeck = p is Deck d ? d : SelectedDeck == Deck.A ? Deck.B : Deck.A);
        RefreshCommand = new RelayCommand(_ => _ = RecommendAsync(Current, Current.Loaded?.Track));

        ApplySettings();
    }

    public AppSettings Settings { get; }
    public DeckViewModel DeckA { get; }
    public DeckViewModel DeckB { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand SwitchDeckCommand { get; }
    public RelayCommand RefreshCommand { get; }

    public Deck SelectedDeck
    {
        get => Settings.SelectedDeck;
        set
        {
            if (Settings.SelectedDeck == value)
                return;
            Settings.SelectedDeck = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Current));
            OnPropertyChanged(nameof(IsDeckA));
            OnPropertyChanged(nameof(IsDeckB));
        }
    }

    public bool IsDeckA
    {
        get => SelectedDeck == Deck.A;
        set { if (value) SelectedDeck = Deck.A; }
    }

    public bool IsDeckB
    {
        get => SelectedDeck == Deck.B;
        set { if (value) SelectedDeck = Deck.B; }
    }

    public bool FollowLatestDeck
    {
        get => Settings.FollowLatestDeck;
        set
        {
            Settings.FollowLatestDeck = value;
            OnPropertyChanged();
        }
    }

    public DeckViewModel Current => SelectedDeck == Deck.A ? DeckA : DeckB;

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>Short-lived confirmation such as "Copied: Bicep Glue".</summary>
    public string Toast
    {
        get => _toast;
        private set => Set(ref _toast, value);
    }

    public string SourcesSummary =>
        "Deezer · ReccoBeats" +
        (string.IsNullOrWhiteSpace(Settings.GetSongBpmApiKey) ? "" : " · GetSongBPM") +
        (string.IsNullOrWhiteSpace(Settings.LastFmApiKey) ? "" : " · Last.fm");

    public void Start() => _monitor.Start();

    /// <summary>Rebuilds API clients from settings (called at start-up and after the settings window closes).</summary>
    public void ApplySettings()
    {
        var cache = new HttpJson(Http, System.IO.Path.Combine(AppSettings.Folder, "cache"), TimeSpan.FromDays(30));
        var gsb = string.IsNullOrWhiteSpace(Settings.GetSongBpmApiKey) ? null : new GetSongBpmClient(cache, Settings.GetSongBpmApiKey.Trim());
        var lastFm = string.IsNullOrWhiteSpace(Settings.LastFmApiKey) ? null : new LastFmClient(cache, Settings.LastFmApiKey.Trim());
        cache.Pace("api.deezer.com", TimeSpan.FromMilliseconds(100)); // Deezer allows 50 requests per 5 seconds.
        _engine = new RecommendationEngine(new DeezerClient(cache), new ReccoBeatsClient(cache), gsb, lastFm, new RecommendationOptions
        {
            MaxResults = Math.Clamp(Settings.MaxResults, 5, 50),
            BpmTolerancePercent = Math.Clamp(Settings.BpmTolerancePercent, 1, 20),
            HideKeyClashes = Settings.HideKeyClashes,
            HidePlayedThisSession = Settings.HidePlayedThisSession,
        });
        _monitor.Reset();
        _knownTracksLoadedAt = DateTime.MinValue;
        OnPropertyChanged(nameof(SourcesSummary));

        foreach (var deck in new[] { DeckA, DeckB })
        {
            if (deck.Loaded is not null)
                _ = RecommendAsync(deck, deck.Loaded.Track);
        }
    }

    /// <summary>Lets the DJ type "Artist - Title" to get suggestions without Serato (handy for prep).</summary>
    public void SearchManually(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        var parts = text.Split(" - ", 2, StringSplitOptions.TrimEntries);
        var track = parts.Length == 2
            ? new TrackInfo { Artist = parts[0], Title = parts[1] }
            : new TrackInfo { Artist = "", Title = text.Trim() };
        if (track.Artist.Length == 0)
        {
            Toast = "Type it as: Artist - Title";
            return;
        }
        _ = RecommendAsync(Current, track);
    }

    private void OnTrackLoaded(DeckTrack deckTrack)
    {
        var deck = deckTrack.Deck == Deck.A ? DeckA : DeckB;
        if (FollowLatestDeck)
            SelectedDeck = deckTrack.Deck;
        _ = RecommendAsync(deck, deckTrack.Track);
    }

    private async Task RecommendAsync(DeckViewModel deck, TrackInfo? track)
    {
        if (track is null)
            return;
        var ct = deck.BeginLoad();
        deck.Loaded = new TrackDisplay(track);
        deck.IsBusy = true;
        deck.Message = "Finding tracks that mix well…";
        deck.Suggestions.Clear();

        try
        {
            var history = _monitor.Latest.SessionHistory;
            var known = await Task.Run(KnownTracks, ct);
            void Show(RecommendationResult result)
            {
                if (ct.IsCancellationRequested)
                    return;
                deck.Loaded = new TrackDisplay(result.Seed);
                deck.Suggestions.Clear();
                var rank = 1;
                foreach (var r in result.Tracks)
                    deck.Suggestions.Add(new RecommendationViewModel(r, rank++));
                deck.Message = result.Notes.Count > 0
                    ? string.Join(" ", result.Notes)
                    : $"{result.Tracks.Count} suggestions · best matches first";
            }

            var final = await Task.Run(() => _engine.RecommendAsync(track, history, known, ct, partial => OnUi(() => Show(partial))), ct);
            Show(final);
        }
        catch (OperationCanceledException)
        {
            // A newer track was loaded on this deck.
        }
        catch (Exception ex)
        {
            deck.Message = $"Couldn't get suggestions: {ex.Message}";
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                deck.IsBusy = false;
        }
    }

    /// <summary>Tracks from all Serato sessions, refreshed every 10 minutes.</summary>
    private IReadOnlyList<TrackInfo> KnownTracks()
    {
        if (DateTime.UtcNow - _knownTracksLoadedAt < TimeSpan.FromMinutes(10))
            return _knownTracks;
        try
        {
            _knownTracks = SeratoLocator.Locate(Settings.SeratoPathOverride)?.ReadAllHistory() ?? [];
        }
        catch (Exception)
        {
            _knownTracks = [];
        }
        _knownTracksLoadedAt = DateTime.UtcNow;
        return _knownTracks;
    }

    private void Copy(TrackDisplay? track)
    {
        if (track is null)
            return;
        var text = Settings.FormatForCopy(track.Track);
        // The clipboard is occasionally locked by another app; retry briefly.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                ShowToast($"Copied: {text}");
                return;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(30);
            }
        }
        ShowToast("Clipboard busy – try again");
    }

    private async void ShowToast(string text)
    {
        _toastCts?.Cancel();
        var cts = _toastCts = new CancellationTokenSource();
        Toast = text;
        try
        {
            await Task.Delay(2500, cts.Token);
            Toast = "";
        }
        catch (TaskCanceledException)
        {
        }
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;
        if (dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Serter/1.0 (+https://github.com/jondoesflow/serter)");
        return http;
    }

    public void Dispose() => _monitor.Dispose();
}
