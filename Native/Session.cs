using System.IO;
using System.Text.Json;
using ErRandoCompanion.Core.AllChecks;
using ErRandoCompanion.Core.Data;
using ErRandoCompanion.Core.Logic;
using ErRandoCompanion.Core.Models;
using ErRandoCompanion.Core.Navigation;
using ErRandoCompanion.Core.Persistence;
using ErRandoCompanion.Core.SpoilerLog;
using ErRandoCompanion.Core.Updates;
using ErRandoCompanion.GameReader;

namespace ErRandoCompanion.Native;

public sealed class SessionException(string message) : Exception(message);

/// <summary>
/// One run being tracked, for the mod: the same steps the app takes, built from the same Core code.
/// Uses the app's Seeds folder and file format, so a run can move between the two, but only one
/// should have a run open at a time. Not thread-safe; the exports serialise calls.
/// </summary>
public sealed class Session : IDisposable
{
    private const string FirstSeedName = "My Seed";
    private const string SignaturesFile = "signatures.json";
    private const string MapDataFile = "map_data.json";

    private readonly GameData _data;
    private readonly TrackerStore _store;
    private readonly RunOwnership _ownership;
    private readonly RunClock _clock = new();
    private int _polls;
    private readonly ReceivedItems _received = new();
    private readonly DeathCounter _deaths = new();
    private readonly KillCounter _killCounter = new();
    private readonly string _preferencesPath;
    private readonly string _modDirectory;
    private ReleaseInfo? _release;
    private string? _updateMessage;
    private bool _updateInstalled;
    private readonly RecentKills _recentKills = new();
    private int? _enemiesLoaded;
    private int? _enemiesDead;
    private int _killsCounted;
    private string? _lastKill;
    private GraceTracker? _graces;
    private IReadOnlyList<TimedEvent>? _bestSplits;
    private PlayerPosition? _lastPlayer;
    // The guide's grace hint, kept until the target or the found graces change.
    private (string Target, int Graces, (Grace Grace, double Distance)? Hint)? _graceHint;
    private bool? _inventoryReadable;
    private readonly GameConnection? _game;
    private readonly string? _randomizerDirectory;
    private readonly Dictionary<string, MapPin> _pins;
    private readonly Dictionary<string, Location> _locations;
    private readonly Dictionary<string, string> _regionNames;
    private readonly MapLinks _openWorldOnly = new([]);
    private TrackerState _state = new();
    private LogicEvaluator _logic;
    private LoadedSeed? _seed;
    private AutoTracker? _autoTracker;
    private string? _problem;

    // Kept apart from _problem, which opening a run clears.
    private string? _gameProblem;

