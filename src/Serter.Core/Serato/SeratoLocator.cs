namespace Serter.Core.Serato;

/// <summary>Finds where Serato keeps its history on this PC.</summary>
public static class SeratoLocator
{
    public static IEnumerable<string> DatabaseCandidates()
    {
        foreach (var root in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            var dir = Environment.GetFolderPath(root);
            if (!string.IsNullOrEmpty(dir))
                yield return Path.Combine(dir, "Serato", "Library", "master.sqlite");
        }
        var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (!string.IsNullOrEmpty(music))
            yield return Path.Combine(music, "Serato", "Library", "master.sqlite");
    }

    public static IEnumerable<string> SessionDirectoryCandidates()
    {
        var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (!string.IsNullOrEmpty(music))
            yield return Path.Combine(music, "_Serato_", "History", "Sessions");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
            yield return Path.Combine(profile, "Music", "_Serato_", "History", "Sessions");
    }

    /// <summary>
    /// Picks the source Serato wrote to most recently. <paramref name="overridePath"/> may point at a
    /// master.sqlite file, a Sessions folder, or a _Serato_ folder.
    /// </summary>
    public static IDeckSource? Locate(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
            return FromPath(overridePath.Trim());

        var options = new List<(DateTime Written, IDeckSource Source)>();
        foreach (var db in DatabaseCandidates().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(db))
                options.Add((LastWrite(db), new Serato4DatabaseSource(db)));
        }
        foreach (var dir in SessionDirectoryCandidates().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var latest = SeratoSessionFileSource.LatestSessionFile(dir);
            if (latest is not null)
                options.Add((latest.LastWriteTimeUtc, new SeratoSessionFileSource(dir)));
        }
        return options.OrderByDescending(o => o.Written).Select(o => o.Source).FirstOrDefault();
    }

    private static IDeckSource? FromPath(string path)
    {
        if (File.Exists(path))
            return path.EndsWith(".session", StringComparison.OrdinalIgnoreCase)
                ? new SeratoSessionFileSource(Path.GetDirectoryName(path)!)
                : new Serato4DatabaseSource(path);

        if (!Directory.Exists(path))
            return null;
        var db = Path.Combine(path, "master.sqlite");
        if (File.Exists(db))
            return new Serato4DatabaseSource(db);
        var sessions = Path.Combine(path, "History", "Sessions");
        return new SeratoSessionFileSource(Directory.Exists(sessions) ? sessions : path);
    }

    private static DateTime LastWrite(string db)
    {
        // With WAL journaling the -wal file changes on every write, not the main file.
        var wal = db + "-wal";
        var main = File.GetLastWriteTimeUtc(db);
        return File.Exists(wal) ? Max(main, File.GetLastWriteTimeUtc(wal)) : main;
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
