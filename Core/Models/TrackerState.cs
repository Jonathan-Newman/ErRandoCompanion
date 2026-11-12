using ErRandoCompanion.Core.Data;

namespace ErRandoCompanion.Core.Models;

/// <summary>Everything the player has marked for one seed.</summary>
public sealed class TrackerState
{
    public string SeedName { get; set; } = "";
    public HashSet<string> CheckedLocations { get; set; } = [];

    /// <summary>Owned count by item name. Missing means none.</summary>
    public Dictionary<string, int> Items { get; set; } = [];

    /// <summary>Setting values by id, as text: "true", "2", "normal".</summary>
    public Dictionary<string, string> Settings { get; set; } = [];

    /// <summary>The seed's spoiler log, reloaded whenever this seed is opened.</summary>
    public string? SpoilerLogPath { get; set; }

    /// <summary>
    /// What auto-tracking has already credited, so unticking something or spending a key
    /// sticks instead of being undone on the next poll.
    /// </summary>
    public HashSet<string> AutoCredited { get; set; } = [];

    /// <summary>Time played on this run, in seconds: only while a character is in the world (see <see cref="RunClock"/>).</summary>
    public double PlayedSeconds { get; set; }

    /// <summary>When the run's goal was met; the clock stops then. Null while the run is still going.</summary>
    public DateTime? FinishedAt { get; set; }

    /// <summary>What finishes this run; null for no goal.</summary>
    public RunGoal? Goal { get; set; }

    /// <summary>Everything picked up in this run, in the order it was found.</summary>
    public List<FoundItem> Found { get; set; } = [];

    /// <summary>Whether pickups have been scanned once; the first scan's finds are marked as before tracking.</summary>
    public bool PickupsScanned { get; set; }

    /// <summary>The run's played time when each check was ticked, by location id (see <see cref="Logic.RunTimeline"/>).</summary>
    public Dictionary<string, double> CheckedAt { get; set; } = [];

    /// <summary>Key items as they were found, in order: "Rusty Key", "Great Runes 2". Played time of each.</summary>
    public List<TimedEvent> KeyItemsFound { get; set; } = [];

    /// <summary>Each step toward the goal as it was reached (a Great Rune, a chosen boss), with its played time.</summary>
    public List<TimedEvent> Splits { get; set; } = [];

    /// <summary>Deaths by the place they happened ("Limgrave", "Stormveil Castle"), counted from the game's own death count.</summary>
    public Dictionary<string, int> Deaths { get; set; } = [];

    /// <summary>Whether this run has kept a timeline from its start; older saves have not, so what they had gets no time.</summary>
    public bool TimelineStarted { get; set; }

    /// <summary>Checks put aside for now ("skipped for now"): left out of the nearest list and the dead-end count.</summary>
    public HashSet<string> Skipped { get; set; } = [];

    /// <summary>
    /// Ticked checks of the full list (every slot the randomizer could fill, see <see cref="AllChecks.AllChecksSeed"/>),
    /// by slot id ("lot:10000420", "shop:100012"). Kept apart from <see cref="CheckedLocations"/>, the main checklist.
    /// </summary>
    public HashSet<string> AllChecked { get; set; } = [];

    /// <summary>Enemies killed in this run, by kind (NpcParam row), counted from the game (see <see cref="Logic.KillCounter"/>).</summary>
    public Dictionary<int, int> Kills { get; set; } = [];

    /// <summary>The name of each kind killed ("Imp"), by NpcParam row.</summary>
    public Dictionary<int, string> KillKinds { get; set; } = [];

    public const int MaxTargets = 5;

    /// <summary>Checks the player is hunting, by location id, numbered 1 to <see cref="MaxTargets"/>. Each number is used once.</summary>
    public Dictionary<string, int> Targets { get; set; } = [];

    /// <summary>The target number the guide arrow follows. Null, or a number no longer in use, means the lowest one.</summary>
    public int? ActiveTarget { get; set; }

    /// <summary>Gives a location a target number, taking it from any other location; null clears the location's target.</summary>
    public void SetTarget(string locationId, int? number)
    {
        if (number is null)
        {
            Targets.Remove(locationId);
            return;
        }
        if (number is < 1 or > MaxTargets)
            throw new ArgumentOutOfRangeException(nameof(number), number, $"Targets are numbered 1 to {MaxTargets}.");

        foreach (var holder in Targets.Where(t => t.Value == number && t.Key != locationId).Select(t => t.Key).ToList())
            Targets.Remove(holder);
        Targets[locationId] = number.Value;
    }

    /// <summary>The location the guide arrow follows, or null when there are no targets.</summary>
    public string? ActiveTargetLocation()
    {
        var active = Targets.FirstOrDefault(t => t.Value == ActiveTarget).Key;
        return active ?? Targets.OrderBy(t => t.Value).Select(t => t.Key).FirstOrDefault();
    }

    /// <summary>A found target has done its job, so checking a location drops its target. Returns whether any were dropped.</summary>
    public bool ClearCheckedTargets()
    {
        var found = Targets.Keys.Where(CheckedLocations.Contains).ToList();
        foreach (var id in found)
            Targets.Remove(id);
        return found.Count > 0;
    }

    public static TrackerState Create(string seedName, GameData data)
    {
        var state = new TrackerState { SeedName = seedName, TimelineStarted = true };
        state.ApplyMissingDefaults(data);
        return state;
    }

    /// <summary>Fills in settings added to the data files since this state was saved.</summary>
    public void ApplyMissingDefaults(GameData data)
    {
        foreach (var setting in data.Settings)
            Settings.TryAdd(setting.Id, setting.DefaultText);
    }
}
