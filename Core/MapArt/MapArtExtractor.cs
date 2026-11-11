using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ErRandoCompanion.Core.MapArt;

/// <summary>
/// Which part of one of the game's map sheets the app shows: left, top, right, bottom in full size pixels (one per metre),
/// and how much it is scaled down. These match tools/build_map_data.py, which placed the pins in Data/map_data.json.
/// </summary>
public sealed record MapArtSheet(string FileName, int Sheet, int Left, int Top, int Right, int Bottom, int Downscale)
{
    public int Width => (Right - Left) / Downscale;
    public int Height => (Bottom - Top) / Downscale;
}

/// <summary>A stitched sheet: three bytes a pixel, blue green red, row by row.</summary>
public sealed record MapArtImage(int Width, int Height, byte[] Bgr);

/// <summary>
/// Builds the app's world map pictures from the player's own Elden Ring install. The art is FromSoftware's, so it is
/// never shipped with the app: each player makes their own copy from the game they own. Read-only on the game folder.
/// </summary>
public sealed partial class MapArtExtractor : IDisposable
{
    public static readonly IReadOnlyList<MapArtSheet> Sheets =
    [
        new("map_base.jpg", 0, 0, 0, 9700, 9550, 2),
        new("map_underground.jpg", 1, 0, 0, 9700, 9550, 2),
        // The DLC sheet is cropped to the land, leaving out its wide border and the tree above it.
        new("map_dlc.jpg", 10, 3650, 3450, 7750, 7750, 1),
    ];

    private const string TileIndex = "/menu/71_maptile.tpfbhd";
    private const string TileData = "/menu/71_maptile.tpfbdt";
    private const string TileMasks = "/menu/71_maptile.mtmskbnd.dcx";
    private const int TilePixels = 256;
    private const int Bc7Format = 98;

    private readonly List<GameArchive> _archives = [];
    private readonly Oodle _oodle;
    private readonly ArchiveFile _data;
    private readonly Dictionary<int, List<Tile>> _tiles = [];
    private readonly Dictionary<int, Dictionary<(int X, int Y), int>> _masks = [];

    private sealed record Tile(int X, int Y, int Mask, long Offset, int Size);

    /// <summary>True when a folder looks like the game's own folder (the one with eldenring.exe and the archives).</summary>
    public static bool IsGameFolder(string? folder) => folder is not null
        && File.Exists(Path.Combine(folder, "eldenring.exe"))
        && File.Exists(Path.Combine(folder, "Data0.bhd"))
        && File.Exists(Path.Combine(folder, "oo2core_6_win64.dll"));

    public MapArtExtractor(string gameFolder)
    {
        if (!IsGameFolder(gameFolder))
            throw new MapArtException($"{gameFolder} is not the Elden Ring game folder: it needs eldenring.exe, Data0.bhd and oo2core_6_win64.dll.");

        var keys = GameArchive.ReadKeys(Path.Combine(gameFolder, "eldenring.exe"));
        if (keys.Count == 0)
            throw new MapArtException("Could not find the archive keys in eldenring.exe.");
        foreach (var name in GameArchive.Names)
            if (GameArchive.TryOpen(Path.Combine(gameFolder, name + ".bhd"), keys) is { } archive)
                _archives.Add(archive);

        _oodle = new Oodle(Path.Combine(gameFolder, "oo2core_6_win64.dll"));
        var index = Find(TileIndex).ReadAll();
        _data = Find(TileData);
        ReadIndex(index);
        ReadMasks(_oodle.Dcx(Find(TileMasks).ReadAll()));
    }

    private ArchiveFile Find(string path) =>
        _archives.Select(a => a.Find(path)).FirstOrDefault(f => f is not null)
        ?? throw new MapArtException($"The game files do not have {path}. Is the game up to date?");

