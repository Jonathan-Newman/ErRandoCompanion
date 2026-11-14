using System.IO;
using System.Text.Json;
using ErRandoCompanion.Core.Json;

namespace ErRandoCompanion.Core.Persistence;

/// <summary>Settings that belong to the player rather than to any one seed.</summary>
public sealed class Preferences
{
    /// <summary>The randomizer folder watched for new seeds.</summary>
    public string? RandomizerDirectory { get; set; }

    /// <summary>The newest spoiler log already offered to the player, so declining a seed is not asked again.</summary>
    public string? LastOfferedLog { get; set; }

    /// <summary>Whether to look on GitHub for a newer version of the companion (at most about once a day).</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>When the last look for updates was, so it is not done more than about once a day.</summary>
    public DateTime? LastUpdateCheck { get; set; }

    /// <summary>A version the player chose not to install ("Not now"), so it is not offered again; a newer one is.</summary>
    public string? SkippedVersion { get; set; }

    /// <summary>The newest release found at the last look ("v1.4.0") and its page, so the app and the overlay both know.</summary>
    public string? LatestVersion { get; set; }

    public string? LatestPage { get; set; }

    /// <summary>Missing or unreadable preferences are not worth bothering the player about, so they start fresh.</summary>
    public static Preferences Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path), SaveJsonContext.Default.Preferences) ?? new()
                : new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    /// <returns>False when the file could not be written; preferences then only last until the app closes.</returns>
    public bool Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, SaveJsonContext.Default.Preferences));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
