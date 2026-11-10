namespace ErRandoCompanion.Core.Models;

public sealed class Item
{
    public required string Name { get; init; }

    /// <summary>"key" for progression items, "optional" for quest items.</summary>
    public required string Category { get; init; }

    public int MaxCount { get; set; } = 1;
    public bool Dlc { get; init; }
    public bool MiniDungeon { get; init; }

    /// <summary>True for optional items that can open an alternate route or shortcut.</summary>
    public bool Shortcut { get; init; }

    /// <summary>Id of a bool setting that must be on for this item to matter.</summary>
    public string? OnlyIfSetting { get; init; }

    public string Notes { get; set; } = "";
}
