using ErRandoCompanion.Core.MapArt;
using ErRandoCompanion.Core.Models;

namespace ErRandoCompanion.Tests;

public class MapArtTests
{
    // Tiles dumped from the game with Pillow's decode beside them (bc7.bin/rgba.bin, bc1.bin/bc1rgba.bin).
    private static readonly string? Samples = Environment.GetEnvironmentVariable("ER_TEST_TEXTURE_SAMPLES");
    // The Elden Ring game folder (the one with eldenring.exe). Read only.
    private static readonly string? GameFolder = Environment.GetEnvironmentVariable("ER_TEST_GAME");

    [Fact]
    public void PathHashMatchesTheGames()
    {
        Assert.Equal(0x267434f51b689da4UL, GameArchive.PathHash("/menu/71_maptile.tpfbhd"));
        Assert.Equal(GameArchive.PathHash("/menu/71_maptile.tpfbhd"), GameArchive.PathHash("\\MENU\\71_maptile.TPFBHD"));
    }

    [Fact]
    public void Bc1BlockDecodes()
    {
        // Pure red and pure blue endpoints; pixel 0 takes colour 0, pixel 1 colour 1, pixel 2 the 2/3 mix.
        byte[] block = [0x00, 0xF8, 0x1F, 0x00, 0b_00_10_01_00, 0, 0, 0];
        var rgba = Bc1.Decode(block, 4, 4);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, rgba[..4]);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, rgba[4..8]);
        Assert.Equal(new byte[] { 170, 0, 85, 255 }, rgba[8..12]);
    }

    [Fact]
    public void Bc7DecodesLikePillow()
    {
        if (Samples is null)
            return;
        CompareWithPillow(Path.Combine(Samples, "bc7.bin"), Path.Combine(Samples, "rgba.bin"), 65536, Bc7.Decode, 0);
    }

    [Fact]
    public void Bc1DecodesLikePillow()
    {
        if (Samples is null)
            return;
        // Decoders round the 1/3 and 2/3 mixes slightly differently.
        CompareWithPillow(Path.Combine(Samples, "bc1.bin"), Path.Combine(Samples, "bc1rgba.bin"), 32768, Bc1.Decode, 1);
    }

    private delegate byte[] Decoder(ReadOnlySpan<byte> data, int width, int height);

    private static void CompareWithPillow(string texturePath, string expectedPath, int tileBytes, Decoder decode, int tolerance)
    {
        var textures = File.ReadAllBytes(texturePath);
        var expected = File.ReadAllBytes(expectedPath);
        var tiles = textures.Length / tileBytes;
        Assert.True(tiles > 0);
        Assert.Equal(tiles * 256 * 256 * 4, expected.Length);
        var worst = 0;
        var wrong = 0;
        for (var t = 0; t < tiles; t++)
        {
            var mine = decode(textures.AsSpan(t * tileBytes, tileBytes), 256, 256);
            var theirs = expected.AsSpan(t * mine.Length, mine.Length);
            for (var i = 0; i < mine.Length; i++)
            {
                var diff = Math.Abs(mine[i] - theirs[i]);
                worst = Math.Max(worst, diff);
                if (diff > tolerance)
                    wrong++;
            }
        }
        Assert.True(wrong == 0, $"{wrong} channel values differ from Pillow by more than {tolerance} (worst {worst}) over {tiles} tiles.");
    }

    [Fact]
    public void BuildsEverySheetFromTheGame()
    {
        if (GameFolder is null)
            return;
        var maps = MapData.Load(Path.Combine(AppContext.BaseDirectory, "Data", "map_data.json")).Maps;
        using var extractor = new MapArtExtractor(GameFolder);
        foreach (var sheet in MapArtExtractor.Sheets)
        {
            // The pins in map_data.json were placed for pictures exactly this size.
            var map = Assert.Single(maps, m => m.Image == sheet.FileName);
            Assert.Equal(map.Width, sheet.Width);
            Assert.Equal(map.Height, sheet.Height);

            var image = extractor.Build(sheet);
            Assert.Equal(sheet.Width * sheet.Height * 3, image.Bgr.Length);
            if (Environment.GetEnvironmentVariable("ER_TEST_MAP_OUT") is { } output)
                File.WriteAllBytes(Path.Combine(output, sheet.FileName + ".bgr"), image.Bgr);
            // The underground sheet is mostly the black space between its caverns, so only look for some art.
            var lit = image.Bgr.Count(b => b > 16);
            Assert.True(lit > image.Bgr.Length / 20, $"{sheet.FileName} came out black.");
        }
    }
}
