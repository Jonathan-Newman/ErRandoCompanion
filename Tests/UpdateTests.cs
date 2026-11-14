using System.IO.Compression;
using System.Security.Cryptography;
using ErRandoCompanion.Core.Updates;

namespace ErRandoCompanion.Tests;

public class UpdateTests
{
    [Fact]
    public void ReadsVersions()
    {
        Assert.Equal(new Version(1, 4, 0), Updates.Parse("v1.4.0"));
        Assert.Equal(new Version(1, 3, 0), Updates.Parse("1.3.0+80795d1"));
        Assert.Equal(new Version(2, 0, 0), Updates.Parse("2.0"));
        Assert.Null(Updates.Parse("latest"));
        Assert.True(Updates.Parse("v1.10.0") > Updates.Parse("v1.9.2"));
    }

    [Fact]
    public void ReadsGitHubsReleaseJson()
    {
        const string json = """
            {
              "tag_name": "v1.4.0",
              "html_url": "https://github.com/Jonathan-Newman/ErRandoCompanion/releases/tag/v1.4.0",
              "body": "What's new",
              "assets": [
                { "name": "ErRandoCompanion-1.4.0-app.zip", "size": 100, "digest": "sha256:ab",
                  "browser_download_url": "https://github.com/Jonathan-Newman/ErRandoCompanion/releases/download/v1.4.0/ErRandoCompanion-1.4.0-app.zip" },
                { "name": "ErRandoCompanionMod-1.4.0.zip", "size": 50, "digest": null,
                  "browser_download_url": "https://github.com/Jonathan-Newman/ErRandoCompanion/releases/download/v1.4.0/ErRandoCompanionMod-1.4.0.zip" }
              ]
            }
            """;
        var release = Updates.ParseRelease(json);
        Assert.Equal(new Version(1, 4, 0), release.Version);
        Assert.Equal("ErRandoCompanion-1.4.0-app.zip", release.App?.Name);
        Assert.Equal("sha256:ab", release.App?.Digest);
        Assert.Null(release.Mod?.Digest);
    }

    [Fact]
    public async Task RefusesDownloadsWithoutAHashOrFromElsewhere()
    {
        using var client = Updates.Client();
        var noHash = new ReleaseAsset("ErRandoCompanionMod-9.zip", $"https://github.com/{Updates.Repository}/releases/download/v9/x.zip", 10, null);
        var elsewhere = new ReleaseAsset("ErRandoCompanionMod-9.zip", "https://example.com/x.zip", 10, "sha256:00");
        Assert.Contains("SHA-256", (await Assert.ThrowsAsync<UpdateException>(() => Updates.DownloadAsync(client, noHash))).Message);
        Assert.Contains("not a download", (await Assert.ThrowsAsync<UpdateException>(() => Updates.DownloadAsync(client, elsewhere))).Message);
    }

    [Fact]
    public void InstallsOverFilesInUseAndLeavesSettingsAlone()
    {
        var folder = Directory.CreateTempSubdirectory("er-update-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "ErRandoCompanionMod.dll"), "old dll");
            File.WriteAllText(Path.Combine(folder, "ErRandoCompanionMod.ini"), "my settings");
            var zip = Zip(("ErRandoCompanion/ErRandoCompanionMod.dll", "new dll"), ("ErRandoCompanion/Data/items.json", "{}"));

            // Held open the way Windows holds a loaded DLL: it can be renamed, not overwritten.
            using (new FileStream(Path.Combine(folder, "ErRandoCompanionMod.dll"), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                Assert.Equal(2, Updates.Install(zip, folder));
                Assert.Equal("new dll", File.ReadAllText(Path.Combine(folder, "ErRandoCompanionMod.dll")));
                // A loaded DLL cannot be deleted, so it stays as .old for now; this handle allows deleting, so it may be gone.
            }
            Assert.Equal("{}", File.ReadAllText(Path.Combine(folder, "Data", "items.json")));
            Assert.Equal("my settings", File.ReadAllText(Path.Combine(folder, "ErRandoCompanionMod.ini")));

            // Once nothing holds it, the old file goes; an unrelated .old file stays.
            File.WriteAllText(Path.Combine(folder, "ErRandoCompanionMod.dll.old"), "left behind");
            File.WriteAllText(Path.Combine(folder, "notes.old"), "not ours");
            Updates.CleanUp(folder);
            Assert.False(File.Exists(Path.Combine(folder, "ErRandoCompanionMod.dll.old")));
            Assert.True(File.Exists(Path.Combine(folder, "notes.old")));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void RefusesAZipWithFilesOutsideItsFolder()
    {
        var folder = Directory.CreateTempSubdirectory("er-update-").FullName;
        try
        {
            var zip = Zip(("ErRandoCompanion/../../evil.txt", "no"));
            Assert.Throws<UpdateException>(() => Updates.Install(zip, folder));
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(folder)!, "evil.txt")));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    /// <summary>The released 1.3.0 overlay zip (ER_TEST_RELEASE_ZIP) against the SHA-256 GitHub published for it.</summary>
    [Fact]
    public void MatchesGitHubsHashForARealRelease()
    {
        var path = Environment.GetEnvironmentVariable("ER_TEST_RELEASE_ZIP");
        if (path is null)
            return;
        Assert.Equal("e5a4d73a7cf01c01ce0ce272e2afba8c0958c6e6ee766b46f57617efd2d739ae",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), ignoreCase: true);
    }

    private static byte[] Zip(params (string Name, string Text)[] files)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in files)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(text);
            }
        }
        return stream.ToArray();
    }
}
