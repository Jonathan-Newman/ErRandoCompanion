using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace ErRandoCompanion.Core.MapArt;

/// <summary>The game's own Oodle library (oo2core_6_win64.dll in the game folder), loaded from where it is installed.</summary>
public sealed unsafe class Oodle : IDisposable
{
    private readonly IntPtr _library;
    private readonly delegate* unmanaged<byte*, long, byte*, long, int, int, int, IntPtr, long, IntPtr, IntPtr, IntPtr, long, int, long> _decompress;

    public Oodle(string dllPath)
    {
        _library = NativeLibrary.Load(dllPath);
        _decompress = (delegate* unmanaged<byte*, long, byte*, long, int, int, int, IntPtr, long, IntPtr, IntPtr, IntPtr, long, int, long>)
            NativeLibrary.GetExport(_library, "OodleLZ_Decompress");
    }

    public byte[] Decompress(ReadOnlySpan<byte> source, int size)
    {
        var output = new byte[size];
        long written;
        fixed (byte* src = source)
        fixed (byte* dst = output)
            written = _decompress(src, source.Length, dst, size, 0, 0, 0, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, 3);
        if (written != size)
            throw new InvalidDataException("Oodle could not decompress a game file.");
        return output;
    }

    /// <summary>Unpacks a DCX container (Oodle "KRAK" or zlib "DFLT").</summary>
    public byte[] Dcx(byte[] data)
    {
        if (data.Length < 0x30 || !data.AsSpan(0, 4).SequenceEqual("DCX\0"u8))
            throw new InvalidDataException("Not a DCX file.");
        var format = data.AsSpan(0x28, 4);
        var size = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0x1C));
        var compressed = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0x20));
        var start = data.AsSpan().IndexOf("DCA\0"u8) + 8;
        var body = data.AsSpan(start, compressed);
        if (format.SequenceEqual("KRAK"u8))
            return Decompress(body, size);
        if (!format.SequenceEqual("DFLT"u8))
            throw new InvalidDataException("Unsupported DCX format.");
        using var zlib = new ZLibStream(new MemoryStream(body.ToArray()), CompressionMode.Decompress);
        var output = new byte[size];
        zlib.ReadExactly(output);
        return output;
    }

    public void Dispose() => NativeLibrary.Free(_library);
}
