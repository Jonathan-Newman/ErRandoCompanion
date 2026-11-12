using System.Text.Json.Serialization;

namespace ErRandoCompanion.Native;

// What er_call returns, as camelCase JSON. Nothing here may carry spoiler data: no item placements.

public sealed record SessionOptions(string? DataDirectory, string? SeedsDirectory, string? RandomizerDirectory, bool ReadOwnProcess);

public sealed record TargetInfo(int Number, string Id, string Name, bool Active);

public sealed record SpoilerInfo(bool Loaded, string? Seed, string? RandomizerVersion);

public sealed record Summary(
    string SeedName,
    SpoilerInfo Spoiler,
    int Checked,
    int Total,
    int Available,
    IReadOnlyList<TargetInfo> Targets,
    string? Warning,
    string? Problem,
    double PlayedSeconds = 0,
    bool Finished = false,
    GoalInfo? Goal = null)
{
    /// <summary>Available checks that are not skipped: what can be done right now.</summary>
    public int Open { get; init; }

    public int Skipped { get; init; }

    /// <summary>So few open checks that the run is close to stuck: the overlay suggests the key items that open the most.</summary>
    public bool DeadEnd { get; init; }

    public int Deaths { get; init; }

    /// <summary>The goal's steps so far, each beside the best finished run's time at the same step.</summary>
    public IReadOnlyList<SplitInfo> Splits { get; init; } = [];
}

/// <summary>A step toward the goal. Seconds is negative for a step reached before timing started; Best is the best run's time.</summary>
public sealed record SplitInfo(int Number, string Name, double Seconds, double? Best);

/// <summary>One region's checks by state; open means available and not skipped.</summary>
public sealed record RegionInfo(string Region, int Open, int Skipped, int Locked, int Done, int Total);

public sealed record TimedInfo(string Name, double Seconds);

public sealed record PlaceCount(string Place, int Count);

/// <summary>An enemy kind killed in this run, how many, and the drops of every enemy of a kind it can give, with their odds.</summary>
public sealed record KillKindInfo(string Kind, int Kills, IReadOnlyList<AllCheckInfo> Drops);

/// <summary>A place in the full check list, with its counts.</summary>
public sealed record AllPlaceInfo(string Name, int Done, int Total, int Open);

/// <summary>
/// The full check list's places and totals. Message says why there is no list (no spoiler log, missing tables),
/// and is null when there is one.
/// </summary>
public sealed record AllPlacesInfo(string? Message, int Done, int Total, int Open, int ByHand, IReadOnlyList<AllPlaceInfo> Places)
{
    /// <summary>Drops that would usually have dropped by now, from the kills so far.</summary>
    public int Overdue { get; init; }
}

/// <summary>One check of the full list: where it is and what the unrandomized game had there, never what it holds now.</summary>
public sealed record AllCheckInfo(string Id, string Description, string Kind, string Replaces, bool Missable, bool ByHand, string Status)
{
    /// <summary>For a drop of every enemy of a kind: kills and odds ("23 killed  ·  5% a kill  ·  31% chance of no drop yet").</summary>
    public string? Odds { get; init; }

    /// <summary>It would usually have dropped by now.</summary>
    public bool Overdue { get; init; }
}

/// <summary>The longest wait between ticked checks: from and to in played seconds, and the check that ended it.</summary>
public sealed record StretchInfo(double From, double To, string EndedWith);

/// <summary>A run in numbers: what the player did, never where items are.</summary>
public sealed record ReportInfo(
    string SeedName,
    string Goal,
    bool Finished,
    double PlayedSeconds,
    int Checked,
    int Total,
    int BossesDefeated,
    int Deaths,
    IReadOnlyList<PlaceCount> DeathPlaces,
    StretchInfo? Slowest,
    IReadOnlyList<TimedInfo> KeyItems,
    IReadOnlyList<SplitInfo> Splits)
{
    /// <summary>Enemies killed, by kind ("Imp"), most first.</summary>
    public IReadOnlyList<PlaceCount> Kills { get; init; } = [];
}

/// <summary>The run's goal and how far along it is.</summary>
public sealed record GoalInfo(string Kind, int? Count, string Description, int Done, int Needed, bool Met)
{
    /// <summary>For "defeat chosen bosses": each chosen boss and whether it is defeated. Never where it is.</summary>
    public IReadOnlyList<GoalBossInfo> Bosses { get; init; } = [];
}

public sealed record GoalBossInfo(string Id, string Name, bool Defeated);

/// <summary>A missing item and how many locked checks it would open now. Logic only: says nothing about where items are.</summary>
public sealed record UnlockInfo(string Item, int Opens, IReadOnlyList<string> Regions);

/// <summary>Something picked up. At and PlayedTime are null for pickups made before tracking started.</summary>
public sealed record FoundInfo(IReadOnlyList<string> Items, IReadOnlyList<string> Kinds, string Where, string Map, string? At, string? PlayedTime)
{
    /// <summary>"" for a pickup in the world; "Drop or other" for something seen arriving in the inventory.</summary>
    public string Source { get; init; } = "";
}

