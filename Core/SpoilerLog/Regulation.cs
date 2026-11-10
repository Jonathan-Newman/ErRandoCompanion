using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ErRandoCompanion.Core.SpoilerLog;

public sealed record LotEntry(int ItemId, int Category, int Count, uint Flag);

/// <param name="Flag">Turns on when the stock runs out (a purchase, for one-off items); 0 for unlimited stock.</param>
/// <param name="RowId">The ShopLineupParam row, which says whose shop it is.</param>
public sealed record ShopRow(int ItemId, byte ItemType, uint Flag, short Quantity, int RowId = 0);

/// <summary>A map tile, without its level: area, then grid x and z, as in m60_42_36.</summary>
public readonly record struct MapTile(byte Area, byte X, byte Z);

/// <summary>A Site of Grace: the flag that turns on when it is found, and where it is in its own map.</summary>
public sealed record GraceRow(int RowId, uint Flag, MapTile Tile, float X, float Y, float Z);

/// <summary>One point given in two maps' coordinates, which is how the game joins a dungeon to the open world.</summary>
public sealed record MapLinkRow(MapTile From, float FromX, float FromY, float FromZ, MapTile To, float ToX, float ToY, float ToZ);

/// <summary>
/// The item lots and shops from a randomized regulation.bin. Unlike the randomizer's vanilla
/// location tables, this is what the seed actually does, including which flag each pickup sets.
/// </summary>
public sealed class Regulation
{
    public const int GoodsCategory = 1;
    public const byte GoodsShopType = 3;

    private const string MapLots = "ItemLotParam_map.param";
    private const string EnemyLots = "ItemLotParam_enemy.param";
    private const string Shops = "ShopLineupParam.param";
    private const string MapLinks = "WorldMapLegacyConvParam.param";
    private const string Graces = "BonfireWarpParam.param";

    private static readonly byte[] AesKey = Convert.FromHexString(
        "99BFFC366A6BC8C6F5827D093602D676C42892A01C207FB024D3AF4E493FEF99");

    public required IReadOnlyDictionary<int, IReadOnlyList<LotEntry>> Lots { get; init; }
    public required IReadOnlyList<ShopRow> ShopRows { get; init; }

    /// <summary>Where dungeons join the open world. The same in every seed, as the randomizer does not move maps.</summary>
    public IReadOnlyList<MapLinkRow> MapLinkRows { get; init; } = [];

    /// <summary>Every Site of Grace you can warp to. The same in every seed.</summary>
    public IReadOnlyList<GraceRow> GraceRows { get; init; } = [];

    /// <param name="regulationPath">The seed's regulation.bin.</param>
    /// <param name="oodlePath">The game's Oodle library, which the regulation's compression needs.</param>
    /// <exception cref="SpoilerLogException">The file is missing or not understood.</exception>
    public static Regulation Load(string regulationPath, string oodlePath)
    {
        try
        {
            var dcx = Decrypt(File.ReadAllBytes(regulationPath));
            return FromBinder(Decompress(dcx, oodlePath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException
            or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or ArgumentException or InvalidDataException)
        {
            throw new SpoilerLogException($"Could not read {regulationPath}: {e.Message}", e);
        }
    }

    /// <summary>Reads the params from a decompressed BND4 archive.</summary>
    public static Regulation FromBinder(byte[] binder)
    {
        var files = ReadBinder(binder);
        var lots = new Dictionary<int, IReadOnlyList<LotEntry>>();

        // A lot id is only in one table in practice; map lots win as the game checks them first.
        foreach (var table in new[] { EnemyLots, MapLots })
        {
            foreach (var (id, row) in ReadParamRows(RequireFile(files, table)))
                lots[id] = ReadLot(row);
        }

        var shops = ReadParamRows(RequireFile(files, Shops))
            .Select(r => new ShopRow(
                BitConverter.ToInt32(r.Row, 0),
                r.Row[0x17],
                BitConverter.ToUInt32(r.Row, 12),
                BitConverter.ToInt16(r.Row, 20),
                r.Id))
            .ToList();
        var links = files.TryGetValue(MapLinks, out var linkParam)
            ? ReadParamRows(linkParam).Select(r => ReadMapLink(r.Row)).ToList()
            : [];
        var graces = files.TryGetValue(Graces, out var graceParam)
            ? ReadParamRows(graceParam).Select(r => ReadGrace(r.Id, r.Row)).Where(g => g.Flag != 0).ToList()
            : [];
        return new Regulation { Lots = lots, ShopRows = shops, MapLinkRows = links, GraceRows = graces };
    }

    private static byte[] RequireFile(Dictionary<string, byte[]> files, string name) =>
        files.TryGetValue(name, out var file) ? file : throw new InvalidDataException($"It has no {name}.");

    private static byte[] Decrypt(byte[] file)
    {
        using var aes = Aes.Create();
        aes.Key = AesKey;
        return aes.DecryptCbc(file.AsSpan(16), file.AsSpan(0, 16), PaddingMode.None);
    }

    private static byte[] Decompress(byte[] dcx, string oodlePath)
    {
        if (Encoding.ASCII.GetString(dcx, 0, 3) != "DCX")
            throw new InvalidDataException("It is not a DCX archive.");

        var format = Encoding.ASCII.GetString(dcx, 0x28, 4);
        if (format != "KRAK")
            throw new InvalidDataException($"Its compression, {format}, is not supported.");

        var size = ReadBigEndianInt(dcx, 0x1C);
        var compressed = ReadBigEndianInt(dcx, 0x20);
        var start = dcx.AsSpan().IndexOf("DCA\0"u8) + 8;
        if (start < 8 || start + compressed > dcx.Length)
            throw new InvalidDataException("Its DCX header is damaged.");

        var output = new byte[size];
        var library = NativeLibrary.Load(oodlePath);
        try
        {
            if (OodleDecompress(NativeLibrary.GetExport(library, "OodleLZ_Decompress"), dcx.AsSpan(start, compressed), output) != size)
                throw new InvalidDataException("Decompressing it failed.");
        }
        finally
        {
            NativeLibrary.Free(library);
        }
        return output;
    }

    // A function pointer rather than a marshalled delegate, so this also works in the mod's native library.
    private static unsafe long OodleDecompress(nint function, ReadOnlySpan<byte> source, byte[] output)
    {
        var decompress = (delegate* unmanaged[Cdecl]<byte*, long, byte*, long, int, int, int, nint, long, nint, nint, nint, long, int, long>)function;
        fixed (byte* input = source)
        fixed (byte* result = output)
        {
            // Arguments: fuzz safety, CRC check and verbosity off, no dictionary, callback or scratch memory, all thread phases.
            return decompress(input, source.Length, result, output.Length, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3);
        }
    }

    // WORLD_MAP_LEGACY_CONV_PARAM_ST: a tile and a point in it, then the same point's tile and position in the other map.
    private static MapLinkRow ReadMapLink(byte[] row) => new(
        new MapTile(row[4], row[5], row[6]),
        BitConverter.ToSingle(row, 8), BitConverter.ToSingle(row, 12), BitConverter.ToSingle(row, 16),
        new MapTile(row[20], row[21], row[22]),
        BitConverter.ToSingle(row, 24), BitConverter.ToSingle(row, 28), BitConverter.ToSingle(row, 32));

    // BONFIRE_WARP_PARAM_ST: the found flag at 4, then the map tile at 0x20 and the position at 0x24.
    private static GraceRow ReadGrace(int id, byte[] row) => new(
        id,
        BitConverter.ToUInt32(row, 4),
        new MapTile(row[0x20], row[0x21], row[0x22]),
        BitConverter.ToSingle(row, 0x24), BitConverter.ToSingle(row, 0x28), BitConverter.ToSingle(row, 0x2C));

    private static int ReadBigEndianInt(byte[] data, int offset) =>
        System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset));

