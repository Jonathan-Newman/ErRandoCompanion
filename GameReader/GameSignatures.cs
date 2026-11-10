using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ErRandoCompanion.GameReader;

/// <summary>
/// Where the player's data sits inside the game's player object, as hex text such as "0x6D4".
/// These change between game versions, so they live in Data/signatures.json and can be fixed without a rebuild.
/// </summary>
public sealed class PlayerOffsetsText
{
    public string PlayerInsOffset { get; set; } = "";
    public string MapIdOffset { get; set; } = "";
    public string PositionOffset { get; set; } = "";
}

public sealed record PlayerOffsets(int PlayerInstance, int MapId, int Position);

/// <summary>Everything Data/signatures.json says about finding things in the game's memory.</summary>
public sealed class GameSignatures
{
    public Signature? EventFlagManager { get; set; }

    /// <summary>Optional: without it, flags are still read but the player's position is not.</summary>
    public Signature? WorldChrMan { get; set; }

    public PlayerOffsetsText? Player { get; set; }

    /// <summary>Optional: the game data manager, for reading the inventory (drops and other received items).</summary>
    public Signature? GameDataMan { get; set; }

    /// <exception cref="IOException">The file is missing or unreadable.</exception>
    /// <exception cref="JsonException">The file is damaged, or has no event flag signature.</exception>
    public static GameSignatures Load(string path)
    {
        var signatures = JsonSerializer.Deserialize(File.ReadAllText(path), GameReaderJsonContext.Default.GameSignatures)
            ?? throw new JsonException($"{path} is empty.");
        if (signatures.EventFlagManager is null)
            throw new JsonException($"{path} has no eventFlagManager entry.");
        return signatures;
    }

    /// <summary>The player offsets as numbers, or null when they are missing or not hex.</summary>
    public PlayerOffsets? ParsePlayerOffsets()
    {
        if (Player is null)
            return null;
        return TryHex(Player.PlayerInsOffset, out var instance) && TryHex(Player.MapIdOffset, out var map) && TryHex(Player.PositionOffset, out var position)
            ? new PlayerOffsets(instance, map, position)
            : null;

        static bool TryHex(string text, out int value) =>
            int.TryParse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text,
                NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GameSignatures))]
internal sealed partial class GameReaderJsonContext : JsonSerializerContext;
