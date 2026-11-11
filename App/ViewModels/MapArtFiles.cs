using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ErRandoCompanion.Core.MapArt;
using Microsoft.Win32;

namespace ErRandoCompanion.App.ViewModels;

/// <summary>
/// The world map pictures. They are FromSoftware's art, so the app never ships them: each player makes them from their
/// own copy of the game, and they are kept in the app's folder under %LOCALAPPDATA%.
/// </summary>
public sealed partial class MapArtFiles(string artDirectory, string dataDirectory)
{
    public string ArtDirectory => artDirectory;

    /// <summary>Where a picture is: the player's own copy first, then a developer build's Data folder. Null when missing.</summary>
    public string? Find(string fileName)
    {
        foreach (var folder in new[] { artDirectory, dataDirectory })
        {
            var path = Path.Combine(folder, fileName);
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    public bool HasAll => MapArtExtractor.Sheets.All(s => Find(s.FileName) is not null);

    /// <summary>Builds every picture from the game folder. Runs off the UI thread; progress is 0 to 1.</summary>
    public void Extract(string gameFolder, Action<string, double> progress)
    {
        Directory.CreateDirectory(artDirectory);
        using var extractor = new MapArtExtractor(gameFolder);
        var sheets = MapArtExtractor.Sheets;
        for (var i = 0; i < sheets.Count; i++)
        {
            var sheet = sheets[i];
            var step = i;
            var image = extractor.Build(sheet, p => progress($"Stitching map {step + 1} of {sheets.Count}", (step + p * 0.9) / sheets.Count));
            progress($"Saving map {step + 1} of {sheets.Count}", (step + 0.95) / sheets.Count);

            var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgr24, null, image.Bgr, image.Width * 3);
            var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(artDirectory, sheet.FileName);
            var partial = path + ".partial";
            using (var file = File.Create(partial))
                encoder.Save(file);
            File.Move(partial, path, overwrite: true);
        }
        progress("Done", 1);
    }

    /// <summary>Loads a picture without keeping the file open, so it can be made again while the app runs.</summary>
    public static ImageSource? Load(string? path)
    {
        if (path is null)
            return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Looks for the Elden Ring game folder: the running game, the folder above the randomizer, then Steam's libraries.
    /// </summary>
    public static string? FindGameFolder(string? randomizerDirectory)
    {
        foreach (var candidate in Candidates(randomizerDirectory))
        {
            if (MapArtExtractor.IsGameFolder(candidate))
                return candidate;
        }
        return null;
    }

    private static IEnumerable<string?> Candidates(string? randomizerDirectory)
    {
        foreach (var process in Process.GetProcessesByName("eldenring"))
        {
            string? folder = null;
            try
            {
                folder = Path.GetDirectoryName(process.MainModule?.FileName);
            }
            catch (Exception)
            {
                // Access to another process can be refused; the other places are still worth trying.
            }
            process.Dispose();
            yield return folder;
        }

        if (randomizerDirectory is not null)
        {
            yield return Path.GetDirectoryName(randomizerDirectory);
            yield return randomizerDirectory;
        }

        foreach (var library in SteamLibraries())
            yield return Path.Combine(library, "steamapps", "common", "ELDEN RING", "Game");
    }

    private static IEnumerable<string> SteamLibraries()
    {
        string? steam = null;
        try
        {
            steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        }
        catch (Exception)
        {
            // No Steam, or the registry could not be read.
        }

        var libraries = new List<string>();
        if (steam is not null)
        {
            libraries.Add(steam);
            var list = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(list))
            {
                foreach (Match m in LibraryPath().Matches(File.ReadAllText(list)))
                    libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            }
        }
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
        {
            libraries.Add(Path.Combine(drive.Name, "SteamLibrary"));
            libraries.Add(Path.Combine(drive.Name, "Program Files (x86)", "Steam"));
        }
        return libraries.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex LibraryPath();
}