    private static Dictionary<string, byte[]> ReadBinder(byte[] data)
    {
        if (Encoding.ASCII.GetString(data, 0, 4) != "BND4")
            throw new InvalidDataException("It does not contain a BND4 archive.");

        var count = BitConverter.ToInt32(data, 0x0C);
        var headerSize = (int)BitConverter.ToInt64(data, 0x20);
        var files = new Dictionary<string, byte[]>();
        for (var i = 0; i < count; i++)
        {
            var pos = 0x40 + i * headerSize;
            var compressedSize = BitConverter.ToInt64(data, pos + 8);
            var size = BitConverter.ToInt64(data, pos + 16);
            var offset = BitConverter.ToInt32(data, pos + 24);
            var nameOffset = BitConverter.ToInt32(data, pos + 32);

            var nameEnd = nameOffset;
            while (data[nameEnd] != 0 || data[nameEnd + 1] != 0)
                nameEnd += 2;
            var name = Encoding.Unicode.GetString(data, nameOffset, nameEnd - nameOffset);

            var length = (int)(compressedSize > 0 ? compressedSize : size);
            files[name[(name.LastIndexOf('\\') + 1)..]] = data.AsSpan(offset, length).ToArray();
        }
        return files;
    }

    /// <summary>Row ids and contents from a 64-bit param file. All rows in a param are the same size.</summary>
    private static List<(int Id, byte[] Row)> ReadParamRows(byte[] param)
    {
        const int firstEntry = 0x40;
        const int entrySize = 24;

        int count = BitConverter.ToUInt16(param, 0x0A);
        var offsets = new List<(int Id, long Offset)>(count);
        for (var i = 0; i < count; i++)
        {
            var pos = firstEntry + i * entrySize;
            offsets.Add((BitConverter.ToInt32(param, pos), BitConverter.ToInt64(param, pos + 8)));
        }

        var rowSize = offsets.Zip(offsets.Skip(1), (a, b) => b.Offset - a.Offset).Where(d => d > 0).DefaultIfEmpty(0).Min();
        return offsets.Select(o => (o.Id, param.AsSpan((int)o.Offset, (int)rowSize).ToArray())).ToList();
    }

    // ITEMLOT_PARAM_ST: 8 item ids, 8 categories, 8+8 chance fields, 8 per-entry flags, the lot flag, then counts.
    private static List<LotEntry> ReadLot(byte[] row)
    {
        const int entries = 8;
        const int categories = 32;
        const int entryFlags = 96;
        const int lotFlag = 128;
        const int counts = 138;

        var flag = BitConverter.ToUInt32(row, lotFlag);
        var lot = new List<LotEntry>();
        for (var i = 0; i < entries; i++)
        {
            var id = BitConverter.ToInt32(row, i * 4);
            if (id != 0)
            {
                var entryFlag = BitConverter.ToUInt32(row, entryFlags + i * 4);
                lot.Add(new LotEntry(id, BitConverter.ToInt32(row, categories + i * 4), row[counts + i], entryFlag != 0 ? entryFlag : flag));
            }
        }
        return lot;
    }
}
