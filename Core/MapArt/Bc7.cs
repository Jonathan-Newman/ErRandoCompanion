namespace ErRandoCompanion.Core.MapArt;

/// <summary>Decodes BC7 textures (DXGI format 98), the format of the game's map tiles, written from the BC7 format spec.</summary>
public static class Bc7
{
    // Per mode: subsets, partition bits, rotation bits, index selection bit, colour bits, alpha bits,
    // endpoint p-bits, shared p-bits, index bits, second index bits.
    private static readonly int[,] Modes =
    {
        { 3, 4, 0, 0, 4, 0, 1, 0, 3, 0 },
        { 2, 6, 0, 0, 6, 0, 0, 1, 3, 0 },
        { 3, 6, 0, 0, 5, 0, 0, 0, 2, 0 },
        { 2, 6, 0, 0, 7, 0, 1, 0, 2, 0 },
        { 1, 0, 2, 1, 5, 6, 0, 0, 2, 3 },
        { 1, 0, 2, 0, 7, 8, 0, 0, 2, 2 },
        { 1, 0, 0, 0, 7, 7, 1, 0, 4, 0 },
        { 2, 6, 0, 0, 5, 5, 1, 0, 2, 0 },
    };

    private static readonly ushort[] Partitions2 =
    [
        0xCCCC, 0x8888, 0xEEEE, 0xECC8, 0xC880, 0xFEEC, 0xFEC8, 0xEC80, 0xC800, 0xFFEC, 0xFE80, 0xE800, 0xFFE8, 0xFF00, 0xFFF0, 0xF000,
        0xF710, 0x008E, 0x7100, 0x08CE, 0x008C, 0x7310, 0x3100, 0x8CCE, 0x088C, 0x3110, 0x6666, 0x366C, 0x17E8, 0x0FF0, 0x718E, 0x399C,
        0xAAAA, 0xF0F0, 0x5A5A, 0x33CC, 0x3C3C, 0x55AA, 0x9696, 0xA55A, 0x73CE, 0x13C8, 0x324C, 0x3BDC, 0x6996, 0xC33C, 0x9966, 0x0660,
        0x0272, 0x04E4, 0x4E40, 0x2720, 0xC936, 0x936C, 0x39C6, 0x639C, 0x9336, 0x9CC6, 0x817E, 0xE718, 0xCCF0, 0x0FCC, 0x7744, 0xEE22,
    ];

    private static readonly string[] Partitions3 =
    [
        "0011001102212222", "0001001122112221", "0000200122112211", "0222002200110111",
        "0000000011221122", "0011001100220022", "0022002211111111", "0011001122112211",
        "0000000011112222", "0000111111112222", "0000111122222222", "0012001200120012",
        "0112011201120112", "0122012201220122", "0011011211221222", "0011200122002220",
        "0001001101121122", "0111001120012200", "0000112211221122", "0022002200221111",
        "0111011102220222", "0001000122212221", "0000001101220122", "0000110022102210",
        "0122012200110000", "0012001211222222", "0110122112210110", "0000011012211221",
        "0022110211020022", "0110011020022222", "0011012201220011", "0000200022112221",
        "0000000211221222", "0222002200120011", "0011001200220222", "0120012001200120",
        "0000111122220000", "0120120120120120", "0120201212010120", "0011220011220011",
        "0011112222000011", "0101010122222222", "0000000021212121", "0022112200221122",
        "0022001100220011", "0220122102201221", "0101222222220101", "0000212121212121",
        "0101010101012222", "0222011102220111", "0002111200021112", "0000211221122112",
        "0222011101110222", "0002111211120002", "0110011001102222", "0000000021122112",
        "0110011022222222", "0022001100110022", "0022112211220022", "0000000000002112",
        "0002000100020001", "0222122202221222", "0101222222222222", "0111201122012220",
    ];

