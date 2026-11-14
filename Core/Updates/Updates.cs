using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace ErRandoCompanion.Core.Updates;

/// <summary>A file of a release, with the SHA-256 GitHub publishes for it ("sha256:..."), when it does.</summary>
public sealed record ReleaseAsset(string Name, string Url, long Size, string? Digest);

/// <summary>A published release of the companion on GitHub.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string Page, string Notes, IReadOnlyList<ReleaseAsset> Assets)
{
    /// <summary>The desktop app's zip ("ErRandoCompanion-1.4.0-app.zip").</summary>
    public ReleaseAsset? App => Assets.FirstOrDefault(a => a.Name.EndsWith("-app.zip", StringComparison.OrdinalIgnoreCase));

    /// <summary>The overlay's zip ("ErRandoCompanionMod-1.4.0.zip").</summary>
    public ReleaseAsset? Mod => Assets.FirstOrDefault(a => a.Name.StartsWith("ErRandoCompanionMod", StringComparison.OrdinalIgnoreCase));
}

public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Finds, downloads and installs new releases from the project's GitHub page. Only ever talks to GitHub, sends nothing
/// about the player, and installs nothing unless asked. Downloads are checked against the SHA-256 GitHub publishes,
/// and one without it is refused.
/// </summary>
public static class Updates
{
    public const string Repository = "Jonathan-Newman/ErRandoCompanion";
    public const string ReleasesPage = $"https://github.com/{Repository}/releases";
    private const string LatestRelease = $"https://api.github.com/repos/{Repository}/releases/latest";
    private const long MaxDownload = 300L * 1024 * 1024;

    /// <summary>How often to look, at most.</summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(20);

    /// <summary>For testing: act as if this were an older version, to see an update offered.</summary>
    public const string TestVersionVariable = "ER_COMPANION_UPDATE_TEST_VERSION";

    /// <summary>This build's version, or the test version when one is set.</summary>
    public static Version CurrentOf(Assembly assembly) =>
        Parse(Environment.GetEnvironmentVariable(TestVersionVariable) ?? "") ?? VersionOf(assembly);

    /// <summary>The version of a built assembly ("1.3.0" from "1.3.0+commit").</summary>
    public static Version VersionOf(Assembly assembly)
    {
        var text = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "0.0.0";
        return Parse(text) ?? new Version(0, 0, 0);
    }

    /// <summary>"v1.4.0", "1.4.0+abc" or "1.4" as a version, or null.</summary>
    public static Version? Parse(string text)
    {
        text = text.Trim().TrimStart('v', 'V');
        var end = text.IndexOfAny(['+', '-', ' ']);
        if (end >= 0)
            text = text[..end];
        return Version.TryParse(text, out var version) ? new Version(version.Major, version.Minor, Math.Max(0, version.Build)) : null;
    }

    public static HttpClient Client()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        // GitHub asks for a User-Agent; a generic one, with nothing about the player.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ErRandoCompanion-updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>The latest release on GitHub.</summary>
    /// <exception cref="UpdateException">GitHub could not be reached or answered with something unexpected.</exception>
    public static async Task<ReleaseInfo> LatestAsync(HttpClient client, CancellationToken cancel = default)
    {
        try
        {
            using var response = await client.GetAsync(LatestRelease, cancel).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new UpdateException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            var json = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
            return ParseRelease(json);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            throw new UpdateException($"Could not check for updates: {e.Message}", e);
        }
    }

