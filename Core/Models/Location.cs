namespace ErRandoCompanion.Core.Models;

public enum LocationCategory
{
    KeyItem,
    MajorBoss,
    OtherBoss,
    SeedOrTear,
    Merchant,
    Blessing,
    ExtraBoss,
}

public sealed class Location
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Region { get; init; }
    public string Details { get; set; } = "";
    public required LocationCategory Category { get; init; }
    public bool Dlc { get; init; }
    public bool MiniDungeon { get; init; }

    /// <summary>0 means not mapped yet, so the location stays manual-only.</summary>
    public int FlagId { get; init; }

    /// <summary>
    /// The vanilla item lots at this location. A seed can give a lot a different flag,
    /// so a loaded seed's lots take priority over <see cref="FlagId"/>.
    /// </summary>
    public IReadOnlyList<int> LotIds { get; set; } = [];

    /// <summary>For a merchant: the ShopLineupParam rows it sells from. Buying any limited item there ticks the check.</summary>
    public IReadOnlyList<int> ShopRows { get; set; } = [];

    /// <summary>OR of ANDs. Empty means the region is the only requirement.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Requires { get; set; } = [];
}