    private void ReadIndex(byte[] index)
    {
        var count = BinaryPrimitives.ReadInt32LittleEndian(index.AsSpan(0x0C));
        for (var i = 0; i < count; i++)
        {
            var pos = 0x40 + i * 36;
            var size = BinaryPrimitives.ReadInt64LittleEndian(index.AsSpan(pos + 8));
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(pos + 24));
            var nameOffset = BinaryPrimitives.ReadInt32LittleEndian(index.AsSpan(pos + 32));
            var match = TileName().Match(Utf16At(index, nameOffset));
            if (!match.Success || int.Parse(match.Groups[2].Value) != 0)
                continue;
            var sheet = int.Parse(match.Groups[1].Value);
            if (!_tiles.TryGetValue(sheet, out var list))
                _tiles[sheet] = list = [];
            list.Add(new Tile(int.Parse(match.Groups[3].Value), int.Parse(match.Groups[4].Value),
                Convert.ToInt32(match.Groups[5].Value, 16), offset, (int)size));
        }
    }

    // Many tile versions are stale development art the game never asks for: the mask says which one is really drawn.
    private void ReadMasks(byte[] binder)
    {
        var count = BinaryPrimitives.ReadInt32LittleEndian(binder.AsSpan(0x0C));
        var headerSize = (int)BinaryPrimitives.ReadInt64LittleEndian(binder.AsSpan(0x20));
        for (var i = 0; i < count; i++)
        {
            var pos = 0x40 + i * headerSize;
            var size = (int)BinaryPrimitives.ReadInt64LittleEndian(binder.AsSpan(pos + 16));
            var offset = BinaryPrimitives.ReadInt32LittleEndian(binder.AsSpan(pos + 24));
            var nameOffset = BinaryPrimitives.ReadInt32LittleEndian(binder.AsSpan(pos + 32));
            var match = MaskName().Match(Utf16At(binder, nameOffset));
            if (!match.Success)
                continue;
            var cells = new Dictionary<(int, int), int>();
            var xml = XDocument.Load(new MemoryStream(binder, offset, size, writable: false));
            foreach (var entry in xml.Descendants("MapTileMask"))
            {
                var cell = (int)entry.Attribute("id")!;
                cells[(cell / 100, cell % 100)] = (int)entry.Attribute("mask")!;
            }
            _masks[int.Parse(match.Groups[1].Value)] = cells;
        }
    }

    private static string Utf16At(byte[] data, int offset)
    {
        var end = offset;
        while (end + 1 < data.Length && (data[end] != 0 || data[end + 1] != 0))
            end += 2;
        return Encoding.Unicode.GetString(data, offset, end - offset);
    }

    /// <summary>Stitches one sheet, as if every map fragment were found, cropped and scaled for the app.</summary>
    public MapArtImage Build(MapArtSheet sheet, Action<double>? progress = null)
    {
        if (!_tiles.TryGetValue(sheet.Sheet, out var tiles) || tiles.Count == 0)
            throw new MapArtException($"The game files have no tiles for map sheet {sheet.Sheet}.");
        var masks = _masks.GetValueOrDefault(sheet.Sheet) ?? [];
        var versions = new Dictionary<(int, int, int), Tile>();
        foreach (var t in tiles)
            versions.TryAdd((t.X, t.Y, t.Mask), t);
        var cells = tiles.Select(t => (t.X, t.Y)).Distinct().ToList();
        var rows = cells.Max(c => c.Y) + 1;

        var scale = sheet.Downscale;
        var tileSize = TilePixels / scale;
        var image = new MapArtImage(sheet.Width, sheet.Height, new byte[sheet.Width * sheet.Height * 3]);
        var done = 0;
        Parallel.ForEach(cells, cell =>
        {
            var wanted = masks.GetValueOrDefault(cell, 0);
            if ((versions.GetValueOrDefault((cell.X, cell.Y, wanted)) ?? versions.GetValueOrDefault((cell.X, cell.Y, 0))) is { } tile)
            {
                var left = cell.X * tileSize - sheet.Left / scale;
                var top = (rows - 1 - cell.Y) * tileSize - sheet.Top / scale;
                if (left < sheet.Width && top < sheet.Height && left + tileSize > 0 && top + tileSize > 0)
                    Paste(image, DecodeTile(tile), scale, left, top);
            }
            progress?.Invoke((double)Interlocked.Increment(ref done) / cells.Count);
        });
        return image;
    }

    private byte[] DecodeTile(Tile tile)
    {
        var raw = _oodle.Dcx(_data.Read(tile.Offset, tile.Size));
        var dds = raw.AsSpan().IndexOf("DDS "u8);
        if (dds < 0)
            throw new MapArtException("A map tile has no texture in it.");
        var header = raw.AsSpan(dds);
        var height = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        var width = BinaryPrimitives.ReadInt32LittleEndian(header[16..]);
        if (width == TilePixels && height == TilePixels)
        {
            var fourCc = header.Slice(84, 4);
            if (fourCc.SequenceEqual("DX10"u8) && BinaryPrimitives.ReadInt32LittleEndian(header[128..]) == Bc7Format)
                return Bc7.Decode(header[148..], width, height);
            if (fourCc.SequenceEqual("DXT1"u8))
                return Bc1.Decode(header[128..], width, height);
        }
        throw new MapArtException("A map tile is in a format this version cannot read.");
    }

    // Copies a 256 pixel RGBA tile into the sheet, averaging scale x scale pixels into one and clipping to the crop.
    private static void Paste(MapArtImage image, byte[] rgba, int scale, int left, int top)
    {
        var size = TilePixels / scale;
        var area = scale * scale;
        for (var y = Math.Max(0, -top); y < size && top + y < image.Height; y++)
        for (var x = Math.Max(0, -left); x < size && left + x < image.Width; x++)
        {
            int r = 0, g = 0, b = 0;
            for (var dy = 0; dy < scale; dy++)
            for (var dx = 0; dx < scale; dx++)
            {
                var p = ((y * scale + dy) * TilePixels + x * scale + dx) * 4;
                r += rgba[p];
                g += rgba[p + 1];
                b += rgba[p + 2];
            }
            var o = ((top + y) * image.Width + left + x) * 3;
            image.Bgr[o] = (byte)((b + area / 2) / area);
            image.Bgr[o + 1] = (byte)((g + area / 2) / area);
            image.Bgr[o + 2] = (byte)((r + area / 2) / area);
        }
    }

    [GeneratedRegex(@"MENU_MapTile_M(\d+)_L(\d+)_(\d+)_(\d+)_(\w{8})")]
    private static partial Regex TileName();

    [GeneratedRegex(@"_M(\d+)\.mtmsk")]
    private static partial Regex MaskName();

    public void Dispose()
    {
        foreach (var archive in _archives)
            archive.Dispose();
        _oodle.Dispose();
    }
}

public sealed class MapArtException(string message) : Exception(message);