    /// <summary>Reads GitHub's release JSON.</summary>
    public static ReleaseInfo ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = Parse(tag) ?? throw new UpdateException($"The latest release's tag, '{tag}', is not a version.");
        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var list))
        {
            foreach (var asset in list.EnumerateArray())
            {
                assets.Add(new ReleaseAsset(
                    asset.GetProperty("name").GetString() ?? "",
                    asset.GetProperty("browser_download_url").GetString() ?? "",
                    asset.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
                    asset.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String ? digest.GetString() : null));
            }
        }
        return new ReleaseInfo(version, tag,
            root.TryGetProperty("html_url", out var page) ? page.GetString() ?? ReleasesPage : ReleasesPage,
            root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
            assets);
    }

    /// <summary>Downloads a release file and checks it against GitHub's SHA-256.</summary>
    /// <exception cref="UpdateException">The download failed, is too large, has no published hash, or does not match it.</exception>
    public static async Task<byte[]> DownloadAsync(HttpClient client, ReleaseAsset asset, CancellationToken cancel = default)
    {
        if (asset.Digest is not { } digest || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            throw new UpdateException($"GitHub gives no SHA-256 for {asset.Name}, so it is not installed.");
        if (!asset.Url.StartsWith($"https://github.com/{Repository}/releases/download/", StringComparison.Ordinal))
            throw new UpdateException($"{asset.Name} is not a download of this project's releases.");
        if (asset.Size > MaxDownload)
            throw new UpdateException($"{asset.Name} is unexpectedly large.");
        byte[] bytes;
        try
        {
            bytes = await client.GetByteArrayAsync(asset.Url, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new UpdateException($"Could not download {asset.Name}: {e.Message}", e);
        }
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!hash.Equals(digest["sha256:".Length..], StringComparison.OrdinalIgnoreCase))
            throw new UpdateException($"{asset.Name} does not match the SHA-256 GitHub publishes for it, so it is not installed.");
        return bytes;
    }

    /// <summary>
    /// Puts a release zip's files ("ErRandoCompanion/...") into a folder. A file in use (the overlay's DLLs inside a
    /// running game, the app's own exe) is renamed to ".old" first, which Windows allows, so the new one is used the
    /// next time it starts. Only the release's own files are touched: settings, logs and runs are left alone.
    /// Returns how many files were written.
    /// </summary>
    /// <exception cref="UpdateException">The zip is not a release, or a file could not be replaced.</exception>
    public static int Install(byte[] zip, string folder)
    {
        try
        {
            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            var files = archive.Entries.Where(e => e.Name.Length > 0).ToList();
            if (files.Count == 0)
                throw new UpdateException("The download has no files in it.");
            var root = Path.GetFullPath(folder);
            var written = 0;
            foreach (var entry in files)
            {
                // Inside the zip everything is under "ErRandoCompanion/", which is the folder being updated.
                var relative = entry.FullName.Replace('\\', '/');
                var slash = relative.IndexOf('/');
                if (slash < 0)
                    continue;
                relative = relative[(slash + 1)..];
                var target = Path.GetFullPath(Path.Combine(root, relative));
                if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new UpdateException($"The download has a file outside its folder: {entry.FullName}.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var fresh = target + ".new";
                entry.ExtractToFile(fresh, overwrite: true);
                Replace(fresh, target);
                written++;
            }
            return written;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new UpdateException($"Could not install the update into {folder}: {e.Message}", e);
        }
    }

    // Moves the new file into place. The old one is renamed out of the way first, which Windows allows even for a DLL
    // a running game has loaded, then deleted if nothing holds it; otherwise CleanUp removes it later.
    private static void Replace(string fresh, string target)
    {
        if (File.Exists(target))
        {
            var old = target + ".old";
            if (File.Exists(old) && !TryDelete(old))
                old = $"{target}.{DateTime.UtcNow.Ticks}.old";
            File.Move(target, old);
            File.Move(fresh, target);
            TryDelete(old);
            return;
        }
        File.Move(fresh, target);
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // "X.old" and "X.638...old" both belong to "X".
    private static string BaseOf(string old)
    {
        var name = old[..^4];
        var dot = name.LastIndexOf('.');
        return dot > 0 && name[(dot + 1)..].Length > 0 && name[(dot + 1)..].All(char.IsDigit) ? name[..dot] : name;
    }

    /// <summary>
    /// Deletes ".old" files a previous update left behind, once nothing uses them: only "X.old" (or "X.123.old") beside
    /// an "X", in the folder itself and its Data and licenses folders, so nothing else ending in .old is touched.
    /// Never throws.
    /// </summary>
    public static void CleanUp(string folder)
    {
        try
        {
            var olds = new[] { folder, Path.Combine(folder, "Data"), Path.Combine(folder, "licenses") }
                .Where(Directory.Exists)
                .SelectMany(f => Directory.EnumerateFiles(f, "*.old", SearchOption.TopDirectoryOnly))
                .Where(old => File.Exists(BaseOf(old)))
                .ToList();
            foreach (var old in olds)
            {
                try
                {
                    File.Delete(old);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Still in use (the game is running); next time.
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
