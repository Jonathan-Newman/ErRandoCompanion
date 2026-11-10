using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace ErRandoCompanion.Core.Persistence;

/// <summary>
/// The game's own save, copied alongside a run's backups so a run can be put back together with the character it was
/// played on. The save in use is the newest ER0000.* in %APPDATA%\EldenRing\&lt;Steam id&gt;: the randomizer's .RD2, the
/// normal .sl2, or Seamless Co-op's .co2, whichever the game wrote last. Copies are compressed (29 MB becomes about 1.5).
/// </summary>
public static class GameSaves
{
    /// <summary>For tests: a folder to look in instead of %APPDATA%\EldenRing.</summary>
    public const string FolderOverride = "ER_COMPANION_SAVES_DIR";

    public const string CopyExtension = ".save.gz";
    public const string InfoExtension = ".save.json";

    /// <summary>The save the game wrote last, or null when there is none.</summary>
    public static string? FindActive() => FindActive(Environment.GetEnvironmentVariable(FolderOverride)
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EldenRing"));

    /// <summary>The save the game wrote last under <paramref name="root"/>, or null when there is none.</summary>
    public static string? FindActive(string root)
    {
        try
        {
            if (!Directory.Exists(root))
                return null;
            return new DirectoryInfo(root)
                .EnumerateFiles("ER0000.*", SearchOption.AllDirectories)
                // The game's and the randomizer's own spares, and other tools' copies, are not the save.
                .Where(f => f.Name.Count(c => c == '.') == 1)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Copies a save, compressed, to <paramref name="copyPath"/>, and notes where it came from in <paramref name="infoPath"/>.
    /// Read while the game may be writing it, so a copy taken mid-write is taken again. Returns false when it could not be read.
    /// </summary>
    public static bool Copy(string savePath, string copyPath, string infoPath)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var before = File.GetLastWriteTimeUtc(savePath);
                byte[] data;
                using (var file = new FileStream(savePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    data = new byte[file.Length];
                    file.ReadExactly(data);
                }
                if (File.GetLastWriteTimeUtc(savePath) != before)
                    continue;
                using (var output = File.Create(copyPath))
                using (var zip = new GZipStream(output, CompressionLevel.Fastest))
                    zip.Write(data);
                var info = new SaveCopyInfo(savePath, before, data.Length);
                File.WriteAllText(infoPath, JsonSerializer.Serialize(info, SaveCopyJsonContext.Default.SaveCopyInfo));
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Locked for a moment; try again.
            }
        }
        return false;
    }

    /// <summary>Where a copy came from, or null when its note is missing or damaged.</summary>
    public static SaveCopyInfo? InfoOf(string infoPath)
    {
        try
        {
            return File.Exists(infoPath) ? JsonSerializer.Deserialize(File.ReadAllText(infoPath), SaveCopyJsonContext.Default.SaveCopyInfo) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Puts a copy back over the save it came from. What is there now is first kept as "&lt;save&gt;.before-restore", so this
    /// can be undone by hand. Only call with the game closed: it rewrites the save on exit.
    /// </summary>
    /// <exception cref="IOException">The copy cannot be read or the save cannot be written.</exception>
    public static void Restore(string copyPath, SaveCopyInfo info)
    {
        byte[] data;
        using (var input = File.OpenRead(copyPath))
        using (var zip = new GZipStream(input, CompressionMode.Decompress))
        using (var buffer = new MemoryStream())
        {
            zip.CopyTo(buffer);
            data = buffer.ToArray();
        }
        if (data.Length != info.Length)
            throw new IOException($"The copy of {Path.GetFileName(info.Path)} is damaged (it holds {data.Length} bytes, not {info.Length}).");
        if (File.Exists(info.Path))
            File.Copy(info.Path, info.Path + ".before-restore", overwrite: true);
        var temp = info.Path + ".restoring";
        File.WriteAllBytes(temp, data);
        File.Move(temp, info.Path, overwrite: true);
    }
}

/// <summary>Where a save copy came from: the save's path, when the game last wrote it, and its size.</summary>
public sealed record SaveCopyInfo(string Path, DateTime WrittenAt, long Length);

[System.Text.Json.Serialization.JsonSerializable(typeof(SaveCopyInfo))]
public sealed partial class SaveCopyJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
