using System.Buffers.Binary;

namespace ErRandoCompanion.Core.MapArt;

/// <summary>Decodes BC1 (DXT1) textures, which some of the base game's map tiles use.</summary>
public static class Bc1
{
    /// <summary>Decodes a whole BC1 image to RGBA, four bytes a pixel.</summary>
    public static byte[] Decode(ReadOnlySpan<byte> data, int width, int height)
    {
        var output = new byte[width * height * 4];
        Span<byte> palette = stackalloc byte[16];
        var blocksWide = (width + 3) / 4;
        for (var by = 0; by < (height + 3) / 4; by++)
        for (var bx = 0; bx < blocksWide; bx++)
        {
            var block = data.Slice((by * blocksWide + bx) * 8, 8);
            var c0 = BinaryPrimitives.ReadUInt16LittleEndian(block);
            var c1 = BinaryPrimitives.ReadUInt16LittleEndian(block[2..]);
            Unpack(c0, palette[..4]);
            Unpack(c1, palette[4..8]);
            for (var c = 0; c < 3; c++)
            {
                int a = palette[c], b = palette[4 + c];
                if (c0 > c1)
                {
                    palette[8 + c] = (byte)((2 * a + b) / 3);
                    palette[12 + c] = (byte)((a + 2 * b) / 3);
                }
                else
                {
                    palette[8 + c] = (byte)((a + b) / 2);
                    palette[12 + c] = 0;
                }
            }
            palette[11] = 255;
            palette[15] = (byte)(c0 > c1 ? 255 : 0);

            var indices = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
            for (var py = 0; py < 4 && by * 4 + py < height; py++)
            for (var px = 0; px < 4 && bx * 4 + px < width; px++)
            {
                var index = (int)(indices >> (2 * (py * 4 + px))) & 3;
                palette.Slice(index * 4, 4).CopyTo(output.AsSpan(((by * 4 + py) * width + bx * 4 + px) * 4));
            }
        }
        return output;
    }

    private static void Unpack(ushort colour, Span<byte> rgba)
    {
        int r = (colour >> 11) & 31, g = (colour >> 5) & 63, b = colour & 31;
        rgba[0] = (byte)((r << 3) | (r >> 2));
        rgba[1] = (byte)((g << 2) | (g >> 4));
        rgba[2] = (byte)((b << 3) | (b >> 2));
        rgba[3] = 255;
    }
}
