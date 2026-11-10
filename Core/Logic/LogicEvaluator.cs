using ErRandoCompanion.Core.Data;
using ErRandoCompanion.Core.Models;

namespace ErRandoCompanion.Core.Logic;

public enum LocationStatus
{
    /// <summary>Not relevant under the current randomizer settings.</summary>
    Excluded,
    Checked,
    Available,
    Locked,
}

/// <summary>
/// Answers logic questions for one tracker state. Region results are cached, so
/// call <see cref="Invalidate"/> after changing the state.
/// </summary>
public sealed class LogicEvaluator(GameData data, TrackerState state)
{
    private readonly Dictionary<string, Region> _regions = data.Regions.ToDictionary(r => r.Id);
    private readonly Dictionary<string, RequirementToken> _tokens = [];
    private readonly Dictionary<string, bool> _reachable = [];

    public void Invalidate() => _reachable.Clear();

    public LocationStatus GetStatus(Location location)
    {
        if (IsExcluded(location))
            return LocationStatus.Excluded;
        if (state.CheckedLocations.Contains(location.Id))
            return LocationStatus.Checked;
        return IsLocationAvailable(location) ? LocationStatus.Available : LocationStatus.Locked;
    }

    public bool IsLocationAvailable(Location location) =>
        IsRegionReachable(location.Region) && IsSatisfied(location.Requires);

    public bool IsRegionReachable(string regionId)
    {
        if (_reachable.TryGetValue(regionId, out var known))
            return known;

        // Provisionally false, so a route cycle in hand-edited data ends instead of overflowing.
        _reachable[regionId] = false;
        return _reachable[regionId] = _regions.TryGetValue(regionId, out var region) && IsSatisfied(region.Requires);
    }

    public bool IsExcluded(Location location)
    {
        if (location.MiniDungeon && IsOn("excludeMiniDungeons"))
            return true;
        if (location.Dlc ? !IsOn("dlcEnabled") : SettingText("startMode") == "dlcOnly")
            return true;

        return location.Category switch
        {
            LocationCategory.OtherBoss => !IsOn("otherBosses"),
            LocationCategory.SeedOrTear => !IsOn("seedsAndTears"),
            LocationCategory.Merchant => !IsOn("merchantShops"),
            LocationCategory.Blessing => !IsOn("blessings"),
            _ => false,
        };
    }

    /// <summary>False when the current settings make an item irrelevant to this run.</summary>
    public bool IsItemRelevant(Item item)
    {
        if (item.Dlc && !IsOn("dlcEnabled"))
            return false;
        if (item.MiniDungeon && IsOn("excludeMiniDungeons"))
            return false;
        return item.OnlyIfSetting is null || IsOn(item.OnlyIfSetting);
    }

    private bool IsSatisfied(IReadOnlyList<IReadOnlyList<string>> requires) =>
        requires.Count == 0 || requires.Any(option => option.All(IsTrue));

    private bool IsTrue(string text)
    {
        if (!_tokens.TryGetValue(text, out var token))
            _tokens[text] = token = RequirementToken.Parse(text);

        var result = token.Kind switch
        {
            TokenKind.Check => state.CheckedLocations.Contains(token.Name),
            TokenKind.Route => IsRegionReachable(token.Name),
            TokenKind.Setting => token.SettingValue is null ? IsOn(token.Name) : SettingText(token.Name) == token.SettingValue,
            _ => state.Items.GetValueOrDefault(token.Name) >= NeededCount(token),
        };
        return result != token.Negated;
    }

    private int NeededCount(RequirementToken token) =>
        token.CountSetting is null ? token.Count : int.TryParse(SettingText(token.CountSetting), out var count) ? count : 0;

    private bool IsOn(string settingId) => SettingText(settingId) == "true";

    private string SettingText(string settingId) => state.Settings.GetValueOrDefault(settingId, "0");
}
