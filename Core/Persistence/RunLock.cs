using System.IO;
using System.Text;

namespace ErRandoCompanion.Core.Persistence;

/// <summary>
/// Says that one front end (the desktop app or the in-game overlay) owns a run. Ownership is an open handle
/// on "&lt;run&gt;.lock" beside the run's save, so it ends by itself when the owner closes, crashes or is killed:
/// a lock can never be left stuck. The file holds a short description of the owner for the other to show.
/// </summary>
public sealed class RunLock : IDisposable
{
    private const string Extension = ".lock";
    private readonly FileStream _stream;
    private readonly string _path;

    private RunLock(FileStream stream, string path)
    {
        _stream = stream;
        _path = path;
    }

    /// <summary>Takes the run for <paramref name="owner"/>, or returns null when something else holds it.</summary>
    public static RunLock? TryAcquire(string directory, string seedName, string owner)
    {
        var path = PathFor(directory, seedName);
        try
        {
            Directory.CreateDirectory(directory);
            // Others may read who the owner is, but not open it for writing, which is what taking it means.
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            stream.SetLength(0);
            var text = Encoding.UTF8.GetBytes(owner);
            stream.Write(text, 0, text.Length);
            stream.Flush();
            return new RunLock(stream, path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Who holds the run, as its owner described itself; null when nobody does.</summary>
    public static string? Holder(string directory, string seedName)
    {
        // Free to take means nobody holds it.
        using (var probe = TryAcquire(directory, seedName, "nobody"))
        {
            if (probe is not null)
                return null;
        }
        try
        {
            using var stream = new FileStream(PathFor(directory, seedName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var owner = reader.ReadToEnd().Trim();
            return owner.Length > 0 ? owner : "another program";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "another program";
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
        try
        {
            File.Delete(_path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Someone else has just taken it, or is reading who owned it; the handle is what mattered.
        }
    }

    private static string PathFor(string directory, string seedName) =>
        Path.Combine(directory, Uri.EscapeDataString(seedName) + Extension);
}