    private static readonly byte[] Anchor2 =
    [
        15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15,
        15, 2, 8, 2, 2, 8, 8, 15, 2, 8, 2, 2, 8, 8, 2, 2,
        15, 15, 6, 8, 2, 8, 15, 15, 2, 8, 2, 2, 2, 15, 15, 6,
        6, 2, 6, 8, 15, 15, 2, 2, 15, 15, 15, 15, 15, 2, 2, 15,
    ];

    private static readonly byte[] Anchor3A =
    [
        3, 3, 15, 15, 8, 3, 15, 15, 8, 8, 6, 6, 6, 5, 3, 3,
        3, 3, 8, 15, 3, 3, 6, 10, 5, 8, 8, 6, 8, 5, 15, 15,
        8, 15, 3, 5, 6, 10, 8, 15, 15, 3, 15, 5, 15, 15, 15, 15,
        3, 15, 5, 5, 5, 8, 5, 10, 5, 10, 8, 13, 15, 12, 3, 3,
    ];

    private static readonly byte[] Anchor3B =
    [
        15, 8, 8, 3, 15, 15, 3, 8, 15, 15, 15, 15, 15, 15, 15, 8,
        15, 8, 15, 3, 15, 8, 15, 8, 3, 15, 6, 10, 15, 15, 10, 8,
        15, 3, 15, 10, 10, 8, 9, 10, 6, 15, 8, 15, 3, 6, 6, 8,
        15, 3, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 3, 15, 15, 8,
    ];

    private static readonly byte[] Weights2 = [0, 21, 43, 64];
    private static readonly byte[] Weights3 = [0, 9, 18, 27, 37, 46, 55, 64];
    private static readonly byte[] Weights4 = [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];

    /// <summary>Decodes a whole BC7 image to RGBA, four bytes a pixel.</summary>
    public static byte[] Decode(ReadOnlySpan<byte> data, int width, int height)
    {
        var output = new byte[width * height * 4];
        Span<byte> block = stackalloc byte[64];
        var blocksWide = (width + 3) / 4;
        for (var by = 0; by < (height + 3) / 4; by++)
        for (var bx = 0; bx < blocksWide; bx++)
        {
            DecodeBlock(data.Slice((by * blocksWide + bx) * 16, 16), block);
            for (var py = 0; py < 4 && by * 4 + py < height; py++)
            for (var px = 0; px < 4 && bx * 4 + px < width; px++)
                block.Slice((py * 4 + px) * 4, 4).CopyTo(output.AsSpan(((by * 4 + py) * width + bx * 4 + px) * 4));
        }
        return output;
    }