    /// <exception cref="SessionException">The data files are missing or damaged.</exception>
    public Session(string dataDirectory, string seedsDirectory, string? randomizerDirectory, bool readOwnProcess)
    {
        try
        {
            _data = GameData.Load(dataDirectory);
        }
        catch (GameDataException e)
        {
            throw new SessionException($"Could not load the data files in {dataDirectory}: {e.Message}");
        }

        _store = new TrackerStore(seedsDirectory, GameSaves.FindActive);
        _ownership = new RunOwnership(seedsDirectory, "the in-game overlay");
        _locations = _data.Locations.ToDictionary(l => l.Id);
        _regionNames = _data.Regions.ToDictionary(r => r.Id, r => r.Name);
        _pins = LoadPins(dataDirectory);
        _game = LoadGame(dataDirectory, readOwnProcess);

        // The app remembers the randomizer folder; the mod is told it, as it lives there.
        _randomizerDirectory = randomizerDirectory
            ?? Preferences.Load(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(seedsDirectory))!, "preferences.json")).RandomizerDirectory;
        _preferencesPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(seedsDirectory))!, "preferences.json");
        _modDirectory = Path.GetDirectoryName(Path.GetFullPath(dataDirectory))!;
        Updates.CleanUp(_modDirectory);

        _logic = new LogicEvaluator(_data, _state);
        OpenStartupSeed();
    }

    public void Dispose()
    {
        _ownership.Dispose();
        _game?.Dispose();
    }

    public IReadOnlyList<string> SeedNames() => _store.SeedNames().Append(_state.SeedName).Distinct().ToList();

    /// <summary>Opens a saved run, or starts a new one under that name.</summary>
    public Summary OpenSeed(string name)
    {
        name = name.Trim();
        if (name.Length == 0)
            throw new SessionException("A run needs a name.");
        Show(LoadOrCreate(name));
        Save();
        return Summary();
    }

    public bool DeleteSeed(string name)
    {
        if (_ownership.IsOpenElsewhere(name))
            throw new SessionException($"'{name}' is open in the desktop app, so it cannot be deleted here.");
        var deleted = Store(() => _store.Delete(name));
        if (_state.SeedName == name)
            OpenStartupSeed();
        return deleted;
    }

    /// <summary>Every run's backups (deleted runs included), newest first within each run.</summary>
    public IReadOnlyList<BackupInfo> Backups() =>
        _store.RunsWithBackups()
            .SelectMany(_store.Backups)
            .Select(b => new BackupInfo(b.SeedName, b.Id, b.SavedAt.ToLocalTime().ToString("ddd d MMM, HH:mm"), b.Checked) { HasGameSave = b.HasGameSave })
            .ToList();

    /// <summary>Puts a backup back as its run's save and opens that run. What was there is backed up first.</summary>
    public Summary RestoreBackup(string name, string id)
    {
        if (_ownership.IsOpenElsewhere(name))
            throw new SessionException($"'{name}' is open in the desktop app, so restore it there or close it first.");
        if (name == _state.SeedName && _ownership.IsReadOnly)
            throw new SessionException("This run is open in the desktop app, so it cannot be restored here.");
        Show(Store(() => _store.Restore(name, id)));
        return Summary();
    }

    /// <summary>Links a spoiler log to the open run and copies its randomizer options into the settings.</summary>
    public Summary LoadSpoilerLog(string path)
    {
        _state.SpoilerLogPath = Path.GetFullPath(path);
        LoadSeed(applyOptions: true);
        Changed();
        return Summary();
    }

    /// <summary>
    /// Opens the run for the randomizer's newest seed: the run already linked to its spoiler log, a run for the
    /// same randomization made again (relinked to the new log), or else a new run.
    /// This is how the mod picks up a fresh randomize without the player doing anything.
    /// </summary>
    public Summary OpenNewestSeed()
    {
        if (_randomizerDirectory is null)
            throw new SessionException("The randomizer folder is not known, so its newest seed cannot be found.");
        var newest = SpoilerLogLocator.FindNewest(_randomizerDirectory)
            ?? throw new SessionException($"There are no spoiler logs in {Path.Combine(_randomizerDirectory, SpoilerLogLocator.LogsFolder)}.");

        if (SamePath(_state.SpoilerLogPath, newest))
            return Summary();

        var runs = _store.SeedNames().Select(TryLoad).OfType<TrackerState>().ToList();
        if (runs.FirstOrDefault(r => SamePath(r.SpoilerLogPath, newest)) is { } linked)
        {
            Show(linked);
            return Summary();
        }

        var log = SpoilerLogParser.Parse(File.ReadAllLines(newest), newest);
        if (runs.FirstOrDefault(r => SpoilerLogParser.TryParseFile(r.SpoilerLogPath)?.IsSameRandomization(log) == true) is { } same)
        {
            // Progress carries over: only the log it points to changes.
            same.SpoilerLogPath = Path.GetFullPath(newest);
            Show(same);
            Save();
            return Summary();
        }

        var baseName = string.IsNullOrEmpty(log.Seed) ? Path.GetFileNameWithoutExtension(newest) : $"Seed {log.Seed}";
        var unique = baseName;
        var names = SeedNames();
        for (var i = 2; names.Contains(unique); i++)
            unique = $"{baseName} ({i})";

        Show(TrackerState.Create(unique, _data));
        return LoadSpoilerLog(newest);
    }

    public Summary Summary()
    {
        var statuses = _data.Locations.Select(_logic.GetStatus).ToList();
        var open = RunReports.OpenCount(_data, _state, _logic);
        var active = _state.ActiveTargetLocation();
        return new Summary(
            _state.SeedName,
            new SpoilerInfo(_seed is not null, _seed?.Log.Seed, _seed?.Log.Version),
            statuses.Count(s => s == LocationStatus.Checked),
            statuses.Count(s => s != LocationStatus.Excluded),
            statuses.Count(s => s == LocationStatus.Available),
            _state.Targets.OrderBy(t => t.Value)
                .Select(t => new TargetInfo(t.Value, t.Key, _locations.TryGetValue(t.Key, out var l) ? l.Name : t.Key, t.Key == active))
                .ToList(),
            Join(_ownership.Notice, _seed?.Warning),
            _problem ?? _gameProblem,
            _state.PlayedSeconds,
            _state.FinishedAt is not null,
            RunGoals.Progress(_data, _state, _logic, _seed?.BossArenas) is { } goal
                ? new GoalInfo(goal.Kind, _state.Goal?.Count, goal.Description, goal.Done, goal.Needed, goal.Met)
                {
                    Bosses = goal.Bosses.Select(b => new GoalBossInfo(b.Id, b.Name, b.Defeated)).ToList(),
                }
                : null)
        {
            Open = open,
            Skipped = _state.Skipped.Count(id => _locations.TryGetValue(id, out var l) && _logic.GetStatus(l) == LocationStatus.Available),
            // Only while something is still locked, and not after the run is over.
            DeadEnd = open <= RunReports.DeadEndAt && _state.FinishedAt is null && statuses.Any(s => s == LocationStatus.Locked),
            Deaths = _state.Deaths.Values.Sum(),
            Splits = Splits(_state.Splits, _bestSplits),
        };
    }

    private static IReadOnlyList<SplitInfo> Splits(IReadOnlyList<TimedEvent> splits, IReadOnlyList<TimedEvent>? best) =>
        RunTimeline.Compare(splits, best).Select(s => new SplitInfo(s.Number, s.Name, s.Seconds, s.Best)).ToList();

    /// <summary>Each region's checks by state, in the data's order.</summary>
    private static Version CurrentVersion => Updates.CurrentOf(typeof(Session).Assembly);

    /// <summary>
    /// Looks on GitHub for a newer release, at most about once a day unless <paramref name="force"/>d, sharing what it
    /// finds with the desktop app through the preferences. Off when the player turned update checks off.
    /// </summary>
    public UpdateInfo CheckUpdate(bool force)
    {
        var preferences = Preferences.Load(_preferencesPath);
        if (preferences.CheckForUpdates
            && (force || preferences.LastUpdateCheck is not { } last || DateTime.UtcNow - last >= Updates.CheckEvery))
        {
            try
            {
                using var client = Updates.Client();
                _release = Updates.LatestAsync(client).GetAwaiter().GetResult();
                preferences.LastUpdateCheck = DateTime.UtcNow;
                preferences.LatestVersion = _release.Tag;
                preferences.LatestPage = _release.Page;
                preferences.Save(_preferencesPath);
                _updateMessage = null;
            }
            catch (UpdateException e)
            {
                _updateMessage = force ? e.Message : null;
            }
        }
        return UpdateStatus(preferences);
    }

    /// <summary>Downloads the newest overlay and puts it in place; the game uses it from its next start.</summary>
    public UpdateInfo InstallUpdate()
    {
        var preferences = Preferences.Load(_preferencesPath);
        try
        {
            using var client = Updates.Client();
            _release ??= Updates.LatestAsync(client).GetAwaiter().GetResult();
            if (_release.Version <= CurrentVersion)
                throw new UpdateException("This is already the newest version.");
            if (_release.Mod is not { } asset)
                throw new UpdateException("The newest release has no overlay to download.");
            var zip = Updates.DownloadAsync(client, asset).GetAwaiter().GetResult();
            Updates.Install(zip, _modDirectory);
            _updateInstalled = true;
            _updateMessage = $"Updated to version {_release.Version}. Restart the game to use it.";
        }
        catch (UpdateException e)
        {
            _updateMessage = e.Message;
        }
        return UpdateStatus(preferences);
    }

    public UpdateInfo SetCheckForUpdates(bool enabled)
    {
        var preferences = Preferences.Load(_preferencesPath);
        preferences.CheckForUpdates = enabled;
        preferences.Save(_preferencesPath);
        return enabled ? CheckUpdate(force: true) : UpdateStatus(preferences);
    }

    private UpdateInfo UpdateStatus(Preferences preferences)
    {
        var latest = _release?.Tag ?? preferences.LatestVersion;
        var newer = Updates.Parse(latest ?? "") is { } version && version > CurrentVersion;
        return new UpdateInfo(CurrentVersion.ToString(), latest, newer && !_updateInstalled, _release?.Page ?? preferences.LatestPage,
            _updateMessage, _updateInstalled, preferences.CheckForUpdates);
    }

    /// <summary>The full check list's places, or why there is none.</summary>
    public AllPlacesInfo AllPlaces()
    {
        if (_seed?.All is not { } all)
        {
            var why = _seed is null
                ? "Load this run's spoiler log to see every check."
                : @"The randomizer folder has no diste\Base\itemslots.txt or annotations.txt, so the full list cannot be made.";
            return new AllPlacesInfo(why, 0, 0, 0, 0, []);
        }
        var logic = new AllChecksLogic(all.Logic, all.Options, _data, _state, all.KnownItems);
        var statuses = all.Checks.Select(c => (c.Place, Status: logic.StatusOf(c))).ToList();
        var byPlace = statuses.ToLookup(s => s.Place);
        return new AllPlacesInfo(null,
            statuses.Count(s => s.Status == LocationStatus.Checked), statuses.Count, statuses.Count(s => s.Status == LocationStatus.Available),
            all.Checks.Count(c => !c.Trackable),
            all.Places.Select(p => new AllPlaceInfo(p, byPlace[p].Count(s => s.Status == LocationStatus.Checked), byPlace[p].Count(),
                byPlace[p].Count(s => s.Status == LocationStatus.Available))).ToList())
        {
            Overdue = all.Checks.Count(c => all.OddsOf(c, _state)?.IsOverdue == true),
        };
    }

    /// <summary>
    /// Every enemy kind killed in this run, most killed first, with the drops of every enemy of a kind that it can
    /// give and their odds. Says what each drop originally was, never what the seed put there.
    /// </summary>
    public IReadOnlyList<KillKindInfo> KillKinds()
    {
        var all = _seed?.All;
        var logic = all is null ? null : new AllChecksLogic(all.Logic, all.Options, _data, _state, all.KnownItems);
        var kinds = _state.Kills.GroupBy(k => _state.KillKinds.GetValueOrDefault(k.Key) ?? $"Enemy {k.Key}");
        return kinds
            .Select(kind =>
            {
                var npcs = kind.Select(k => k.Key).ToList();
                var drops = all is null || logic is null
                    ? []
                    : AllCheckInfos(all, logic, all.Checks.Where(c => npcs.Any(n => all.CouldDrop(n, c, _state))));
                return new KillKindInfo(kind.Key, kind.Sum(k => k.Value), drops);
            })
            .OrderByDescending(k => k.Kills)
            .ThenBy(k => k.Kind, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The checks of one place in the full list, in the randomizer's order.</summary>
    public IReadOnlyList<AllCheckInfo> AllChecks(string place)
    {
        if (_seed?.All is not { } all)
            return [];
        var logic = new AllChecksLogic(all.Logic, all.Options, _data, _state, all.KnownItems);
        return AllCheckInfos(all, logic, all.Checks.Where(c => c.Place == place));
    }

    private IReadOnlyList<AllCheckInfo> AllCheckInfos(AllChecksSeed all, AllChecksLogic logic, IEnumerable<SeedCheck> checks)
    {
        return checks
            .Select(c => (Check: c, Odds: all.OddsOf(c, _state)))
            .Select(x => new AllCheckInfo(x.Check.Id, x.Check.Slot.Description, x.Check.Slot.Kind, x.Check.Slot.Replaces, x.Check.Slot.Missable,
                !x.Check.Trackable, Camel(logic.StatusOf(x.Check).ToString()))
            {
                Odds = x.Odds?.Text(),
                Overdue = x.Odds?.IsOverdue == true,
            })
            .ToList();
    }

    public Summary SetAllChecked(string id, bool isChecked)
    {
        if (_seed?.All is not { } all || all.Checks.All(c => c.Id != id))
            throw new SessionException($"There is no check '{id}' in the full list.");
        if (isChecked ? _state.AllChecked.Add(id) : _state.AllChecked.Remove(id))
            Save();
        return Summary();
    }

    public IReadOnlyList<RegionInfo> Regions() =>
        RunReports.Regions(_data, _state, _logic).Select(r => new RegionInfo(r.Region, r.Open, r.Skipped, r.Locked, r.Done, r.Total)).ToList();

    /// <summary>A run in numbers: the open run, or a saved one by name (for the history list).</summary>
    public ReportInfo Report(string? name)
    {
        var state = name is null || name == _state.SeedName
            ? _state
            : Store(() => _store.Load(name)) ?? throw new SessionException($"There is no run called '{name}'.");
        var logic = state == _state ? _logic : new LogicEvaluator(_data, state);
        var report = RunReports.Build(_data, state, logic);
        return new ReportInfo(
            state.SeedName,
            state.Goal is { } goal ? RunHistory.Describe(goal) : "No goal",
            state.FinishedAt is not null,
            report.PlayedSeconds,
            report.Checked,
            report.Total,
            report.BossesDefeated,
            report.DeathCount,
            report.Deaths.Select(d => new PlaceCount(d.Place, d.Count)).ToList(),
            report.Slowest is { } s ? new StretchInfo(s.From, s.To, s.EndedWith) : null,
            report.KeyItems.Select(k => new TimedInfo(k.Name, k.Seconds)).ToList(),
            Splits(report.Splits, state == _state ? _bestSplits : null))
        {
            Kills = KillCounter.ByKind(state).Select(k => new PlaceCount(k.Kind, k.Kills)).ToList(),
        };
    }

    /// <summary>Puts a check aside for now, or back.</summary>
    public Summary ToggleSkip(string id)
    {
        RequireLocation(id);
        if (!_state.Skipped.Remove(id))
            _state.Skipped.Add(id);
        Changed();
        return Summary();
    }

    /// <summary>Skips the nearest available check (from the last poll's position).</summary>
    public Summary SkipNearest()
    {
        if (_lastPlayer is not { } player)
            throw new SessionException("The player's position is not known, so there is no nearest check.");
        var nearest = Nearest(player, 1).FirstOrDefault()
            ?? throw new SessionException("There is no available check nearby to skip.");
        _state.Skipped.Add(nearest.Id);
        Changed();
        return Summary();
    }

    /// <param name="filter">all, available, locked, checked, or excluded. "all" leaves out checks the settings exclude.</param>
    public IReadOnlyList<CheckInfo> Checks(string filter)
    {
        var wanted = filter.ToLowerInvariant() switch
        {
            "all" => null,
            "available" => LocationStatus.Available,
            "locked" => LocationStatus.Locked,
            "checked" => LocationStatus.Checked,
            "excluded" => (LocationStatus?)LocationStatus.Excluded,
            _ => throw new SessionException($"Unknown filter '{filter}': use all, available, locked, checked or excluded."),
        };

        return _data.Locations
            .Select(l => (Location: l, Status: _logic.GetStatus(l)))
            .Where(c => wanted is null ? c.Status != LocationStatus.Excluded : c.Status == wanted)
            .Select(c => new CheckInfo(
                c.Location.Id,
                c.Location.Name,
                _regionNames.GetValueOrDefault(c.Location.Region, c.Location.Region),
                Camel(c.Location.Category.ToString()),
                Camel(c.Status.ToString()),
                c.Location.Details,
                _state.Targets.TryGetValue(c.Location.Id, out var number) ? number : null,
                _pins.ContainsKey(c.Location.Id))
            {
                Skipped = _state.Skipped.Contains(c.Location.Id),
            })
            .ToList();
    }

    public IReadOnlyList<ItemInfo> Items() =>
        _data.Items
            .Select(i => new ItemInfo(i.Name, i.Category, _state.Items.GetValueOrDefault(i.Name), i.MaxCount, _logic.IsItemRelevant(i), i.Shortcut))
            .ToList();

    public IReadOnlyList<SettingInfo> Settings() =>
        _data.Settings
            .Select(s => new SettingInfo(s.Id, s.Name, Camel(s.Type.ToString()), _state.Settings.GetValueOrDefault(s.Id, s.DefaultText), s.Options, s.OptionNames, s.Max))
            .ToList();

    public Summary SetChecked(string id, bool isChecked)
    {
        RequireLocation(id);
        if (isChecked)
            _state.CheckedLocations.Add(id);
        else
            _state.CheckedLocations.Remove(id);
        Changed();
        return Summary();
    }

    /// <param name="number">1 to 5, or null to clear the check's target.</param>
    public Summary SetTarget(string id, int? number)
    {
        RequireLocation(id);
        if (number is < 1 or > TrackerState.MaxTargets)
            throw new SessionException($"Targets are numbered 1 to {TrackerState.MaxTargets}.");
        _state.SetTarget(id, number);
        Changed();
        return Summary();
    }

    /// <summary>Chooses the target the guide follows.</summary>
    public Summary SetActiveTarget(int number)
    {
        _state.ActiveTarget = number;
        Changed();
        return Summary();
    }

    public Summary SetItem(string name, int count)
    {
        var item = _data.Items.FirstOrDefault(i => i.Name == name) ?? throw new SessionException($"There is no item called '{name}'.");
        _state.Items[item.Name] = Math.Clamp(count, 0, item.MaxCount);
        Changed();
        return Summary();
    }

    public Summary SetSetting(string id, string value)
    {
        var setting = _data.Settings.FirstOrDefault(s => s.Id == id) ?? throw new SessionException($"There is no setting called '{id}'.");
        var valid = setting.Type switch
        {
            SettingType.Bool => value is "true" or "false",
            SettingType.Count => int.TryParse(value, out var n) && n >= 0 && n <= setting.Max,
            _ => setting.Options.Contains(value),
        };
        if (!valid)
            throw new SessionException($"'{value}' is not a value setting '{id}' can take.");
        _state.Settings[id] = value;
        Changed();
        return Summary();
    }

    /// <summary>Reads the game once: auto-tracks flags, and finds the player and the way to the active target.</summary>
    /// <param name="nearby">How many of the closest available checks to include, 0 for none.</param>
    public PollInfo Poll(int nearby = 0)
    {
        // Read-only while the desktop app has this run open: follow its saves, and take over when it closes.
        var reloaded = false;
        if (_ownership.Check() is RunChange.Saved or RunChange.TookOver && TryLoad(_state.SeedName) is { } fresh)
        {
            Show(fresh);
            reloaded = true;
        }

        if (_game is null)
        {
            var (off, offMessage) = Tracking(null);
            return new PollInfo("unavailable", _gameProblem ?? "Reading the game is unavailable.", reloaded, null, null, [], off, offMessage, false, _state.PlayedSeconds);
        }

        // Every few polls the pickups are read too: thousands of flags, so not every second.
        var pickups = _seed?.Pickups ?? new Dictionary<uint, Pickup>();
        var scanPickups = pickups.Count > 0 && !_ownership.IsReadOnly && _polls++ % PickupLog.ScanEvery == 0;
        var flags = _autoTracker?.PendingFlags() ?? [];
        var graceFlags = scanPickups && _graces is not null ? _graces.Pending : [];
        var allFlags = scanPickups ? _seed?.All?.PendingFlags(_state) ?? [] : [];
        var result = _game.Poll(scanPickups ? flags.Concat(PickupLog.Pending(_state, pickups)).Concat(graceFlags).Concat(allFlags).Distinct() : flags);
        if (result.Status == GameStatus.Connected && result.Player is not null)
            _graces?.Update(result.FlagsOn);
        _lastPlayer = result.Player;
        // Played time counts while a character is in the world; only the run's owner counts it.
        var inWorld = result.Status == GameStatus.Connected && result.Player is not null;
        if (!_ownership.IsReadOnly && _clock.Tick(_state, inWorld, DateTime.UtcNow))
            TrySave();
        // The owner auto-tracks; marking checks here too would only be lost at the next reload.
        var changed = result.Status == GameStatus.Connected && !_ownership.IsReadOnly && _autoTracker?.Apply(result.FlagsOn) == true;
        if (changed)
            Changed();
        // Kills: an enemy seen alive and then with no HP, counted by kind for the full list's drop odds.
        if (!_ownership.IsReadOnly && result.Status == GameStatus.Connected && result.Player is not null)
        {
            var enemies = _game.ReadEnemies();
            _enemiesLoaded = enemies?.Count;
            _enemiesDead = enemies?.Count(e => e.Hp <= 0);
            if (enemies is not null)
            {
                var kills = _killCounter.Update(enemies.Select(e => new EnemySighting(e.Handle, e.NpcParam, e.Model, e.Hp)));
                _recentKills.Add(kills, DateTime.UtcNow);
                _killsCounted += kills.Count;
                if (kills.Count > 0)
                    _lastKill = KillCounter.KindName(_seed?.ModelNames ?? new Dictionary<string, string>(), kills[^1].Model);
                if (KillCounter.Record(_state, kills, _seed?.ModelNames ?? new Dictionary<string, string>()))
                {
                    TrySave();
                    changed = true;
                }
            }
        }
        else if (result.Player is null)
        {
            _killCounter.Reset();
        }
        // Deaths, where they happen. The count is one number, so it is read every poll, not to miss the place.
        if (!_ownership.IsReadOnly && result.Status == GameStatus.Connected)
        {
            var place = result.Player is { } standing ? _seed?.MapNames.GetValueOrDefault(standing.Map.ToString()) : null;
            if (_deaths.Update(_state, _game.ReadDeathCount(), place, inWorld, DateTime.UtcNow))
            {
                TrySave();
                changed = true;
            }
        }
        // Only in the world: on the title screen the flags are not this character's.
        if (scanPickups && result.Status == GameStatus.Connected && result.Player is { } who)
        {
            var now = DateTime.UtcNow;
            var mapName = _seed!.MapNames.GetValueOrDefault(who.Map.ToString());
            var before = _state.Found.Count;
            var added = PickupLog.Record(_state, pickups, result.FlagsOn, now, mapName);
            // Then whatever else arrived in the inventory: random drops, purchases, crafting.
            var inventory = _game.ReadInventory();
            _inventoryReadable = inventory is not null;
            if (inventory is not null)
            {
                var gains = _received.Compare(inventory.Held, inventory.Stored);
                if (gains.Count > 0)
                    added += PickupLog.RecordReceived(_state, gains, _seed.ItemNames, PickupLog.Given(_state.Found.Skip(before)), now, mapName);
            }
            // The full list last: its drops are ticked from what was just received, and from kills just before.
            added += _seed.All?.Apply(_state, result.FlagsOn) ?? 0;
            var drops = _state.Found.Skip(before).Where(f => f.Source == FoundItem.DropOrOther).SelectMany(f => f.Items).ToList();
            if (drops.Count > 0)
                added += _seed.All?.ApplyDrops(_state, drops, _recentKills.Kinds(now)) ?? 0;
            // Items no flag explained: in someone else's world (Seamless Co-op) pickups set no flags, so the
            // checklist and key items are ticked from what arrives instead.
            if (drops.Count > 0)
            {
                var near = ReceivedTracker.DistanceFrom(who.Map, who.X, who.Y, who.Z, _pins, _seed.Links);
                var onMap = _seed.All?.ApplyOnMap(_state, drops, who.Map.ToString()) ?? 0;
                if (_seed.Received?.Apply(_state, drops, near) == true || onMap > 0)
                {
                    Changed();
                    added++;
                }
            }
            if (added > 0)
            {
                TrySave();
                changed = true;
            }
        }
        else if (result.Player is null)
        {
            // Out of the world: the next look in is a fresh starting point (it may be another save).
            _received.Reset();
            _graces?.Reset();
            _graceHint = null;
        }
        changed |= reloaded;

        var message = result.Status switch
        {
            GameStatus.Connected when _autoTracker is null => "Connected to Elden Ring. Load this run's spoiler log to start auto-tracking.",
            GameStatus.Connected => "Connected to Elden Ring: tracking automatically",
            GameStatus.NotRunning => "Waiting for Elden Ring to start",
            GameStatus.AntiCheatActive => "Easy Anti-Cheat is running, so the game is not being read. Launch through the randomizer's mod loader.",
            GameStatus.AccessDenied => "Elden Ring is running but could not be read.",
            GameStatus.Unsupported => "This version of Elden Ring is not supported, so the game is not being read.",
            _ => "Elden Ring is loading",
        };
        var (tracking, trackingMessage) = Tracking(result.Status);

        var player = result.Player is { } p ? new PlayerInfo(p.Map.ToString(), p.X, p.Y, p.Z) : null;
        return new PollInfo(Camel(result.Status.ToString()), message, changed, player, result.Player is { } at ? GuideFrom(at) : null,
            result.Player is { } here && nearby > 0 ? Nearest(here, nearby) : [], tracking, trackingMessage, result.PositionSupported, _state.PlayedSeconds)
        {
            InventoryReadable = _inventoryReadable,
            EnemiesLoaded = _enemiesLoaded,
            EnemiesDead = _enemiesDead,
            KillsCounted = _killsCounted,
            LastKill = _lastKill,
        };
    }

    /// <summary>Whether auto-tracking is working, and if not, why, in words for the player.</summary>
    /// <param name="status">The game reader's status, or null when the game cannot be read at all.</param>
    private (string State, string Message) Tracking(GameStatus? status)
    {
        if (_ownership.IsReadOnly)
            return ("readOnly", $"Auto-tracking: done by {_ownership.HeldBy}, which has this run open.");
        if (_autoTracker is null)
        {
            return ("noLog", _state.SpoilerLogPath is null
                ? "Auto-tracking: off. This run has no spoiler log; use \"Open the randomizer's newest seed\" in the Runs tab."
                : "Auto-tracking: off. This run's spoiler log could not be loaded.");
        }
        return status switch
        {
            null => ("unavailable", $"Auto-tracking: off. {_gameProblem ?? "The game cannot be read."}"),
            GameStatus.Connected => ("on", "Auto-tracking: on"),
            GameStatus.Unsupported => ("unsupported", "Auto-tracking: off. This game version is not supported (usually after a game update); Data\\signatures.json needs updating."),
            GameStatus.AntiCheatActive => ("blocked", "Auto-tracking: off. Easy Anti-Cheat is running."),
            GameStatus.AccessDenied => ("blocked", "Auto-tracking: off. The game could not be read."),
            _ => ("waiting", "Auto-tracking: waiting for the game to finish loading."),
        };
    }

    /// <summary>The closest available checks the guide can point to, nearest first. Names only, never contents.</summary>
    private IReadOnlyList<NearbyInfo> Nearest(PlayerPosition player, int count)
    {
        var links = _seed?.Links ?? _openWorldOnly;
        return _data.Locations
            .Where(l => _pins.ContainsKey(l.Id) && !_state.Skipped.Contains(l.Id) && _logic.GetStatus(l) == LocationStatus.Available)
            .Select(l => (Location: l, Way: Core.Navigation.Guide.For(player.Map, player.X, player.Y, player.Z, _pins[l.Id], links)))
            .Where(c => c.Way.Mode is not (GuideMode.OtherRealm or GuideMode.Unknown))
            .OrderBy(c => c.Way.Distance)
            .Take(Math.Min(count, 20))
            .Select(c => new NearbyInfo(c.Location.Id, c.Location.Name, Camel(c.Way.Mode.ToString()), c.Way.Dx, c.Way.Dz, c.Way.Distance))
            .ToList();
    }

    private GuideInfo? GuideFrom(PlayerPosition player)
    {
        if (_state.ActiveTargetLocation() is not { } id || !_pins.TryGetValue(id, out var pin))
            return null;

        // Without a loaded seed there is no regulation to link dungeons, so only the open world can be guided.
        var guide = Core.Navigation.Guide.For(player.Map, player.X, player.Y, player.Z, pin, _seed?.Links ?? _openWorldOnly);
        // The nearest found grace to the target, worked out again only when the target or the graces found change.
        if (_graces is null)
            _graceHint = null;
        else if (_graceHint is not { } cached || cached.Target != id || cached.Graces != _graces.Found.Count)
            _graceHint = (id, _graces.Found.Count, Grace.Nearest(_graces.Graces, _graces.Found, pin));
        var hint = _graceHint?.Hint;
        // Walk from here, or warp to a found grace and walk from there, whichever is shorter.
        var route = RoutePlanner.Plan(guide, pin, _graces?.Graces ?? [], _graces?.Found ?? new HashSet<uint>());
        return new GuideInfo(id, _locations[id].Name, Camel(guide.Mode.ToString()), guide.Dx, guide.Dy, guide.Dz, guide.Distance)
        {
            Grace = hint?.Grace.Name,
            GraceDistance = hint?.Distance,
            WarpTo = route?.WarpTo?.Name,
            Walk = route?.Walk,
            Rise = guide.Mode is GuideMode.SameMap or GuideMode.Direct ? guide.Dy : null,
        };
    }

    private void OpenStartupSeed()
    {
        // A damaged save must not block the mod, or be overwritten, so fall through to the next one.
        var problems = new List<string>();
        foreach (var name in _store.SeedNames().Append(FirstSeedName))
        {
            try
            {
                Show(_store.Load(name) ?? TrackerState.Create(name, _data));
                _problem = problems.Count > 0 ? string.Join(Environment.NewLine, problems) : _problem;
                return;
            }
            catch (TrackerStoreException e)
            {
                problems.Add(e.Message);
            }
        }
        Show(TrackerState.Create($"{FirstSeedName} {DateTime.Now:yyyy-MM-dd HH-mm-ss}", _data));
        _problem = string.Join(Environment.NewLine, problems);
    }

    private TrackerState LoadOrCreate(string name) => Store(() => _store.Load(name)) ?? TrackerState.Create(name, _data);

    private TrackerState? TryLoad(string name)
    {
        try
        {
            return _store.Load(name);
        }
        catch (TrackerStoreException)
        {
            return null;
        }
    }

    private static bool SamePath(string? a, string b) =>
        a is not null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private void Show(TrackerState state)
    {
        state.ApplyMissingDefaults(_data);
        _state = state;
        _ownership.Open(state.SeedName);
        _clock.Restart();
        _logic = new LogicEvaluator(_data, state);
        _problem = null;
        LoadSeed(applyOptions: false);
        // A run whose goal is already met is finished, so its clock does not run on.
        RunGoals.Update(state, RunGoals.Progress(_data, state, _logic, _seed?.BossArenas), DateTime.UtcNow);
        _bestSplits = BestSplits();
        if (RunTimeline.Update(_data, state, _logic, _seed?.BossArenas) && !_ownership.IsReadOnly)
            TrySave();
    }

    private IReadOnlyList<TimedEvent>? BestSplits()
    {
        try
        {
            return RunHistory.BestSplits(RunHistory.Read(_store), _state.Goal, _state.SeedName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void LoadSeed(bool applyOptions)
    {
        _seed = null;
        _autoTracker = null;
        _graces = null;
        if (_state.SpoilerLogPath is not { } path)
            return;

        try
        {
            _seed = SeedLoader.Load(_data, path);
            if (applyOptions)
                _seed.Log.ApplyOptionsTo(_state);

            // Only a run linked to its own log is tracked, so another run cannot be filled with this save's progress.
            _autoTracker = new AutoTracker(_data, _state, _seed.Flags);
            // Older entries may hold the randomizer's placeholder text for a place.
            if (PickupLog.Repair(_state, _seed.Pickups) > 0 && !_ownership.IsReadOnly)
                TrySave();
            _graces = new GraceTracker(_seed.Graces);
            _graceHint = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SpoilerLogException)
        {
            _problem = $"The spoiler log could not be loaded: {e.Message}";
        }
    }

    private void Changed()
    {
        _state.ClearCheckedTargets();
        _logic.Invalidate();
        // Meeting the goal finishes the run, which stops its clock.
        RunGoals.Update(_state, RunGoals.Progress(_data, _state, _logic, _seed?.BossArenas), DateTime.UtcNow);
        RunTimeline.Update(_data, _state, _logic, _seed?.BossArenas);
        Save();
    }

    /// <summary>What each missing key or shortcut item would open now, biggest first.</summary>
    public IReadOnlyList<UnlockInfo> Unlocks() =>
        UnlockForecast.Compute(_data, _state).Select(u => new UnlockInfo(u.Item, u.Opens, u.Regions)).ToList();

    /// <summary>Everything picked up in this run, newest first (pickups from before tracking last). Only what was taken.</summary>
    public IReadOnlyList<FoundInfo> Found() =>
        Enumerable.Reverse(_state.Found)
            .OrderBy(f => f.At is null ? 1 : 0)
            .Select(f => new FoundInfo(
                f.Items, f.Kinds, f.Where, f.Map,
                f.At?.ToLocalTime().ToString("ddd d MMM, HH:mm"),
                f.At is null ? null : RunClock.Format(f.PlayedSeconds))
            {
                Source = f.Source,
            })
            .ToList();

    /// <summary>Finished runs, newest first, with the best time per goal marked.</summary>
    public IReadOnlyList<HistoryInfo> History() =>
        RunHistory.Read(_store)
            .Select(r => new HistoryInfo(r.SeedName, r.Goal, r.FinishedAt.ToLocalTime().ToString("ddd d MMM yyyy, HH:mm"), r.PlayedSeconds, r.Checked, r.PerHour, r.Best)
            {
                Deaths = r.Deaths,
            })
            .ToList();

    /// <summary>Every boss that can be chosen for the goal, major bosses first.</summary>
    public IReadOnlyList<BossChoiceInfo> BossChoices() =>
        _data.Locations
            .Where(BossArenas.IsBoss)
            .OrderBy(l => l.Category == LocationCategory.MajorBoss ? 0 : 1)
            .ThenBy(l => _regionNames.GetValueOrDefault(l.Region, l.Region))
            .ThenBy(l => l.Name)
            .Select(l => new BossChoiceInfo(l.Id, l.Name, _regionNames.GetValueOrDefault(l.Region, l.Region), l.Category == LocationCategory.MajorBoss))
            .ToList();

    /// <summary>Adds a boss to the "defeat chosen bosses" goal, or takes it off; picks that goal if it is not the run's goal yet.</summary>
    public Summary ToggleGoalBoss(string id)
    {
        RequireLocation(id);
        if (_state.Goal?.Kind != RunGoals.ChosenBosses)
            _state.Goal = new RunGoal { Kind = RunGoals.ChosenBosses };
        if (!_state.Goal.Bosses.Remove(id))
            _state.Goal.Bosses.Add(id);
        Changed();
        return Summary();
    }

    public static IReadOnlyList<GoalKindInfo> GoalKinds() =>
        RunGoals.Kinds.Select(k => new GoalKindInfo(k.Kind, k.Name, k.Counts)).ToList();

    /// <summary>Sets what finishes the run. <paramref name="count"/> is for goals that count; null means all.</summary>
    public Summary SetGoal(string kind, int? count)
    {
        if (RunGoals.Kinds.All(k => k.Kind != kind))
            throw new SessionException($"'{kind}' is not a goal. Use one of: {string.Join(", ", RunGoals.Kinds.Select(k => k.Kind))}.");
        if (count is < 1)
            throw new SessionException("A goal needs at least 1.");
        // Changing only the count keeps the chosen bosses.
        _state.Goal = kind == RunGoals.None ? null : new RunGoal { Kind = kind, Count = count, Bosses = _state.Goal?.Bosses ?? [] };
        _bestSplits = BestSplits();
        Changed();
        return Summary();
    }

    private void Save() => Store(() =>
    {
        // Another front end owns this run: saving here would overwrite its progress.
        if (!_ownership.IsReadOnly)
            _store.Save(_state);
        return true;
    });

    /// <summary>Saves without failing the poll; a save that fails here is retried by the next one.</summary>
    private void TrySave()
    {
        try
        {
            Save();
        }
        catch (SessionException)
        {
        }
    }

    private static string? Join(params string?[] lines)
    {
        var present = lines.Where(l => !string.IsNullOrEmpty(l)).ToList();
        return present.Count > 0 ? string.Join(Environment.NewLine, present) : null;
    }

    private static T Store<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (TrackerStoreException e)
        {
            throw new SessionException(e.Message);
        }
    }

    private void RequireLocation(string id)
    {
        if (!_locations.ContainsKey(id))
            throw new SessionException($"There is no check called '{id}'.");
    }

    private static Dictionary<string, MapPin> LoadPins(string dataDirectory)
    {
        try
        {
            return MapData.Load(Path.Combine(dataDirectory, MapDataFile)).Pins.ToDictionary(p => p.Id);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // The checklist works without the map; only guidance needs it.
            return [];
        }
    }

    private GameConnection? LoadGame(string dataDirectory, bool readOwnProcess)
    {
        try
        {
            return new GameConnection(GameSignatures.Load(Path.Combine(dataDirectory, SignaturesFile)), readOwnProcess);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            _gameProblem = $"Reading the game is unavailable: {e.Message}";
            return null;
        }
    }

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];
}
