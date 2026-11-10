using System.IO;

namespace ErRandoCompanion.Core.SpoilerLog;

/// <summary>Finds the randomizer's spoiler logs, so a new seed can be picked up without browsing for it.</summary>
public static class SpoilerLogLocator
{
    public const string LogsFolder = "spoiler_logs";

    /// <summary>The randomizer folder a log belongs to: logs are written to &lt;randomizer&gt;/spoiler_logs.</summary>
    public static string? RandomizerDirectoryOf(string logPath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(logPath));
        return folder is not null && Path.GetFileName(folder).Equals(LogsFolder, StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(folder)
            : null;
    }

    /// <summary>
    /// The most recently written log in this randomizer folder, or null when there is none. Each randomize run writes
    /// a new log alongside the regulation.bin it describes, so the newest log is the seed currently installed.
    /// </summary>
    public static string? FindNewest(string randomizerDirectory)
    {
        var logs = new DirectoryInfo(Path.Combine(randomizerDirectory, LogsFolder));
        if (!logs.Exists)
            return null;

        try
        {
            return logs.GetFiles("*.txt").MaxBy(f => f.LastWriteTimeUtc)?.FullName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