    /// <summary>Decodes one 16 byte block into 16 RGBA pixels, row by row.</summary>
    public static void DecodeBlock(ReadOnlySpan<byte> source, Span<byte> rgba)
    {
        var bits = new Bits(source);
        var mode = 0;
        while (mode < 8 && bits.Read(1) == 0)
            mode++;
        if (mode == 8)
        {
            rgba[..64].Clear();
            return;
        }

        int subsets = Modes[mode, 0], colourBits = Modes[mode, 4], alphaBits = Modes[mode, 5];
        int indexBits = Modes[mode, 8], indexBits2 = Modes[mode, 9];
        var partition = bits.Read(Modes[mode, 1]);
        var rotation = bits.Read(Modes[mode, 2]);
        var indexSelection = bits.Read(Modes[mode, 3]);

        var endpoints = subsets * 2;
        Span<int> ep = stackalloc int[6 * 4];
        for (var c = 0; c < 3; c++)
        for (var i = 0; i < endpoints; i++)
            ep[i * 4 + c] = bits.Read(colourBits);
        for (var i = 0; i < endpoints; i++)
            ep[i * 4 + 3] = alphaBits > 0 ? bits.Read(alphaBits) : 255;

        Span<int> pbit = stackalloc int[6];
        var hasPbit = Modes[mode, 6] == 1 || Modes[mode, 7] == 1;
        if (Modes[mode, 6] == 1)
            for (var i = 0; i < endpoints; i++)
                pbit[i] = bits.Read(1);
        else if (Modes[mode, 7] == 1)
            for (var s = 0; s < subsets; s++)
                pbit[s * 2] = pbit[s * 2 + 1] = bits.Read(1);

        for (var i = 0; i < endpoints; i++)
        {
            for (var c = 0; c < 3; c++)
                ep[i * 4 + c] = Expand(ep[i * 4 + c], colourBits, hasPbit, pbit[i]);
            if (alphaBits > 0)
                ep[i * 4 + 3] = Expand(ep[i * 4 + 3], alphaBits, hasPbit, pbit[i]);
        }

        Span<int> subsetOf = stackalloc int[16];
        int anchor1 = 0, anchor2 = 0;
        for (var i = 0; i < 16; i++)
            subsetOf[i] = subsets switch
            {
                2 => (Partitions2[partition] >> i) & 1,
                3 => Partitions3[partition][i] - '0',
                _ => 0,
            };
        if (subsets == 2)
            anchor1 = Anchor2[partition];
        else if (subsets == 3)
        {
            anchor1 = Anchor3A[partition];
            anchor2 = Anchor3B[partition];
        }

        Span<int> index = stackalloc int[16];
        Span<int> index2 = stackalloc int[16];
        for (var i = 0; i < 16; i++)
        {
            var anchor = i == 0 || (subsets >= 2 && i == anchor1) || (subsets == 3 && i == anchor2);
            index[i] = bits.Read(indexBits - (anchor ? 1 : 0));
        }
        if (indexBits2 > 0)
            for (var i = 0; i < 16; i++)
                index2[i] = bits.Read(indexBits2 - (i == 0 ? 1 : 0));

        for (var i = 0; i < 16; i++)
        {
            var e0 = subsetOf[i] * 2 * 4;
            var e1 = e0 + 4;
            int colourIndex = index[i], colourWidth = indexBits, alphaIndex = index[i], alphaWidth = indexBits;
            if (indexBits2 > 0)
            {
                if (indexSelection == 0)
                {
                    alphaIndex = index2[i];
                    alphaWidth = indexBits2;
                }
                else
                {
                    colourIndex = index2[i];
                    colourWidth = indexBits2;
                    alphaWidth = indexBits;
                    alphaIndex = index[i];
                    // The second index set colours, the first (narrower) one alpha.
                }
            }

            var pixel = rgba.Slice(i * 4, 4);
            var cw = Weight(colourWidth, colourIndex);
            var aw = Weight(alphaWidth, alphaIndex);
            for (var c = 0; c < 3; c++)
                pixel[c] = Interpolate(ep[e0 + c], ep[e1 + c], cw);
            pixel[3] = Interpolate(ep[e0 + 3], ep[e1 + 3], aw);

            switch (rotation)
            {
                case 1: (pixel[0], pixel[3]) = (pixel[3], pixel[0]); break;
                case 2: (pixel[1], pixel[3]) = (pixel[3], pixel[1]); break;
                case 3: (pixel[2], pixel[3]) = (pixel[3], pixel[2]); break;
            }
        }
    }

    private static int Expand(int value, int width, bool hasPbit, int pbit)
    {
        if (hasPbit)
        {
            value = (value << 1) | pbit;
            width++;
        }
        value <<= 8 - width;
        return (value | (value >> width)) & 0xFF;
    }

    private static int Weight(int width, int index) => width switch
    {
        2 => Weights2[index],
        3 => Weights3[index],
        _ => Weights4[index],
    };

    private static byte Interpolate(int a, int b, int weight) => (byte)(((64 - weight) * a + weight * b + 32) >> 6);

    private ref struct Bits(ReadOnlySpan<byte> data)
    {
        private readonly UInt128 _value = new(
            BitConverter.ToUInt64(data[8..16]), BitConverter.ToUInt64(data[..8]));
        private int _position;

        public int Read(int count)
        {
            if (count == 0)
                return 0;
            var result = (int)((_value >> _position) & ((UInt128.One << count) - 1));
            _position += count;
            return result;
        }
    }
}
