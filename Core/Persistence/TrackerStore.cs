using System.Globalization;
using System.IO;
using System.Text.Json;
using ErRandoCompanion.Core.Json;
using ErRandoCompanion.Core.Models;

namespace ErRandoCompanion.Core.Persistence;

public sealed class TrackerStoreException(string message, Exception inner) : Exception(message, inner);

/// <summary>A saved copy of a run from an earlier moment.</summary>
/// <param name="Id">Identifies the copy within its run; pass it back to restore it.</param>
/// <param name="Checked">How many checks were ticked in it, so copies are easy to tell apart.</param>
public sealed record RunBackup(string SeedName, string Id, DateTime SavedAt, int Checked)
{
    /// <summary>Whether the game's save was copied with it (see <see cref="GameSaves"/>).</summary>
    public bool HasGameSave { get; init; }
}

/// <summary>
/// Saves each seed's tracker state as its own JSON file, and keeps recent copies of each in Backups\&lt;run&gt;\:
/// one at most every <see cref="BackupEvery"/> while it is played, one before it is deleted or restored over,
/// and the last <see cref="KeepBackups"/> of each. With <paramref name="gameSave"/>, each copy also keeps the game's save
/// as it was then.
/// </summary>
/// <param name="gameSave">Finds the game's save to copy with each backup; null to copy none (tests).</param>
public sealed class TrackerStore(string directory, Func<string?>? gameSave = null)
{
    private const string Extension = ".seed.json";
    private const string BackupFolder = "Backups";
    private const string StampFormat = "yyyyMMdd-HHmmss-fff";
    public static readonly TimeSpan BackupEvery = TimeSpan.FromMinutes(10);
    public const int KeepBackups = 10;

