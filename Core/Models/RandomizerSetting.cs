using System.Text.Json;

namespace ErRandoCompanion.Core.Models;

public enum SettingType
{
    Bool,
    Count,
    Choice,
}

/// <summary>A randomizer option that changes which checks matter or how they unlock.</summary>
public sealed class RandomizerSetting
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required SettingType Type { get; init; }
    public required JsonElement Default { get; init; }

    /// <summary>Allowed values when <see cref="Type"/> is Choice.</summary>
    public IReadOnlyList<string> Options { get; set; } = [];

    /// <summary>Display names matching <see cref="Options"/>.</summary>
    public IReadOnlyList<string> OptionNames { get; set; } = [];

    /// <summary>Highest value when <see cref="Type"/> is Count.</summary>
    public int Max { get; init; }

    public string Notes { get; set; } = "";

    /// <summary>The default in the text form tracker state stores: "true", "2", "normal".</summary>
    public string DefaultText => Default.ValueKind == JsonValueKind.String ? Default.GetString()! : Default.GetRawText();
}
