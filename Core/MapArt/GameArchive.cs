using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ErRandoCompanion.Core.MapArt;

/// <summary>One of the RSA public keys that open the game's archive headers.</summary>
public sealed record ArchiveKey(BigInteger Modulus, BigInteger Exponent, int Size);

/// <summary>
/// Reads single files out of Elden Ring's encrypted archives (Data0-3 and DLC .bhd/.bdt). Read-only: it never writes to
/// the game folder. The header keys are read from the player's own eldenring.exe, which carries them as plain text.
/// </summary>
public sealed class GameArchive : IDisposable
{
    public static readonly string[] Names = ["Data0", "Data1", "Data2", "Data3", "DLC"];
    private const int FileHeaderSize = 40;
    private static readonly byte[] KeyStart = Encoding.ASCII.GetBytes("-----BEGIN RSA PUBLIC KEY-----");
    private static readonly byte[] KeyEnd = Encoding.ASCII.GetBytes("-----END RSA PUBLIC KEY-----");

    private readonly byte[] _bhd;
    private readonly SafeFileHandle _bdt;
    private readonly int _bucketCount;
    private readonly int _bucketsOffset;

    private GameArchive(byte[] bhd, SafeFileHandle bdt)
    {
        _bhd = bhd;
        _bdt = bdt;
        _bucketCount = BinaryPrimitives.ReadInt32LittleEndian(bhd.AsSpan(0x10));
        _bucketsOffset = BinaryPrimitives.ReadInt32LittleEndian(bhd.AsSpan(0x14));
    }

    /// <summary>Every RSA public key written in an exe, as ASCII (the game) or UTF-16 (the randomizer).</summary>
    public static IReadOnlyList<ArchiveKey> ReadKeys(string exePath)
    {
        var bytes = File.ReadAllBytes(exePath);
        var keys = new List<ArchiveKey>();
        foreach (var pem in FindPem(bytes, KeyStart, KeyEnd, Encoding.ASCII)
                     .Concat(FindPem(bytes, Encoding.Unicode.GetBytes("-----BEGIN RSA PUBLIC KEY-----"),
                         Encoding.Unicode.GetBytes("-----END RSA PUBLIC KEY-----"), Encoding.Unicode)))
        {
            using var rsa = RSA.Create();
            try
            {
                rsa.ImportFromPem(pem.Replace("\r", ""));
            }
            catch (Exception e) when (e is ArgumentException or CryptographicException)
            {
                continue;
            }
            var p = rsa.ExportParameters(false);
            var modulus = new BigInteger(p.Modulus, isUnsigned: true, isBigEndian: true);
            if (keys.All(k => k.Modulus != modulus))
                keys.Add(new ArchiveKey(modulus, new BigInteger(p.Exponent, isUnsigned: true, isBigEndian: true), p.Modulus!.Length));
        }
        return keys;
    }

    private static IEnumerable<string> FindPem(byte[] bytes, byte[] start, byte[] end, Encoding encoding)
    {
        var from = 0;
        while (from < bytes.Length)
        {
            var at = bytes.AsSpan(from).IndexOf(start);
            if (at < 0)
                yield break;
            at += from;
            var stop = bytes.AsSpan(at).IndexOf(end);
            if (stop < 0 || stop > 4096)
            {
                from = at + start.Length;
                continue;
            }
            yield return encoding.GetString(bytes, at, stop + end.Length);
            from = at + stop + end.Length;
        }
    }

