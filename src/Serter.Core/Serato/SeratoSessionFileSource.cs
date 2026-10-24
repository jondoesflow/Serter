using System.Buffers.Binary;
using System.Text;
using Serter.Core.Models;

namespace Serter.Core.Serato;

/// <summary>
/// Reads the binary history files Serato DJ Pro 3.x and earlier write to
/// <c>Music\_Serato_\History\Sessions\*.session</c>. Each loaded track is an "oent" chunk holding an
/// "adat" record of numbered fields (title, artist, BPM, key, deck…).
/// </summary>
public sealed class SeratoSessionFileSource : IDeckSource
{
    private const int FieldTitle = 6;
    private const int FieldArtist = 7;
    private const int FieldGenre = 9;
    private const int FieldBpm = 15;
    private const int FieldStartTime = 28;
    private const int FieldDeck = 31;
    private const int FieldKey = 51;

    private readonly string _sessionsDirectory;

    public SeratoSessionFileSource(string sessionsDirectory)
    {
        _sessionsDirectory = sessionsDirectory;
    }

    public string Description => $"Serato session history ({_sessionsDirectory})";

    public DeckSnapshot Read()
    {
        var latest = LatestSessionFile(_sessionsDirectory);
        if (latest is null)
            return DeckSnapshot.Empty;

        return Parse(ReadShared(latest.FullName));
    }

    public IReadOnlyList<TrackInfo> ReadAllHistory()
    {
        if (!Directory.Exists(_sessionsDirectory))
            return [];
        var tracks = new List<TrackInfo>();
        var seen = new HashSet<string>();
        var files = new DirectoryInfo(_sessionsDirectory)
            .EnumerateFiles("*.session")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(200);
        foreach (var file in files)
        {
            try
            {
                foreach (var track in Parse(ReadShared(file.FullName)).SessionHistory)
                {
                    if (track.Bpm is > 0 && track.Camelot is not null && seen.Add(track.MatchKey))
                        tracks.Add(track);
                }
            }
            catch (IOException)
            {
                // Skip files Serato is busy with.
            }
        }
        return tracks;
    }

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    internal static FileInfo? LatestSessionFile(string directory)
    {
        if (!Directory.Exists(directory))
            return null;
        return new DirectoryInfo(directory)
            .EnumerateFiles("*.session")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
    }

    public static DeckSnapshot Parse(ReadOnlySpan<byte> data)
    {
        var decks = new Dictionary<Deck, DeckTrack>();
        var history = new List<TrackInfo>();
        var order = 0;

        foreach (var (tag, payload) in Chunks(data))
        {
            if (tag != "oent")
                continue;
            foreach (var (innerTag, adat) in Chunks(payload))
            {
                if (innerTag != "adat")
                    continue;
                // Fields normally start at offset 0; some versions prefix a 12-byte header.
                var entry = ParseAdat(adat, 0) ?? ParseAdat(adat, 12);
                if (entry is null)
                    continue;
                order++;
                history.Add(entry.Value.Track);
                var deck = DeckMapping.FromSeratoDeck(entry.Value.Deck);
                if (deck is null)
                    continue;
                // Older files may lack a start time, so fall back to file order.
                var loadedAt = entry.Value.StartTime ?? DateTimeOffset.FromUnixTimeSeconds(order);
                if (!decks.TryGetValue(deck.Value, out var existing) || loadedAt >= existing.LoadedAt)
                    decks[deck.Value] = new DeckTrack(deck.Value, entry.Value.Track, loadedAt);
            }
        }

        return new DeckSnapshot(decks, history);
    }

    private static (TrackInfo Track, long Deck, DateTimeOffset? StartTime)? ParseAdat(ReadOnlySpan<byte> data, int offset)
    {
        string? title = null, artist = null, genre = null, key = null;
        double? bpm = null;
        long deck = 0;
        DateTimeOffset? start = null;

        var i = offset;
        while (i + 8 <= data.Length)
        {
            var field = BinaryPrimitives.ReadUInt32BigEndian(data[i..]);
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(i + 4)..]);
            if (length < 0 || i + 8 + length > data.Length)
                break;
            var value = data.Slice(i + 8, length);
            switch (field)
            {
                case FieldTitle: title = Utf16(value); break;
                case FieldArtist: artist = Utf16(value); break;
                case FieldGenre: genre = Utf16(value); break;
                case FieldKey: key = Utf16(value); break;
                case FieldBpm:
                    var raw = Unsigned(value);
                    bpm = raw > 0 ? raw : null;
                    break;
                case FieldDeck: deck = (long)Unsigned(value); break;
                case FieldStartTime:
                    var t = Unsigned(value);
                    start = t > 0 ? SeratoTime.FromUnknownEpoch(t) : null;
                    break;
            }
            i += 8 + length;
        }

        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(artist))
            return null;

        var track = new TrackInfo
        {
            Artist = artist ?? "",
            Title = title ?? "",
            Genre = string.IsNullOrWhiteSpace(genre) ? null : genre,
            Bpm = bpm,
            Key = string.IsNullOrWhiteSpace(key) ? null : key,
        };
        return (track, deck, start);
    }

    private static List<(string Tag, byte[] Payload)> Chunks(ReadOnlySpan<byte> data)
    {
        var result = new List<(string, byte[])>();
        var i = 0;
        while (i + 8 <= data.Length)
        {
            var tag = Encoding.ASCII.GetString(data.Slice(i, 4));
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(i + 4)..]);
            if (length < 0 || i + 8 + length > data.Length)
                break;
            result.Add((tag, data.Slice(i + 8, length).ToArray()));
            i += 8 + length;
        }
        return result;
    }

    private static string Utf16(ReadOnlySpan<byte> value) =>
        Encoding.BigEndianUnicode.GetString(value).TrimEnd('\0').Trim();

    private static double Unsigned(ReadOnlySpan<byte> value) => value.Length switch
    {
        1 => value[0],
        2 => BinaryPrimitives.ReadUInt16BigEndian(value),
        4 => BinaryPrimitives.ReadUInt32BigEndian(value),
        8 => BinaryPrimitives.ReadUInt64BigEndian(value),
        _ => 0,
    };
}