/// <summary>A finished run. FinishedAt is local time as text; Best marks the fastest run with that goal.</summary>
public sealed record HistoryInfo(string SeedName, string Goal, string FinishedAt, double PlayedSeconds, int Checked, double? PerHour, bool Best)
{
    public int Deaths { get; init; }
}

/// <summary>A boss that can be chosen for the goal: its own check, and whether it is a major boss.</summary>
public sealed record BossChoiceInfo(string Id, string Name, string Region, bool Major);

/// <summary>A goal the player can pick; Counts says whether it takes a number.</summary>
public sealed record GoalKindInfo(string Kind, string Name, bool Counts);

public sealed record CheckInfo(
    string Id,
    string Name,
    string Region,
    string Category,
    string Status,
    string Details,
    int? Target,
    bool OnMap)
{
    /// <summary>Put aside for now by the player.</summary>
    public bool Skipped { get; init; }
}

public sealed record ItemInfo(string Name, string Category, int Count, int Max, bool Relevant, bool Shortcut);

public sealed record SettingInfo(string Id, string Name, string Type, string Value, IReadOnlyList<string> Options, IReadOnlyList<string> OptionNames, int Max);

/// <summary>A saved copy of a run; SavedAt is local time as text, for showing as it is.</summary>
public sealed record BackupInfo(string SeedName, string Id, string SavedAt, int Checked)
{
    /// <summary>Whether the game's save was copied with it; it can only be put back from the desktop app, with the game closed.</summary>
    public bool HasGameSave { get; init; }
}

public sealed record PlayerInfo(string Map, double X, double Y, double Z);

/// <summary>Which way to the active target, in metres along the game's axes: x and z across the ground, y up.</summary>
public sealed record GuideInfo(string TargetId, string TargetName, string Mode, double Dx, double? Dy, double Dz, double Distance)
{
    /// <summary>The found Site of Grace closest to the target, to warp to, and how far it is from the target in metres.</summary>
    public string? Grace { get; init; }
    public double? GraceDistance { get; init; }

    /// <summary>The best route: warp to this found grace first (null to walk from here), then walk this far. Null when
    /// there is no route (another realm with no grace found there).</summary>
    public string? WarpTo { get; init; }
    public double? Walk { get; init; }

    /// <summary>How far above (positive) or below the player the target is, when that is known.</summary>
    public double? Rise { get; init; }
}

/// <summary>An available check near the player: the way there, as for the guide.</summary>
public sealed record NearbyInfo(string Id, string Name, string Mode, double Dx, double Dz, double Distance);

/// <param name="Changed">True when auto-tracking marked something, so the overlay should refresh its lists.</param>
/// <param name="Nearby">The closest available checks, nearest first, when the poll asked for them.</param>
/// <param name="Tracking">Whether auto-tracking is working: on, waiting, noLog, readOnly, unsupported, blocked or unavailable.</param>
/// <param name="TrackingMessage">The same, said for the player.</param>
/// <param name="PositionSupported">False when this game version's player position cannot be found at all.</param>
/// <param name="PlayedSeconds">The open run's played time, which changes every second while playing.</param>
public sealed record PollInfo(string Status, string Message, bool Changed, PlayerInfo? Player, GuideInfo? Guide, IReadOnlyList<NearbyInfo> Nearby,
    string Tracking, string TrackingMessage, bool PositionSupported, double PlayedSeconds = 0)
{
    /// <summary>Whether the inventory could be read at the last look (for drops); null before the first look.</summary>
    public bool? InventoryReadable { get; init; }

    /// <summary>How many enemies were loaded at the last look, for counting kills; null when they could not be read.</summary>
    public int? EnemiesLoaded { get; init; }

    /// <summary>How many of those had no HP left (corpses), which shows whether dead enemies stay long enough to count.</summary>
    public int? EnemiesDead { get; init; }

    /// <summary>Kills counted since the tracker started, and the last kind killed, for the log.</summary>
    public int KillsCounted { get; init; }

    public string? LastKill { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(SessionOptions))]
[JsonSerializable(typeof(Summary))]
[JsonSerializable(typeof(AllPlacesInfo))]
[JsonSerializable(typeof(IReadOnlyList<KillKindInfo>))]
[JsonSerializable(typeof(IReadOnlyList<AllCheckInfo>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(IReadOnlyList<CheckInfo>))]
[JsonSerializable(typeof(IReadOnlyList<ItemInfo>))]
[JsonSerializable(typeof(IReadOnlyList<SettingInfo>))]
[JsonSerializable(typeof(PollInfo))]
[JsonSerializable(typeof(IReadOnlyList<BackupInfo>))]
[JsonSerializable(typeof(IReadOnlyList<GoalKindInfo>))]
[JsonSerializable(typeof(IReadOnlyList<BossChoiceInfo>))]
[JsonSerializable(typeof(IReadOnlyList<UnlockInfo>))]
[JsonSerializable(typeof(IReadOnlyList<HistoryInfo>))]
[JsonSerializable(typeof(IReadOnlyList<FoundInfo>))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(IReadOnlyList<RegionInfo>))]
[JsonSerializable(typeof(ReportInfo))]
internal sealed partial class NativeJsonContext : JsonSerializerContext;