    /// <summary>Opens an archive, or returns null when none of the keys opens its header.</summary>
    public static GameArchive? TryOpen(string bhdPath, IReadOnlyList<ArchiveKey> keys)
    {
        var bdtPath = Path.ChangeExtension(bhdPath, ".bdt");
        if (!File.Exists(bhdPath) || !File.Exists(bdtPath))
            return null;
        var header = File.ReadAllBytes(bhdPath);
        foreach (var key in keys)
        {
            if (header.Length % key.Size != 0 || !Decrypt(header.AsSpan(0, key.Size), key).AsSpan(0, 4).SequenceEqual("BHD5"u8))
                continue;
            var blocks = header.Length / key.Size;
            var plain = new byte[blocks * (key.Size - 1)];
            Parallel.For(0, blocks, i => Decrypt(header.AsSpan(i * key.Size, key.Size), key)
                .CopyTo(plain, i * (key.Size - 1)));
            return new GameArchive(plain, File.OpenHandle(bdtPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        }
        return null;
    }

    private static byte[] Decrypt(ReadOnlySpan<byte> block, ArchiveKey key)
    {
        var value = BigInteger.ModPow(new BigInteger(block, isUnsigned: true, isBigEndian: true), key.Exponent, key.Modulus);
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        var full = new byte[key.Size];
        bytes.CopyTo(full, key.Size - bytes.Length);
        return full[1..];
    }

    public static ulong PathHash(string path)
    {
        ulong value = 0;
        foreach (var ch in path.ToLowerInvariant().Replace('\\', '/'))
            value = value * 0x85 + ch;
        return value;
    }

    /// <summary>A file in this archive, or null when it is not here.</summary>
    public ArchiveFile? Find(string path)
    {
        var wanted = PathHash(path);
        var bucket = _bucketsOffset + 8 * (int)(wanted % (ulong)_bucketCount);
        var count = BinaryPrimitives.ReadInt32LittleEndian(_bhd.AsSpan(bucket));
        var offset = BinaryPrimitives.ReadInt32LittleEndian(_bhd.AsSpan(bucket + 4));
        for (var i = 0; i < count; i++)
        {
            var pos = offset + i * FileHeaderSize;
            if (BinaryPrimitives.ReadUInt64LittleEndian(_bhd.AsSpan(pos)) != wanted)
                continue;
            var h = _bhd.AsSpan(pos + 8);
            var padded = BinaryPrimitives.ReadInt32LittleEndian(h);
            var unpadded = BinaryPrimitives.ReadInt32LittleEndian(h[4..]);
            var start = BinaryPrimitives.ReadInt64LittleEndian(h[8..]);
            var aesOffset = BinaryPrimitives.ReadInt64LittleEndian(h[24..]);

            byte[]? aesKey = null;
            var ranges = new List<(long Start, long End)>();
            if (aesOffset != 0)
            {
                aesKey = _bhd.AsSpan((int)aesOffset, 16).ToArray();
                var rangeCount = BinaryPrimitives.ReadInt32LittleEndian(_bhd.AsSpan((int)aesOffset + 16));
                for (var r = 0; r < rangeCount; r++)
                {
                    var at = (int)aesOffset + 20 + r * 16;
                    var s = BinaryPrimitives.ReadInt64LittleEndian(_bhd.AsSpan(at));
                    var e = BinaryPrimitives.ReadInt64LittleEndian(_bhd.AsSpan(at + 8));
                    if (s != -1 && e > s)
                        ranges.Add((s, e));
                }
            }
            return new ArchiveFile(_bdt, start, unpadded != 0 ? unpadded : padded, aesKey, ranges);
        }
        return null;
    }

    public void Dispose() => _bdt.Dispose();
}

/// <summary>A file inside an archive, read in pieces so a large one never has to be held in memory whole.</summary>
public sealed class ArchiveFile
{
    private readonly SafeFileHandle _bdt;
    private readonly long _start;
    private readonly byte[]? _aesKey;
    private readonly IReadOnlyList<(long Start, long End)> _ranges;

    internal ArchiveFile(SafeFileHandle bdt, long start, long length, byte[]? aesKey, IReadOnlyList<(long Start, long End)> ranges)
    {
        _bdt = bdt;
        _start = start;
        Length = length;
        _aesKey = aesKey;
        _ranges = ranges;
    }

    public long Length { get; }

    public byte[] ReadAll() => Read(0, checked((int)Length));

    /// <summary>Reads part of the file, decrypting whatever of it is encrypted. Safe to call from several threads.</summary>
    public byte[] Read(long at, int count)
    {
        var lo = at;
        var hi = at + count;
        var blocks = new List<(long Start, long End)>();
        foreach (var (s, e) in _ranges)
        {
            if (e <= at || s >= at + count)
                continue;
            // ECB works on whole 16 byte blocks counted from the start of the range, so widen the read to them.
            var bs = s + (Math.Max(at, s) - s) / 16 * 16;
            var be = Math.Min(s + (e - s) / 16 * 16, s + (Math.Min(at + count, e) - s + 15) / 16 * 16);
            if (be <= bs)
                continue;
            blocks.Add((bs, be));
            lo = Math.Min(lo, bs);
            hi = Math.Max(hi, be);
        }

        var buffer = new byte[hi - lo];
        var done = 0;
        while (done < buffer.Length)
        {
            var n = RandomAccess.Read(_bdt, buffer.AsSpan(done), _start + lo + done);
            if (n <= 0)
                throw new EndOfStreamException("The game archive ended early.");
            done += n;
        }

        if (blocks.Count > 0)
        {
            using var aes = Aes.Create();
            aes.Key = _aesKey!;
            foreach (var (bs, be) in blocks)
            {
                var span = buffer.AsSpan((int)(bs - lo), (int)(be - bs));
                aes.DecryptEcb(span, span, PaddingMode.None);
            }
        }
        return lo == at && buffer.Length == count ? buffer : buffer.AsSpan((int)(at - lo), count).ToArray();
    }
}
