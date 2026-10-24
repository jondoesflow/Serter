using Microsoft.Data.Sqlite;
using Serter.Core.Models;

namespace Serter.Core.Serato;

/// <summary>
/// Reads Serato DJ 4+'s library database (master.sqlite). Serato adds a history_entry row for the
/// current session as soon as a track is loaded onto a deck — including TIDAL and other streaming
/// tracks — with the BPM and key it has analysed.
/// </summary>
public sealed class Serato4DatabaseSource : IDeckSource
{
    private readonly string _databasePath;

    public Serato4DatabaseSource(string databasePath)
    {
        _databasePath = databasePath;
    }

    public string Description => $"Serato DJ 4 library ({_databasePath})";

    public DeckSnapshot Read() => Query(ReadDecks, DeckSnapshot.Empty);

    public IReadOnlyList<TrackInfo> ReadAllHistory() => Query(ReadHistory, []);

    private T Query<T>(Func<SqliteConnection, T> query, T empty)
    {
        if (!File.Exists(_databasePath))
            return empty;

        try
        {
            return Run(_databasePath, query);
        }
        catch (SqliteException)
        {
            // Serato may hold a lock mid-write; read a private copy instead.
            return Run(CopyDatabase(), query);
        }
    }

    private string CopyDatabase()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "Serter", "dbcopy");
        Directory.CreateDirectory(tempDir);
        var copy = Path.Combine(tempDir, "master.sqlite");
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var source = _databasePath + suffix;
            var target = copy + suffix;
            if (File.Exists(source))
                CopyShared(source, target);
            else if (File.Exists(target))
                File.Delete(target);
        }
        return copy;
    }

    private static void CopyShared(string source, string target)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }

    internal static T Run<T>(string path, Func<SqliteConnection, T> query)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return query(connection);
    }

    internal static DeckSnapshot ReadDecks(SqliteConnection connection)
    {
        var sessionId = CurrentSessionId(connection);
        if (sessionId is null)
            return DeckSnapshot.Empty;

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT artist, name, genre, bpm, key, deck, start_time
            FROM history_entry
            WHERE session_id = $session
            ORDER BY start_time ASC
            """;
        command.Parameters.AddWithValue("$session", sessionId);

        var decks = new Dictionary<Deck, DeckTrack>();
        var history = new List<TrackInfo>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var track = new TrackInfo
            {
                Artist = GetString(reader, 0) ?? "",
                Title = GetString(reader, 1) ?? "",
                Genre = GetString(reader, 2),
                Bpm = GetDouble(reader, 3),
                Key = GetString(reader, 4),
            };
            if (track.Artist.Length == 0 && track.Title.Length == 0)
                continue;

            history.Add(track);

            var deck = DeckMapping.FromSeratoDeck((long)(GetDouble(reader, 5) ?? 0));
            if (deck is null)
                continue;
            var loadedAt = SeratoTime.FromUnknownEpoch(GetDouble(reader, 6) ?? 0);
            if (!decks.TryGetValue(deck.Value, out var existing) || loadedAt >= existing.LoadedAt)
                decks[deck.Value] = new DeckTrack(deck.Value, track, loadedAt);
        }

        return new DeckSnapshot(decks, history);
    }

    internal static IReadOnlyList<TrackInfo> ReadHistory(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT artist, name, genre, bpm, key
            FROM history_entry
            WHERE bpm > 0 AND key IS NOT NULL AND key <> ''
            ORDER BY start_time DESC
            LIMIT 5000
            """;
        var tracks = new List<TrackInfo>();
        var seen = new HashSet<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var track = new TrackInfo
            {
                Artist = GetString(reader, 0) ?? "",
                Title = GetString(reader, 1) ?? "",
                Genre = GetString(reader, 2),
                Bpm = GetDouble(reader, 3),
                Key = GetString(reader, 4),
            };
            if (track.Artist.Length > 0 && track.Title.Length > 0 && seen.Add(track.MatchKey))
                tracks.Add(track);
        }
        return tracks;
    }

    private static object? CurrentSessionId(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        // An open session has end_time = -1; fall back to the newest session if none is open.
        command.CommandText = """
            SELECT id FROM history_session
            ORDER BY (end_time = -1) DESC, start_time DESC
            LIMIT 1
            """;
        return command.ExecuteScalar();
    }

    private static string? GetString(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;
        var value = Convert.ToString(reader.GetValue(ordinal))?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static double? GetDouble(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;
        var value = reader.GetValue(ordinal);
        return value switch
        {
            long l => l,
            double d => d,
            string s when double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }
}

internal static class SeratoTime
{
    /// <summary>Serato timestamps are Unix time, in seconds or milliseconds depending on version.</summary>
    public static DateTimeOffset FromUnknownEpoch(double value)
    {
        if (value <= 0)
            return DateTimeOffset.MinValue;
        if (value > 100_000_000_000) // milliseconds
            return DateTimeOffset.FromUnixTimeMilliseconds((long)value);
        return DateTimeOffset.FromUnixTimeSeconds((long)value);
    }
}