    /// <summary>Known seeds, most recently saved first.</summary>
    public IReadOnlyList<string> SeedNames()
    {
        if (!Directory.Exists(directory))
            return [];

        return new DirectoryInfo(directory).GetFiles("*" + Extension)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => Uri.UnescapeDataString(f.Name[..^Extension.Length]))
            .ToList();
    }

    /// <summary>Returns null when this seed has never been saved.</summary>
    /// <exception cref="TrackerStoreException">The file exists but cannot be read.</exception>
    public TrackerState? Load(string seedName)
    {
        var path = PathFor(seedName);
        if (!File.Exists(path))
            return null;

        var state = Read(path);
        state.SeedName = seedName;
        return state;
    }

    /// <exception cref="TrackerStoreException">The file cannot be written.</exception>
    public void Save(TrackerState state)
    {
        var path = PathFor(state.SeedName);
        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            // The copy is of what is on disk now, so it is always a moment the player actually had.
            if (File.Exists(path) && (NewestBackupAge(state.SeedName) is not { } age || age >= BackupEvery))
                Backup(state.SeedName);

            // Written aside first, so a crash mid-save cannot damage existing progress.
            File.WriteAllText(temp, JsonSerializer.Serialize(state, SaveJsonContext.Default.TrackerState));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new TrackerStoreException($"Could not save {path}: {e.Message}", e);
        }
    }

    /// <summary>Removes a seed's save, keeping a backup of it first. Returns false when it was never saved.</summary>
    /// <exception cref="TrackerStoreException">The file exists but cannot be removed.</exception>
    public bool Delete(string seedName)
    {
        var path = PathFor(seedName);
        if (!File.Exists(path))
            return false;

        try
        {
            Backup(seedName);
            File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new TrackerStoreException($"Could not delete {path}: {e.Message}", e);
        }
    }

    /// <summary>A run's backups, newest first. Unreadable copies are left out.</summary>
    public IReadOnlyList<RunBackup> Backups(string seedName)
    {
        var folder = BackupsOf(seedName);
        if (!Directory.Exists(folder))
            return [];
        var backups = new List<RunBackup>();
        foreach (var file in new DirectoryInfo(folder).GetFiles("*" + Extension).OrderByDescending(f => f.Name))
        {
            try
            {
                backups.Add(new RunBackup(seedName, file.Name, StampOf(file), Read(file.FullName).CheckedLocations.Count)
                {
                    HasGameSave = File.Exists(SaveCopyOf(file.FullName)),
                });
            }
            catch (TrackerStoreException)
            {
                // A damaged copy is no use to restore.
            }
        }
        return backups;
    }

    /// <summary>Every run that has backups, including deleted ones, so a deleted run can be brought back.</summary>
    public IReadOnlyList<string> RunsWithBackups()
    {
        var root = Path.Combine(directory, BackupFolder);
        if (!Directory.Exists(root))
            return [];
        return new DirectoryInfo(root).GetDirectories()
            .Where(d => d.EnumerateFiles("*" + Extension).Any())
            .Select(d => Uri.UnescapeDataString(d.Name))
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Puts a backup back as the run's save, after backing up what is there now (so a restore can be undone),
    /// and returns the restored state.
    /// </summary>
    /// <exception cref="TrackerStoreException">The backup is missing or damaged, or the save cannot be written.</exception>
    public TrackerState Restore(string seedName, string backupId)
    {
        var source = Path.Combine(BackupsOf(seedName), Path.GetFileName(backupId));
        if (!File.Exists(source))
            throw new TrackerStoreException($"There is no backup called {backupId} for {seedName}.", new FileNotFoundException(source));
        var state = Read(source);
        state.SeedName = seedName;
        try
        {
            if (File.Exists(PathFor(seedName)))
                Backup(seedName);
            Directory.CreateDirectory(directory);
            File.Copy(source, PathFor(seedName), overwrite: true);
            // Restored is the newest state: mark it so, or the run sorts by the backup's old date.
            File.SetLastWriteTimeUtc(PathFor(seedName), DateTime.UtcNow);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new TrackerStoreException($"Could not restore {seedName}: {e.Message}", e);
        }
        return state;
    }

    private void Backup(string seedName)
    {
        var folder = BackupsOf(seedName);
        Directory.CreateDirectory(folder);
        var stamp = DateTime.UtcNow.ToString(StampFormat, CultureInfo.InvariantCulture);
        var copy = Path.Combine(folder, stamp + Extension);
        File.Copy(PathFor(seedName), copy, overwrite: true);
        // The game's save with it, so the run and its character can be put back together.
        if (gameSave?.Invoke() is { } save)
            GameSaves.Copy(save, SaveCopyOf(copy), SaveInfoOf(copy));

        foreach (var old in new DirectoryInfo(folder).GetFiles("*" + Extension).OrderByDescending(f => f.Name).Skip(KeepBackups))
        {
            old.Delete();
            File.Delete(SaveCopyOf(old.FullName));
            File.Delete(SaveInfoOf(old.FullName));
        }
    }

    /// <summary>The game save kept with a backup: the compressed copy and where it came from, or null when it has none.</summary>
    public (string Copy, SaveCopyInfo Info)? GameSaveOf(string seedName, string backupId)
    {
        var backup = Path.Combine(BackupsOf(seedName), Path.GetFileName(backupId));
        var copy = SaveCopyOf(backup);
        return File.Exists(copy) && GameSaves.InfoOf(SaveInfoOf(backup)) is { } info ? (copy, info) : null;
    }

    private static string SaveCopyOf(string backupPath) => backupPath[..^Extension.Length] + GameSaves.CopyExtension;

    private static string SaveInfoOf(string backupPath) => backupPath[..^Extension.Length] + GameSaves.InfoExtension;

    private TimeSpan? NewestBackupAge(string seedName)
    {
        var folder = BackupsOf(seedName);
        if (!Directory.Exists(folder))
            return null;
        var newest = new DirectoryInfo(folder).GetFiles("*" + Extension).OrderByDescending(f => f.Name).FirstOrDefault();
        return newest is null ? null : DateTime.UtcNow - StampOf(newest);
    }

    /// <summary>When a backup was made, from its name (UTC), falling back to the file's own time.</summary>
    private static DateTime StampOf(FileInfo file) =>
        DateTime.TryParseExact(file.Name[..^Extension.Length], StampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : file.LastWriteTimeUtc;

    private static TrackerState Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), SaveJsonContext.Default.TrackerState)
                ?? throw new JsonException("The file contains no tracker state.");
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new TrackerStoreException($"Could not read {path}: {e.Message}", e);
        }
    }

    // Escaped rather than stripped, so distinct names like "a/b" and "a_b" get distinct files.
    private string PathFor(string seedName) =>
        Path.Combine(directory, Uri.EscapeDataString(seedName) + Extension);

    private string BackupsOf(string seedName) =>
        Path.Combine(directory, BackupFolder, Uri.EscapeDataString(seedName));
}
