using System.Globalization;

namespace ErRandoCompanion.GameReader;

/// <summary>
/// Finds a global pointer through the instruction that loads it, so game patches
/// that move addresses do not break the reader.
/// </summary>
/// <param name="Pattern">Space-separated hex bytes, with ? for any byte.</param>
/// <param name="OperandOffset">Where the 32-bit relative address sits in the pattern.</param>
/// <param name="InstructionEnd">Where the instruction using that address ends.</param>
public sealed record Signature(string Pattern, int OperandOffset, int InstructionEnd);

public static class SignatureScanner
{
    /// <summary>Returns the first match's index, or -1.</summary>
    public static int Find(ReadOnlySpan<byte> memory, string pattern)
    {
        var tokens = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bytes = new byte[tokens.Length];
        var known = new bool[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
        {
            known[i] = tokens[i] != "?";
            if (known[i])
                bytes[i] = byte.Parse(tokens[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        for (var start = 0; start <= memory.Length - bytes.Length; start++)
        {
            var matched = true;
            for (var i = 0; i < bytes.Length && matched; i++)
                matched = !known[i] || memory[start + i] == bytes[i];
            if (matched)
                return start;
        }
        return -1;
    }

    /// <summary>Resolves the address a matching instruction refers to, or returns null.</summary>
    public static long? ResolveAddress(ReadOnlySpan<byte> memory, long baseAddress, Signature signature)
    {
        var index = Find(memory, signature.Pattern);
        if (index < 0)
            return null;

        var relative = BitConverter.ToInt32(memory.Slice(index + signature.OperandOffset, sizeof(int)));
        return baseAddress + index + signature.InstructionEnd + relative;
    }
}
