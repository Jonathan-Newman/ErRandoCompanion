namespace ErRandoCompanion.Core.Models;

/// <summary>
/// Something the player picked up: what it gave and where, kept with the run. Only ever made after the pickup, from
/// the flag the game sets, so it shows nothing the player has not already seen.
/// </summary>
public sealed class FoundItem
{
    public uint Flag { get; set; }

    /// <summary>"Uchigatana", "Golden Rune [2] x3": what the pickup gave.</summary>
    public List<string> Items { get; set; } = [];

    /// <summary>The kind of each item, for filtering: Weapons, Armour, Talismans, Ashes of War, Spells, Spirit Ashes, Items.</summary>
    public List<string> Kinds { get; set; } = [];

    /// <summary>Where it was, as the randomizer describes the spot, or the map the player was on.</summary>
    public string Where { get; set; } = "";

    public string Map { get; set; } = "";

    /// <summary>When it was picked up; null for pickups made before the run was first scanned.</summary>
    public DateTime? At { get; set; }

    /// <summary>The run's played time at the pickup.</summary>
    public double PlayedSeconds { get; set; }

    /// <summary>"" for a pickup in the world; <see cref="DropOrOther"/> for something received another way (a random
    /// enemy drop, a purchase, crafting), seen by its arrival in the inventory.</summary>
    public string Source { get; set; } = "";

    public const string DropOrOther = "Drop or other";
}
