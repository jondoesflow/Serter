using System.Text.Json;
using Serter.Core.Models;

namespace Serter.Core;

/// <summary>User settings, stored as JSON in %APPDATA%\Serter\settings.json.</summary>
public sealed class AppSettings
{
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Serter");

    public static string FilePath => Path.Combine(Folder, "settings.json");

    public string GetSongBpmApiKey { get; set; } = "";
    public string LastFmApiKey { get; set; } = "";

    /// <summary>Blank = auto-detect. Otherwise a master.sqlite file, a _Serato_ folder or a Sessions folder.</summary>
    public string SeratoPathOverride { get; set; } = "";

    /// <summary>What the Copy button puts on the clipboard. {artist} and {title} are replaced.</summary>
    public string CopyFormat { get; set; } = "{artist} {title}";

    public Deck SelectedDeck { get; set; } = Deck.A;

    /// <summary>Switch to whichever deck had a track loaded most recently.</summary>
    public bool FollowLatestDeck { get; set; }

    public bool AlwaysOnTop { get; set; } = true;
    public double BpmTolerancePercent { get; set; } = 6;
    public int MaxResults { get; set; } = 20;
    public bool HideKeyClashes { get; set; }
    public bool HidePlayedThisSession { get; set; } = true;

    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Fall back to defaults if the file is unreadable.
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }

    public string FormatForCopy(TrackInfo track)
    {
        var format = string.IsNullOrWhiteSpace(CopyFormat) ? "{artist} {title}" : CopyFormat;
        return format
            .Replace("{artist}", track.Artist, StringComparison.OrdinalIgnoreCase)
            .Replace("{title}", track.Title, StringComparison.OrdinalIgnoreCase)
            .Trim();
    }
}
