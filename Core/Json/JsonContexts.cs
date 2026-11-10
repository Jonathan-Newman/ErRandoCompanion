using System.Text.Json;
using System.Text.Json.Serialization;
using ErRandoCompanion.Core.Models;
using ErRandoCompanion.Core.Persistence;

namespace ErRandoCompanion.Core.Json;

// The generated readers overwrite an init-only property with null or 0 when a file leaves its key out,
// throwing away the property's default. So properties with defaults use set, not init.

/// <summary>
/// Reads the shipped Data files. Generated at compile time, so it needs no reflection and also
/// works in the mod's native library. Lenient, as the files are edited by hand.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(List<Location>))]
[JsonSerializable(typeof(List<Region>))]
[JsonSerializable(typeof(List<Item>))]
[JsonSerializable(typeof(List<RandomizerSetting>))]
[JsonSerializable(typeof(MapData))]
public sealed partial class DataJsonContext : JsonSerializerContext;

/// <summary>Reads and writes the player's own files. Their format must not change, or older saves stop loading.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(TrackerState))]
[JsonSerializable(typeof(Preferences))]
public sealed partial class SaveJsonContext : JsonSerializerContext;
