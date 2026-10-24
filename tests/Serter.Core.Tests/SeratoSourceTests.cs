using System.Buffers.Binary;
using System.Text;
using Microsoft.Data.Sqlite;
using Serter.Core.Models;
using Serter.Core.Serato;

namespace Serter.Core.Tests;

public class SeratoSourceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "serter-tests-" + Guid.NewGuid().ToString("N"));

    public SeratoSourceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Serato4_database_reports_latest_track_per_deck()
    {
        var db = Path.Combine(_dir, "master.sqlite");
        using (var c = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            c.Open();
            Exec(c, "CREATE TABLE history_session (id INTEGER PRIMARY KEY, start_time INTEGER, end_time INTEGER)");
            Exec(c, """
                CREATE TABLE history_entry (id INTEGER PRIMARY KEY, session_id INTEGER, artist TEXT, name TEXT,
                genre TEXT, bpm REAL, key TEXT, deck INTEGER, start_time INTEGER, played INTEGER)
                """);
            Exec(c, "INSERT INTO history_session VALUES (1, 1000, 2000), (2, 3000, -1)");
            Exec(c, "INSERT INTO history_entry VALUES (1, 1, 'Old', 'Gone', 'House', 120, 'Am', 1, 1500, 1)");
            Exec(c, "INSERT INTO history_entry VALUES (2, 2, 'Bicep', 'Glue', 'Electronic', 130, 'Fm', 1, 3100, 1)");
            Exec(c, "INSERT INTO history_entry VALUES (3, 2, 'Floating Points', 'Silhouettes', 'Electronic', 125.5, 'Dm', 2, 3200, 0)");
            Exec(c, "INSERT INTO history_entry VALUES (4, 2, 'Overmono', 'So U Kno', 'Garage', 131, '4A', 1, 3300, 0)");
        }

        var source = new Serato4DatabaseSource(db);
        var snapshot = source.Read();

        Assert.Equal("Overmono", snapshot.Decks[Deck.A].Track.Artist);
        Assert.Equal("So U Kno", snapshot.Decks[Deck.A].Track.Title);
        Assert.Equal("Silhouettes", snapshot.Decks[Deck.B].Track.Title);
        Assert.Equal(125.5, snapshot.Decks[Deck.B].Track.Bpm);
        Assert.Equal("7A", snapshot.Decks[Deck.B].Track.Camelot?.Code);
        Assert.Equal(3, snapshot.SessionHistory.Count);

        var all = source.ReadAllHistory();
        Assert.Equal(4, all.Count);
    }

    [Fact]
    public void Legacy_session_file_reports_latest_track_per_deck()
    {
        var file = Path.Combine(_dir, "123.session");
        var data = Chunk("vrsn", Encoding.BigEndianUnicode.GetBytes("1.0/Serato Scratch LIVE Review"))
            .Concat(Entry("Bicep", "Glue", "Electronic", 130, "Fm", deck: 1, start: 1_700_000_000))
            .Concat(Entry("Floating Points", "Silhouettes", "Electronic", 125, "Dm", deck: 2, start: 1_700_000_100))
            .Concat(Entry("Overmono", "So U Kno", "Garage", 131, "4A", deck: 1, start: 1_700_000_200))
            .ToArray();
        File.WriteAllBytes(file, data);

        var snapshot = new SeratoSessionFileSource(_dir).Read();

        Assert.Equal("So U Kno", snapshot.Decks[Deck.A].Track.Title);
        Assert.Equal("Silhouettes", snapshot.Decks[Deck.B].Track.Title);
        Assert.Equal(125, snapshot.Decks[Deck.B].Track.Bpm);
        Assert.Equal("Garage", snapshot.Decks[Deck.A].Track.Genre);
        Assert.Equal(3, snapshot.SessionHistory.Count);
    }

    [Fact]
    public void Deck_monitor_raises_only_on_change()
    {
        var file = Path.Combine(_dir, "1.session");
        File.WriteAllBytes(file, Entry("Bicep", "Glue", "Electronic", 130, "Fm", 1, 1_700_000_000).ToArray());
        var loads = new List<DeckTrack>();
        using var monitor = new DeckMonitor(() => new SeratoSessionFileSource(_dir), TimeSpan.FromHours(1));
        monitor.TrackLoaded += (_, t) => loads.Add(t);

        monitor.Poll();
        monitor.Poll();
        Assert.Single(loads);

        File.WriteAllBytes(file, Entry("Bicep", "Glue", "Electronic", 130, "Fm", 1, 1_700_000_000)
            .Concat(Entry("Overmono", "So U Kno", "Garage", 131, "4A", 2, 1_700_000_100)).ToArray());
        monitor.Poll();
        Assert.Equal(2, loads.Count);
        Assert.Equal(Deck.B, loads[1].Deck);
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static IEnumerable<byte> Entry(string artist, string title, string genre, uint bpm, string key, uint deck, uint start)
    {
        var adat = new List<byte>();
        adat.AddRange(Field(1, U32(7)));
        adat.AddRange(Field(6, Utf16(title)));
        adat.AddRange(Field(7, Utf16(artist)));
        adat.AddRange(Field(9, Utf16(genre)));
        adat.AddRange(Field(15, U32(bpm)));
        adat.AddRange(Field(28, U32(start)));
        adat.AddRange(Field(31, U32(deck)));
        adat.AddRange(Field(51, Utf16(key)));
        return Chunk("oent", Chunk("adat", adat.ToArray()).ToArray());
    }

    private static byte[] Utf16(string s) => Encoding.BigEndianUnicode.GetBytes(s + "\0");

    private static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b;
    }

    private static IEnumerable<byte> Field(uint id, byte[] value) => U32(id).Concat(U32((uint)value.Length)).Concat(value);

    private static IEnumerable<byte> Chunk(string tag, byte[] value) =>
        Encoding.ASCII.GetBytes(tag).Concat(U32((uint)value.Length)).Concat(value);
}
