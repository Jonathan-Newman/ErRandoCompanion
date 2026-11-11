using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Threading;
using ErRandoCompanion.Core.AllChecks;
using ErRandoCompanion.Core.Data;
using ErRandoCompanion.Core.Logic;
using ErRandoCompanion.Core.Models;
using ErRandoCompanion.Core.Navigation;
using ErRandoCompanion.Core.Persistence;
using ErRandoCompanion.Core.SpoilerLog;
using ErRandoCompanion.GameReader;
using ReaderStatus = ErRandoCompanion.GameReader.GameStatus;

namespace ErRandoCompanion.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const string FirstSeedName = "My Seed";
    private const string SignaturesFile = "signatures.json";
    private const string MapDataFile = "map_data.json";
    private const string SeedsDirectoryOverride = "ER_COMPANION_SEEDS_DIR";
    private const int ChecklistTab = 0;
    private const int MapTab = 1;
    private const int HistoryTab = 3;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    // The randomizer writes its log in pieces, so wait for it to go quiet before reading it.
    private static readonly TimeSpan NewLogSettleTime = TimeSpan.FromSeconds(2);

    private readonly GameData? _data;
    private readonly TrackerStore _store;
    private readonly RunOwnership _ownership;
    private readonly RunClock _clock = new();
    private IReadOnlyDictionary<string, string>? _bossArenas;
    private IReadOnlyDictionary<uint, Pickup> _pickups = new Dictionary<uint, Pickup>();
    private IReadOnlyDictionary<string, string> _mapNames = new Dictionary<string, string>();
    private int _polls;
    private readonly ReceivedItems _received = new();
    private readonly DeathCounter _deaths = new();
    private GraceTracker? _graces;
    private IReadOnlyDictionary<string, MapPin> _pins = new Dictionary<string, MapPin>();
    private IReadOnlyList<TimedEvent>? _bestSplits;
    private string? _graceHint;
    private (string? Target, int Graces) _graceHintFor;
    private string? _deadEnd;
    private IReadOnlyDictionary<(int Category, int Id), string> _itemNames = new Dictionary<(int, int), string>();
    private string _foundFilter = "All";
    private string _foundSearch = "";
    private readonly DispatcherTimer _ownershipTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly GameConnection? _game;
    private readonly DispatcherTimer _timer = new() { Interval = PollInterval };
    private readonly DispatcherTimer _newLogTimer = new() { Interval = NewLogSettleTime };
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly string _preferencesPath;
    private readonly Preferences _preferences;
    private FileSystemWatcher? _logWatcher;
    private TrackerState _state = new();
    private LogicEvaluator? _logic;
    private AutoTracker? _autoTracker;
    private AllChecksSeed? _allSeed;
    private LocationFilter _filter = LocationFilter.Available;
    private bool _autoTrack = true;
    private bool _isPolling;
    private string _seedInput = "";
    private string _currentSeed = "";
    private string _summary = "";
    private string _gameStatus = "";
    private string _spoilerStatus = "";
    private string? _problem;
    private string? _readOnlyNotice;
    private int _mainTab = ChecklistTab;
    private int _doneCount;
    private int _totalCount;

    public MainViewModel()
    {
        // The override keeps test runs away from real saves, which auto-tracking would otherwise write to.
        var seedsDirectory = Environment.GetEnvironmentVariable(SeedsDirectoryOverride) ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ErRandoCompanion", "Seeds");
        _store = new TrackerStore(seedsDirectory, GameSaves.FindActive);
        _ownership = new RunOwnership(seedsDirectory, "the desktop app");
        _preferencesPath = Path.Combine(Path.GetDirectoryName(seedsDirectory)!, "preferences.json");
        _preferences = Preferences.Load(_preferencesPath);
        _newLogTimer.Tick += OnNewLogSettled;
        OpenSeed = new RelayCommand(OpenEnteredSeed, () => !string.IsNullOrWhiteSpace(_seedInput));
        AllChecks = new AllChecksViewModel(OnAllChecksTicked);

        var dataDirectory = Path.Combine(AppContext.BaseDirectory, "Data");
        try
        {
            _data = GameData.Load(dataDirectory);
        }
        catch (GameDataException e)
        {
            Summary = "Could not load the data files";
            Problem = e.Message;
            return;
        }

        var locationsByRegion = _data.Locations.ToLookup(l => l.Region);
        var mapData = LoadMapData(dataDirectory);
        var pinned = mapData?.Pins.Select(p => p.Id).ToHashSet() ?? [];
        _pins = mapData?.Pins.ToDictionary(p => p.Id) ?? [];

        // Regions keep their file order; routes have no locations and are left out.
        Regions = _data.Regions
            .Where(r => locationsByRegion.Contains(r.Id))
            .Select(r => new RegionGroupViewModel(
                r.Name,
                locationsByRegion[r.Id]
                    .Select(l => new LocationViewModel(l, r.Name, pinned.Contains(l.Id), SetLocationChecked, ShowOnMap, SetLocationTarget, FollowTarget, ToggleSkip))
                    .ToList()))
            .ToList();

        if (mapData is not null)
        {
            Map = new MapViewModel(
                mapData, new MapArtFiles(Path.Combine(Path.GetDirectoryName(seedsDirectory)!, "MapArt"), dataDirectory),
                () => _preferences.RandomizerDirectory, Regions.SelectMany(r => r.Locations).ToDictionary(l => l.Location.Id));
        }

        var items = _data.Items.Select(i => new ItemViewModel(i, SetItemCount)).ToList();
        KeyItems = items.Where(i => i.Item.Category == "key").ToList();
        ShortcutItems = items.Where(i => i.Item.Category != "key" && i.Item.Shortcut).ToList();
        OtherItems = items.Where(i => i.Item.Category != "key" && !i.Item.Shortcut).ToList();
        Settings = _data.Settings.Select(s => new SettingViewModel(s, SetSetting)).ToList();
        var regionNames = _data.Regions.ToDictionary(r => r.Id, r => r.Name);
        var bosses = _data.Locations.Where(BossArenas.IsBoss)
            .OrderBy(l => regionNames.GetValueOrDefault(l.Region, l.Region)).ThenBy(l => l.Name).ToList();
        BossChoiceViewModel Choice(Location l) =>
            new(l.Id, $"{l.Name}  ({regionNames.GetValueOrDefault(l.Region, l.Region)})", ToggleGoalBoss);
        MajorBossChoices = bosses.Where(l => l.Category == LocationCategory.MajorBoss).Select(Choice).ToList();
        OtherBossChoices = bosses.Where(l => l.Category != LocationCategory.MajorBoss).Select(Choice).ToList();

        OpenStartupSeed();
        WatchRandomizer();
        _ownershipTimer.Tick += OnOwnershipTick;
        _ownershipTimer.Start();

        _game = LoadGameConnection(dataDirectory);
        if (_game is not null)
        {
            _timer.Tick += OnPollTick;
            _timer.Start();
        }
    }

    public IReadOnlyList<RegionGroupViewModel> Regions { get; } = [];

    /// <summary>The All checks tab: every check the randomizer could fill in this seed.</summary>
    public AllChecksViewModel AllChecks { get; }
    public IReadOnlyList<ItemViewModel> KeyItems { get; } = [];
    public IReadOnlyList<ItemViewModel> ShortcutItems { get; } = [];
    public IReadOnlyList<ItemViewModel> OtherItems { get; } = [];
    public IReadOnlyList<SettingViewModel> Settings { get; } = [];
    public ObservableCollection<string> SeedNames { get; } = [];

    /// <summary>The current targets, in number order.</summary>
    public ObservableCollection<LocationViewModel> Targets { get; } = [];
    public ICommand OpenSeed { get; }

    /// <summary>Null when the map data could not be loaded; the checklist works without it.</summary>
    public MapViewModel? Map { get; }

    /// <summary>Asks the view to scroll a checklist row into view.</summary>
    public event Action<LocationViewModel>? ListFocusRequested;

    public int MainTab
    {
        get => _mainTab;
        set
        {
            Set(ref _mainTab, value);
            // History reads every run's save, so only when its tab is opened.
            if (value == HistoryTab)
                LoadHistory();
        }
    }

    /// <summary>What each missing key or shortcut item would open now. Logic only: never where an item is.</summary>
    public ObservableCollection<string> Unlocks { get; } = [];

    /// <summary>Everything picked up in this run, newest first, filtered by kind and search.</summary>
    public IReadOnlyList<FoundRow> Found { get; private set; } = [];

    public IReadOnlyList<string> FoundKinds { get; } = ["All", .. ItemPickups.Kinds, FoundItem.DropOrOther];

    public string FoundFilter
    {
        get => _foundFilter;
        set
        {
            Set(ref _foundFilter, value);
            RefreshFound();
        }
    }

    public string FoundSearch
    {
        get => _foundSearch;
        set
        {
            Set(ref _foundSearch, value);
            RefreshFound();
        }
    }

    private void RefreshFound()
    {
        // A new list rather than thousands of adds, so the view updates once.
        var rows = new List<FoundRow>();
        var search = _foundSearch.Trim();
        foreach (var found in Enumerable.Reverse(_state.Found).OrderBy(f => f.At is null ? 1 : 0))
        {
            if (_foundFilter == FoundItem.DropOrOther ? found.Source != FoundItem.DropOrOther
                : _foundFilter != "All" && !found.Kinds.Contains(_foundFilter))
            {
                continue;
            }
            var items = string.Join(", ", found.Items);
            if (search.Length > 0 && !items.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !found.Where.Contains(search, StringComparison.OrdinalIgnoreCase) && !found.Map.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var when = found.At is { } at ? $"{at.ToLocalTime():ddd d MMM, HH:mm} (at {RunClock.Format(found.PlayedSeconds)})" : "Before tracking started";
            var place = found.Map.Length == 0 ? found.Where : found.Where.Length == 0 ? found.Map : $"{found.Map}: {found.Where}";
            rows.Add(new FoundRow(items, place, when));
        }
        Found = rows;
        Raise(nameof(Found));
    }

    /// <summary>Finished runs, newest first; filled when the History tab opens.</summary>
    public ObservableCollection<HistoryRow> History { get; } = [];

    private void LoadHistory()
    {
        History.Clear();
        foreach (var run in RunHistory.Read(_store))
        {
            var pace = run.PerHour is { } p ? $", {p:0.#} an hour" : "";
            var deaths = run.Deaths > 0 ? $", {run.Deaths} deaths" : "";
            IReadOnlyList<ReportSection> report = [];
            try
            {
                if (_data is not null && _store.Load(run.SeedName) is { } state)
                    report = ReportSections(RunReports.Build(_data, state, new LogicEvaluator(_data, state)), null);
            }
            catch (TrackerStoreException)
            {
                // Listed without its summary.
            }
            History.Add(new HistoryRow(
                $"{run.SeedName}  -  {run.Goal}",
                $"{run.FinishedAt.ToLocalTime():ddd d MMM yyyy, HH:mm}  -  {RunClock.Format(run.PlayedSeconds)}, {run.Checked} checks{pace}{deaths}",
                run.Best,
                report));
        }
    }

    /// <summary>The Progress tab: what is left by region, then the run in numbers.</summary>
    public IReadOnlyList<ReportSection> Progress { get; private set; } = [];

    /// <summary>When few checks are open: which missing key items would open the most. Null otherwise.</summary>
    public string? DeadEnd
    {
        get => _deadEnd;
        private set
        {
            Set(ref _deadEnd, value);
            Raise(nameof(HasDeadEnd));
        }
    }

    public bool HasDeadEnd => _deadEnd is not null;

    /// <summary>The found Site of Grace nearest the active target, to warp to; null when unknown.</summary>
    public string? GraceHint
    {
        get => _graceHint;
        private set
        {
            Set(ref _graceHint, value);
            Raise(nameof(HasGraceHint));
        }
    }

    public bool HasGraceHint => _graceHint is not null;

    private void ToggleSkip(Location location)
    {
        if (!_state.Skipped.Remove(location.Id))
            _state.Skipped.Add(location.Id);
        Changed();
    }

    private void RefreshProgress(IReadOnlyList<ItemUnlock> unlocks)
    {
        if (_data is null || _logic is null)
            return;
        var regions = RunReports.Regions(_data, _state, _logic);
        var left = regions.Where(r => r.Done < r.Total)
            .Select(r => $"{r.Region}:  {r.Open} open{(r.Skipped > 0 ? $" (+{r.Skipped} skipped)" : "")},  {r.Locked} locked,  {r.Done} / {r.Total} done")
            .ToList();
        var finished = regions.Count(r => r.Total > 0 && r.Done == r.Total);
        if (finished > 0)
            left.Add($"{finished} regions finished.");
        Progress = [new ReportSection("What's left, by region", left), .. ReportSections(RunReports.Build(_data, _state, _logic), _bestSplits)];
        Raise(nameof(Progress));

        var open = RunReports.OpenCount(_data, _state, _logic);
        var locked = Regions.SelectMany(r => r.Locations).Any(l => l.Status == LocationStatus.Locked);
        if (open > RunReports.DeadEndAt || !locked || _state.FinishedAt is not null)
        {
            DeadEnd = null;
            return;
        }
        var skipped = _state.Skipped.Count(id => Regions.SelectMany(r => r.Locations).Any(l => l.Location.Id == id && l.Status == LocationStatus.Available));
        var orSkipped = skipped > 0 ? $", or try the {skipped} skipped" : "";
        var count = open switch { 0 => "Nothing open", 1 => "Only 1 open check", _ => $"Only {open} open checks" };
        var best = unlocks.Take(3).Select(u => $"{u.Item} (opens {u.Opens})").ToList();
        DeadEnd = best.Count == 0
            ? $"{count}. No single key item would open more{orSkipped}."
            : $"{count}. Most useful to find: {string.Join(", ", best)}{orSkipped}.";
    }

    /// <summary>A run in numbers, as titled lists of lines.</summary>
    private static IReadOnlyList<ReportSection> ReportSections(RunReport report, IReadOnlyList<TimedEvent>? best)
    {
        static string Time(double seconds) => seconds < 0 ? "before timing" : RunClock.Format(seconds);
        var summary = new List<string>
        {
            $"{RunClock.Format(report.PlayedSeconds)} played,  {report.Checked} / {report.Total} checks,  {report.BossesDefeated} bosses defeated,  {report.DeathCount} deaths",
        };
        if (report.Slowest is { } s)
            summary.Add($"Slowest stretch: {RunClock.Format(s.Seconds)} without a check ({RunClock.Format(s.From)} to {RunClock.Format(s.To)}), ended by {s.EndedWith}");
        var sections = new List<ReportSection> { new("The run in numbers", summary) };
        if (report.Splits.Count > 0)
        {
            var splits = RunTimeline.Compare(report.Splits, best).Select(x =>
            {
                var delta = x.Best is { } b ? $"   ({(x.Seconds - b < 0 ? "-" : "+")}{RunClock.Format(Math.Abs(x.Seconds - b))} on your best)" : "";
                return $"{x.Number}. {x.Name}   {Time(x.Seconds)}{delta}";
            }).ToList();
            sections.Add(new ReportSection("Splits", splits));
        }
        if (report.Deaths.Count > 0)
            sections.Add(new ReportSection("Deaths by place", report.Deaths.Select(d => $"{d.Count}   {d.Place}").ToList()));
        if (report.KeyItems.Count > 0)
            sections.Add(new ReportSection("Key items, in the order found", report.KeyItems.Select(k => $"{Time(k.Seconds)}   {k.Name}").ToList()));
        return sections;
    }

    /// <summary>Works out the grace hint again when the active target or the graces found have changed.</summary>
    private void RefreshGraceHint()
    {
        var target = _state.ActiveTargetLocation();
        var key = (target, _graces?.Found.Count ?? -1);
        if (key == _graceHintFor)
            return;
        _graceHintFor = key;
        GraceHint = target is not null && _graces is not null && _pins.TryGetValue(target, out var pin)
            && Grace.Nearest(_graces.Graces, _graces.Found, pin) is { } near
            ? $"Nearest grace to the active target: {near.Grace.Name} ({near.Distance:0} m from it)"
            : null;
    }

    public int DoneCount
    {
        get => _doneCount;
        private set => Set(ref _doneCount, value);
    }

    public int TotalCount
    {
        get => _totalCount;
        private set => Set(ref _totalCount, value);
    }

    public void ShowOnMap(LocationViewModel location)
    {
        MainTab = MapTab;
        Map?.Focus(location);
    }

    /// <summary>Opens the checklist at a location, widening the filter if it currently hides it.</summary>
    public void ShowInList(LocationViewModel location)
    {
        if (!location.IsVisible)
            Filter = LocationFilter.All;

        foreach (var row in Regions.SelectMany(r => r.Locations))
            row.IsHighlighted = row == location;
        Regions.First(r => r.Locations.Contains(location)).IsExpanded = true;

        MainTab = ChecklistTab;
        ListFocusRequested?.Invoke(location);
    }

    public LocationFilter Filter
    {
        get => _filter;
        set
        {
            Set(ref _filter, value);
            Refresh();
        }
    }

    public string SeedInput
    {
        get => _seedInput;
        set => Set(ref _seedInput, value);
    }

    public string CurrentSeed
    {
        get => _currentSeed;
        private set => Set(ref _currentSeed, value);
    }

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public string? Problem
    {
        get => _problem;
        private set => Set(ref _problem, value);
    }

    /// <summary>Raised with a message when the run's goal has just been met.</summary>
    public event Action<string>? RunCompleted;

    /// <summary>The bosses a "defeat chosen bosses" goal can name.</summary>
    public IReadOnlyList<BossChoiceViewModel> MajorBossChoices { get; } = [];
    public IReadOnlyList<BossChoiceViewModel> OtherBossChoices { get; } = [];

    public bool ChoosesBosses => GoalKind == RunGoals.ChosenBosses;

    private void ToggleGoalBoss(string id)
    {
        if (_state.Goal?.Kind != RunGoals.ChosenBosses)
            _state.Goal = new RunGoal { Kind = RunGoals.ChosenBosses };
        if (!_state.Goal.Bosses.Remove(id))
            _state.Goal.Bosses.Add(id);
        Changed();
        UpdateGoal();
    }

    public IReadOnlyList<GoalOption> GoalKinds { get; } = RunGoals.Kinds.Select(k => new GoalOption(k.Kind, k.Name)).ToList();

    /// <summary>The run's goal; changing it changes the run.</summary>
    public string GoalKind
    {
        get => _state.Goal?.Kind ?? RunGoals.None;
        set
        {
            if (value == GoalKind)
                return;
            _state.Goal = value == RunGoals.None ? null : new RunGoal { Kind = value, Count = _state.Goal?.Count, Bosses = _state.Goal?.Bosses ?? [] };
            Changed();
            UpdateGoal();
        }
    }

    /// <summary>How many, as typed; empty means all of them.</summary>
    public string GoalCount
    {
        get => _state.Goal?.Count?.ToString() ?? "";
        set
        {
            if (_state.Goal is null)
                return;
            int? count = int.TryParse(value, out var n) && n > 0 ? n : null;
            if (count == _state.Goal.Count)
                return;
            _state.Goal.Count = count;
            Changed();
            UpdateGoal();
        }
    }

    public bool GoalCounts => RunGoals.Kinds.FirstOrDefault(k => k.Kind == GoalKind).Counts;

    /// <summary>"Defeat 5 major bosses: 3 / 5", or "Run complete" once met.</summary>
    public string GoalProgress
    {
        get => _goalProgress;
        private set => Set(ref _goalProgress, value);
    }

    private string _goalProgress = "";

    private void UpdateGoal()
    {
        Raise(nameof(GoalKind));
        Raise(nameof(GoalCount));
        Raise(nameof(GoalCounts));
        Raise(nameof(ChoosesBosses));
        var progress = _data is null || _logic is null ? null : RunGoals.Progress(_data, _state, _logic, _bossArenas);
        var defeated = progress?.Bosses.ToDictionary(b => b.Id, b => b.Defeated) ?? [];
        var chosen = _state.Goal?.Bosses.ToHashSet() ?? [];
        foreach (var choice in MajorBossChoices.Concat(OtherBossChoices))
            choice.Update(chosen.Contains(choice.Id), defeated.GetValueOrDefault(choice.Id));
        GoalProgress = progress switch
        {
            null => "No goal: the run's timer keeps going until you pick one and meet it.",
            { Met: true } => $"Run complete: {progress.Description}. Time {RunClock.Format(_state.PlayedSeconds)}.",
            _ => $"{progress.Description}: {progress.Done} / {progress.Needed}",
        };
    }

    /// <summary>Set while the in-game overlay owns the open run: this window then follows it without saving.</summary>
    public string? ReadOnlyNotice
    {
        get => _readOnlyNotice;
        private set => Set(ref _readOnlyNotice, value);
    }

    public bool AutoTrack
    {
        get => _autoTrack;
        set
        {
            Set(ref _autoTrack, value);
            if (!value)
                GameStatus = "Auto-tracking is off";
        }
    }

    public string GameStatus
    {
        get => _gameStatus;
        private set => Set(ref _gameStatus, value);
    }

    /// <summary>Only ever says whether a log is loaded, never anything it contains about the seed's items.</summary>
    public string SpoilerStatus
    {
        get => _spoilerStatus;
        private set => Set(ref _spoilerStatus, value);
    }

    /// <summary>Raised with a new spoiler log's path and seed. The handler returns whether to start a run for it.</summary>
    public event Func<string, string, bool>? NewSeedDetected;

    public string? RandomizerDirectory => _preferences.RandomizerDirectory;

    public void Dispose()
    {
        _timer.Stop();
        _newLogTimer.Stop();
        _ownershipTimer.Stop();
        _ownership.Dispose();
        _logWatcher?.Dispose();
        _game?.Dispose();
    }

    /// <summary>Links a spoiler log to the current seed and copies its randomizer options into the settings.</summary>
    public void LoadSpoilerLog(string path)
    {
        if (_data is null)
            return;

        _state.SpoilerLogPath = path;
        Problem = null;
        LoadSeedFlags(applyOptions: true);
        _logic?.Invalidate();
        Refresh();
        Save();

        // Picking a log from the randomizer folder is also how that folder gets remembered.
        if (SpoilerLogLocator.RandomizerDirectoryOf(path) is { } randomizer)
        {
            if (SpoilerLogLocator.FindNewest(randomizer) == path)
                _preferences.LastOfferedLog = path;
            SetRandomizerDirectory(randomizer);
        }
    }

    /// <summary>Remembers the randomizer folder, watches it for new seeds, and offers its newest one straight away.</summary>
    public void SetRandomizerDirectory(string directory)
    {
        _preferences.RandomizerDirectory = directory;
        _preferences.Save(_preferencesPath);
        WatchRandomizer();
        OfferNewestSeed();
    }

    private void WatchRandomizer()
    {
        _logWatcher?.Dispose();
        _logWatcher = null;

        var directory = _preferences.RandomizerDirectory;
        if (directory is null && _state.SpoilerLogPath is { } linked)
            directory = _preferences.RandomizerDirectory = SpoilerLogLocator.RandomizerDirectoryOf(linked);
        if (directory is null)
            return;

        var logs = Path.Combine(directory, SpoilerLogLocator.LogsFolder);
        if (!Directory.Exists(logs))
            return;

        _logWatcher = new FileSystemWatcher(logs, "*.txt") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
        _logWatcher.Created += OnLogWritten;
        _logWatcher.Changed += OnLogWritten;
        _logWatcher.Renamed += OnLogWritten;
        _logWatcher.EnableRaisingEvents = true;
    }

    // Watcher events arrive off the UI thread; restarting the timer turns a burst of writes into one check.
    private void OnLogWritten(object sender, FileSystemEventArgs e) =>
        _dispatcher.BeginInvoke(() =>
        {
            _newLogTimer.Stop();
            _newLogTimer.Start();
        });

    private void OnNewLogSettled(object? sender, EventArgs e)
    {
        _newLogTimer.Stop();
        OfferNewestSeed();
    }

    /// <summary>Offers a seed randomized since it was last checked, such as while the app was closed.</summary>
    public void OfferNewestSeed()
    {
        if (_data is null || _preferences.RandomizerDirectory is not { } directory)
            return;

        var newest = SpoilerLogLocator.FindNewest(directory);
        if (newest is null || newest == _preferences.LastOfferedLog || newest == _state.SpoilerLogPath)
            return;

        ParsedSpoilerLog log;
        try
        {
            log = SpoilerLogParser.Parse(File.ReadAllLines(newest), newest);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SpoilerLogException)
        {
            // Most likely still being written; the next write event brings us back here.
            return;
        }

        _preferences.LastOfferedLog = newest;
        _preferences.Save(_preferencesPath);

        // Randomizing the same seed again gives the same items, so that run carries on, linked to the new log.
        if (FindSameRandomization(log) is { } same)
        {
            Problem = null;
            if (same.SeedName != _state.SeedName)
                Show(same);
            LoadSpoilerLog(newest);
            return;
        }

        if (NewSeedDetected?.Invoke(newest, log.Seed) != true)
            return;

        var name = UniqueSeedName(string.IsNullOrEmpty(log.Seed) ? Path.GetFileNameWithoutExtension(newest) : $"Seed {log.Seed}");
        Problem = null;
        Show(TrackerState.Create(name, _data));
        LoadSpoilerLog(newest);
    }

    private TrackerState? FindSameRandomization(ParsedSpoilerLog log)
    {
        if (SpoilerLogParser.TryParseFile(_state.SpoilerLogPath)?.IsSameRandomization(log) == true)
            return _state;

        foreach (var name in _store.SeedNames().Where(n => n != _state.SeedName))
        {
            TrackerState? saved;
            try
            {
                saved = _store.Load(name);
            }
            catch (TrackerStoreException)
            {
                continue;
            }
            if (SpoilerLogParser.TryParseFile(saved?.SpoilerLogPath)?.IsSameRandomization(log) == true)
                return saved;
        }
        return null;
    }

    private string UniqueSeedName(string name)
    {
        var unique = name;
        for (var i = 2; SeedNames.Contains(unique); i++)
            unique = $"{name} ({i})";
        return unique;
    }

    private void LoadSeedFlags(bool applyOptions)
    {
        SeedFlags? seed = null;
        _allSeed = null;
        string? noFullList = null;
        _bossArenas = null;
        _graces = null;
        _pickups = new Dictionary<uint, Pickup>();
        var path = _state.SpoilerLogPath;
        if (path is null)
        {
            SpoilerStatus = "No spoiler log linked to this seed";
        }
        else
        {
            try
            {
                var loaded = SeedLoader.Load(_data!, path);
                seed = loaded.Flags;
                _bossArenas = loaded.BossArenas;
                _pickups = loaded.Pickups;
                _mapNames = loaded.MapNames;
                _itemNames = loaded.ItemNames;
                _graces = new GraceTracker(loaded.Graces);
                _graceHintFor = (null, -2);
                _allSeed = loaded.All;
                noFullList = loaded.All is null
                    ? @"The randomizer folder has no diste\Base\itemslots.txt or annotations.txt, so the full list cannot be made."
                    : null;

                if (applyOptions)
                    loaded.Log.ApplyOptionsTo(_state);

                SpoilerStatus = $"Spoiler log loaded: seed {loaded.Log.Seed}, randomizer {loaded.Log.Version}. Contents stay hidden.";
                if (loaded.Warning is { } warning)
                    Problem = warning;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or SpoilerLogException)
            {
                SpoilerStatus = "Spoiler log not loaded";
                Problem = e.Message;
            }
        }

        // Only a seed linked to its log is tracked, so opening another seed cannot fill it with this save's progress.
        _autoTracker = seed is null ? null : new AutoTracker(_data!, _state, seed);
        AllChecks.Load(_allSeed, _data!, _state, noFullList);
        // Older entries may hold the randomizer's placeholder text for a place.
        if (seed is not null && PickupLog.Repair(_state, _pickups) > 0)
            Save();
    }

    private MapData? LoadMapData(string dataDirectory)
    {
        try
        {
            return MapData.Load(Path.Combine(dataDirectory, MapDataFile));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Problem = $"The map is unavailable: {e.Message}";
            return null;
        }
    }

    private GameConnection? LoadGameConnection(string dataDirectory)
    {
        try
        {
            return new GameConnection(GameSignatures.Load(Path.Combine(dataDirectory, SignaturesFile)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            GameStatus = $"Auto-tracking is unavailable: {e.Message}";
            return null;
        }
    }

    private async void OnPollTick(object? sender, EventArgs e)
    {
        if (_isPolling || !_autoTrack || _game is null)
            return;

        if (_autoTracker is null)
        {
            GameStatus = "Load this seed's spoiler log to start auto-tracking";
            return;
        }

        _isPolling = true;
        try
        {
            var game = _game;
            // Every few polls the pickups are read too: thousands of flags, so not every second.
            var pickups = _pickups;
            var scanPickups = pickups.Count > 0 && !_ownership.IsReadOnly && _polls++ % PickupLog.ScanEvery == 0;
            IEnumerable<uint> flags = _autoTracker.PendingFlags();
            if (scanPickups)
            {
                flags = flags.Concat(PickupLog.Pending(_state, pickups)).Concat(_graces?.Pending ?? [])
                    .Concat(_allSeed?.PendingFlags(_state) ?? []).Distinct().ToList();
            }
            var result = await Task.Run(() => game.Poll(flags));
            if (result.Status == ReaderStatus.Connected && result.Player is not null && _graces?.Update(result.FlagsOn) == true)
                RefreshGraceHint();

            if (!_autoTrack)
                return;

            GameStatus = result.Status switch
            {
                ReaderStatus.Connected => "Connected to Elden Ring: tracking automatically",
                ReaderStatus.NotRunning => "Waiting for Elden Ring to start",
                ReaderStatus.AntiCheatActive => "Easy Anti-Cheat is running, so the game is not being read. Launch through the randomizer's mod loader.",
                ReaderStatus.AccessDenied => "Elden Ring is running but could not be read. Try running this app as administrator.",
                ReaderStatus.Unsupported => "This version of Elden Ring is not supported (usually after a game update), so auto-tracking is off.",
                _ => "Elden Ring is loading",
            };

            // The owner of the run auto-tracks it; marking checks here too would only be lost at the next reload.
            if (result.Status == ReaderStatus.Connected && !_ownership.IsReadOnly && _autoTracker.Apply(result.FlagsOn))
                Changed();

            // Only in the world: on the title screen the flags are not this character's.
            if (scanPickups && result.Status == ReaderStatus.Connected && result.Player is { } who)
            {
                var now = DateTime.UtcNow;
                var mapName = _mapNames.GetValueOrDefault(who.Map.ToString());
                var before = _state.Found.Count;
                var added = PickupLog.Record(_state, pickups, result.FlagsOn, now, mapName);
                // Then whatever else arrived in the inventory: random drops, purchases, crafting.
                if (await Task.Run(game.ReadInventory) is { } inventory)
                {
                    var gains = _received.Compare(inventory.Held, inventory.Stored);
                    if (gains.Count > 0)
                        added += PickupLog.RecordReceived(_state, gains, _itemNames, PickupLog.Given(_state.Found.Skip(before)), now, mapName);
                }
                // The full list last: its drops are ticked from what was just received.
                if (_allSeed?.Apply(_state, result.FlagsOn) > 0)
                {
                    Save();
                    AllChecks.Refresh(_state);
                }
                if (added > 0)
                {
                    Save();
                    RefreshFound();
                }
            }
            else if (result.Player is null)
            {
                _received.Reset();
                _graces?.Reset();
            }

            // Deaths, where they happen: one number, so read every poll.
            if (!_ownership.IsReadOnly && result.Status == ReaderStatus.Connected)
            {
                var place = result.Player is { } standing ? _mapNames.GetValueOrDefault(standing.Map.ToString()) : null;
                var deaths = await Task.Run(game.ReadDeathCount);
                if (_deaths.Update(_state, deaths, place, result.Player is not null, DateTime.UtcNow))
                {
                    Save();
                    RefreshProgress(UnlockForecast.Compute(_data!, _state));
                }
            }

            // Played time counts while a character is in the world; only the run's owner counts it.
            var inWorld = result.Status == ReaderStatus.Connected && result.Player is not null;
            if (!_ownership.IsReadOnly && _clock.Tick(_state, inWorld, DateTime.UtcNow))
                Save();
            UpdateSummary();
        }
        finally
        {
            _isPolling = false;
        }
    }

    /// <summary>While read-only, shows the owner's saves as they happen, and takes over once it lets go.</summary>
    private void OnOwnershipTick(object? sender, EventArgs e)
    {
        if (_data is null || _ownership.Check() == RunChange.None)
            return;
        try
        {
            if (_store.Load(_state.SeedName) is { } fresh)
                Show(fresh);
        }
        catch (TrackerStoreException)
        {
            // Caught mid-write; the next tick reads it again.
        }
        ReadOnlyNotice = _ownership.Notice;
    }

    private void OpenStartupSeed()
    {
        // A damaged save must not block the app, or be overwritten, so fall through to the next seed.
        var problems = new List<string>();
        foreach (var name in _store.SeedNames().Append(FirstSeedName))
        {
            try
            {
                Show(_store.Load(name) ?? TrackerState.Create(name, _data!));
                break;
            }
            catch (TrackerStoreException e)
            {
                problems.Add(e.Message);
            }
        }

        // Only reachable when every save, including the default seed's, is damaged.
        if (_logic is null)
            Show(TrackerState.Create($"{FirstSeedName} {DateTime.Now:yyyy-MM-dd HH-mm-ss}", _data!));
        if (problems.Count > 0)
            Problem = string.Join(Environment.NewLine, problems);
    }

    private void OpenEnteredSeed()
    {
        if (_data is null)
            return;

        var name = _seedInput.Trim();
        try
        {
            var state = _store.Load(name) ?? TrackerState.Create(name, _data);
            Problem = null;
            Show(state);
            Save();
        }
        catch (TrackerStoreException e)
        {
            // Stay on the current seed so the damaged file is left for the player to recover.
            Problem = e.Message;
        }
    }

    /// <summary>The saved seed the delete button would remove, or null when the entered name has no save.</summary>
    public string? DeletableSeed
    {
        get
        {
            var name = _seedInput.Trim();
            return SeedNames.Contains(name) ? name : null;
        }
    }

    /// <summary>Every run's backups, deleted runs included, newest first within each run.</summary>
    public IReadOnlyList<RunBackup> Backups() => _store.RunsWithBackups().SelectMany(_store.Backups).ToList();

    /// <summary>Puts a backup back as its run's save and opens that run. Returns why it could not, or null.</summary>
    public string? RestoreBackup(RunBackup backup)
    {
        if (_data is null)
            return "The data files are not loaded.";
        if (_ownership.IsOpenElsewhere(backup.SeedName))
            return $"'{backup.SeedName}' is open in the in-game overlay. Restore it there, or close the game first.";
        try
        {
            Show(_store.Restore(backup.SeedName, backup.Id));
            Problem = null;
            return null;
        }
        catch (TrackerStoreException e)
        {
            return e.Message;
        }
    }

    /// <summary>
    /// Puts the game save kept with a backup back over the game's save. Returns why it could not, or null. Refused while
    /// the game runs, as it would write its own save over it on exit.
    /// </summary>
    public string? RestoreGameSave(RunBackup backup)
    {
        if (System.Diagnostics.Process.GetProcessesByName("eldenring").Length > 0)
            return "Close Elden Ring first: it rewrites its save when it exits, which would undo this.";
        if (_store.GameSaveOf(backup.SeedName, backup.Id) is not { } save)
            return "This backup has no copy of the game save.";
        try
        {
            GameSaves.Restore(save.Copy, save.Info);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return $"Could not put the save back: {e.Message}";
        }
    }

    /// <summary>The game save kept with a backup, for the restore question; null when it has none.</summary>
    public SaveCopyInfo? GameSaveInfo(RunBackup backup) => _store.GameSaveOf(backup.SeedName, backup.Id)?.Info;

    public void DeleteSeed(string name)
    {
        if (_ownership.IsOpenElsewhere(name))
        {
            Problem = $"'{name}' is open in the in-game overlay, so it cannot be deleted while the game is running with it.";
            return;
        }
        try
        {
            _store.Delete(name);
            Problem = null;
        }
        catch (TrackerStoreException e)
        {
            Problem = e.Message;
            return;
        }

        SeedNames.Remove(name);
        if (_state.SeedName == name)
        {
            // Drop the in-memory run too, or the next change would save the deleted seed back.
            _logic = null;
            OpenStartupSeed();
        }
        else
        {
            SeedInput = _state.SeedName;
        }
    }

    private void Show(TrackerState state)
    {
        state.ApplyMissingDefaults(_data!);
        _state = state;
        _ownership.Open(state.SeedName);
        _clock.Restart();
        ReadOnlyNotice = _ownership.Notice;
        _logic = new LogicEvaluator(_data!, state);
        LoadSeedFlags(applyOptions: false);
        // A run whose goal is already met is finished, so its clock does not run on.
        RunGoals.Update(state, RunGoals.Progress(_data!, state, _logic, _bossArenas), DateTime.UtcNow);
        _bestSplits = BestSplits();
        if (RunTimeline.Update(_data!, state, _logic, _bossArenas))
            Save();
        CurrentSeed = state.SeedName;

        SeedNames.Clear();
        foreach (var name in _store.SeedNames().Append(state.SeedName).Distinct())
            SeedNames.Add(name);

        // After the list: clearing it can blank the editable seed box.
        SeedInput = state.SeedName;
        Refresh();
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

    private void SetLocationChecked(Location location, bool isChecked)
    {
        if (isChecked)
            _state.CheckedLocations.Add(location.Id);
        else
            _state.CheckedLocations.Remove(location.Id);
        Changed();
    }

    private void SetLocationTarget(Location location, int? number)
    {
        _state.SetTarget(location.Id, number);
        Changed();
    }

    // A tick in the full list touches nothing else, so only that list is worked out again.
    private void OnAllChecksTicked()
    {
        Save();
        AllChecks.Refresh(_state);
    }

    private void FollowTarget(int number)
    {
        _state.ActiveTarget = number;
        Changed();
    }

    private void SetItemCount(Item item, int count)
    {
        _state.Items[item.Name] = Math.Clamp(count, 0, item.MaxCount);
        Changed();
    }

    private void SetSetting(RandomizerSetting setting, string value)
    {
        if (_state.Settings.GetValueOrDefault(setting.Id) == value)
            return;
        _state.Settings[setting.Id] = value;
        Changed();
    }

    private void Changed()
    {
        _state.ClearCheckedTargets();
        _logic?.Invalidate();
        if (_data is not null && _logic is not null
            && RunGoals.Update(_state, RunGoals.Progress(_data, _state, _logic, _bossArenas), DateTime.UtcNow))
        {
            RunCompleted?.Invoke($"Goal met: {RunGoals.Progress(_data, _state, _logic, _bossArenas)?.Description}. Run time {RunClock.Format(_state.PlayedSeconds)}.");
        }
        if (_data is not null && _logic is not null)
            RunTimeline.Update(_data, _state, _logic, _bossArenas);
        Refresh();
        Save();
    }

    private void Save()
    {
        // Another front end owns this run: saving here would overwrite its progress.
        if (_ownership.IsReadOnly)
            return;
        try
        {
            _store.Save(_state);
            if (!SeedNames.Contains(_state.SeedName))
                SeedNames.Insert(0, _state.SeedName);
        }
        catch (TrackerStoreException e)
        {
            Problem = e.Message;
        }
    }

    private (int Done, int Total, int Available) _summaryCounts;

    /// <summary>The header line: counts, then time played and pace once the run has some.</summary>
    private void UpdateSummary()
    {
        var (done, total, available) = _summaryCounts;
        var text = $"{done} / {total} checked  -  {available} available now";
        if (_state.PlayedSeconds >= 1)
        {
            text += $"  -  {RunClock.Format(_state.PlayedSeconds)}";
            if (RunClock.PerHour(done, _state.PlayedSeconds) is { } pace)
                text += $" ({pace:0.#}/h)";
        }
        Summary = text;
    }

    private void Refresh()
    {
        if (_logic is null)
            return;

        foreach (var region in Regions)
        {
            foreach (var location in region.Locations)
            {
                location.IsSkipped = _state.Skipped.Contains(location.Location.Id);
                location.Update(_logic.GetStatus(location.Location), _filter);
            }
            region.Refresh();
        }

        RefreshTargets();

        foreach (var item in KeyItems.Concat(ShortcutItems).Concat(OtherItems))
            item.Update(_state.Items.GetValueOrDefault(item.Name), _logic.IsItemRelevant(item.Item));

        foreach (var setting in Settings)
            setting.Update(_state.Settings.GetValueOrDefault(setting.Id, ""));

        var statuses = Regions.SelectMany(r => r.Locations).Select(l => l.Status).ToList();
        var done = statuses.Count(s => s == LocationStatus.Checked);
        var total = statuses.Count(s => s != LocationStatus.Excluded);
        var available = statuses.Count(s => s == LocationStatus.Available);
        _summaryCounts = (done, total, available);
        UpdateSummary();
        UpdateGoal();
        RefreshFound();

        Unlocks.Clear();
        var forecast = UnlockForecast.Compute(_data!, _state);
        RefreshProgress(forecast);
        RefreshGraceHint();
        foreach (var unlock in forecast.Take(10))
        {
            var regions = string.Join(", ", unlock.Regions.Take(3)) + (unlock.Regions.Count > 3 ? ", ..." : "");
            Unlocks.Add($"{unlock.Item}: opens {unlock.Opens} check{(unlock.Opens == 1 ? "" : "s")} ({regions})");
        }
        DoneCount = done;
        TotalCount = total;
        Map?.Refresh();
        AllChecks.Refresh(_state);
    }

    private void RefreshTargets()
    {
        var active = _state.ActiveTargetLocation();
        var rows = Regions.SelectMany(r => r.Locations).ToList();
        foreach (var row in rows)
        {
            var number = _state.Targets.TryGetValue(row.Location.Id, out var n) ? n : (int?)null;
            row.UpdateTarget(number, row.Location.Id == active);
        }

        // Rebuilt only on change, so the list does not flicker on every tick.
        var targets = rows.Where(r => r.IsTarget).OrderBy(r => r.TargetNumber).ToList();
        if (targets.SequenceEqual(Targets))
            return;
        Targets.Clear();
        foreach (var target in targets)
            Targets.Add(target);
    }
}

/// <summary>A goal in the Run goal tab's list.</summary>
public sealed record GoalOption(string Kind, string Name);

/// <summary>A finished run in the History tab: its title, its details, and whether it is the best time for its goal.</summary>
public sealed record HistoryRow(string Title, string Details, bool Best, IReadOnlyList<ReportSection> Report);

/// <summary>A titled list of lines in the Progress tab or a finished run's summary.</summary>
public sealed record ReportSection(string Title, IReadOnlyList<string> Lines);

/// <summary>A pickup in the Found tab: what it gave, where, and when.</summary>
public sealed record FoundRow(string Items, string Where, string When);
