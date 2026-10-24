using Serter.Core.Models;

namespace Serter.Core.Serato;

/// <summary>Polls Serato and raises an event whenever a different track lands on deck A or B.</summary>
public sealed class DeckMonitor : IDisposable
{
    private readonly Func<IDeckSource?> _sourceFactory;
    private readonly TimeSpan _interval;
    private readonly Dictionary<Deck, string> _lastSeen = new();
    private readonly object _gate = new();
    private Timer? _timer;
    private IDeckSource? _source;
    private bool _polling;

    public DeckMonitor(Func<IDeckSource?> sourceFactory, TimeSpan interval)
    {
        _sourceFactory = sourceFactory;
        _interval = interval;
    }

    /// <summary>Raised on a thread-pool thread when a deck's track changes.</summary>
    public event EventHandler<DeckTrack>? TrackLoaded;

    /// <summary>Raised when the connection status text changes (found Serato, errors…).</summary>
    public event EventHandler<string>? StatusChanged;

    public DeckSnapshot Latest { get; private set; } = DeckSnapshot.Empty;

    public void Start()
    {
        _timer = new Timer(_ => Poll(), null, TimeSpan.Zero, _interval);
    }

    /// <summary>Search for Serato again on the next poll (e.g. after the folder setting changes).</summary>
    public void Reset()
    {
        lock (_gate)
            _source = null;
    }

    public void Poll()
    {
        lock (_gate)
        {
            if (_polling)
                return;
            _polling = true;
        }

        try
        {
            IDeckSource? source;
            lock (_gate)
            {
                _source ??= _sourceFactory();
                source = _source;
            }
            if (source is null)
            {
                StatusChanged?.Invoke(this, "Serato history not found – is Serato installed? (see Settings)");
                return;
            }

            var snapshot = source.Read();
            Latest = snapshot;
            StatusChanged?.Invoke(this, $"Watching {source.Description}");

            foreach (var (deck, deckTrack) in snapshot.Decks)
            {
                var signature = $"{deckTrack.Track.MatchKey}@{deckTrack.LoadedAt.ToUnixTimeMilliseconds()}";
                bool changed;
                lock (_gate)
                {
                    changed = !_lastSeen.TryGetValue(deck, out var previous) || previous != signature;
                    _lastSeen[deck] = signature;
                }
                if (changed)
                    TrackLoaded?.Invoke(this, deckTrack);
            }
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"Couldn't read Serato history: {ex.Message}");
        }
        finally
        {
            lock (_gate)
                _polling = false;
        }
    }

    public void Dispose() => _timer?.Dispose();
}
