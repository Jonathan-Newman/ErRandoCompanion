using System.Collections.ObjectModel;
using ErRandoCompanion.Core.AllChecks;
using ErRandoCompanion.Core.Data;
using ErRandoCompanion.Core.Logic;
using ErRandoCompanion.Core.Models;

namespace ErRandoCompanion.App.ViewModels;

/// <summary>One check of the full list. It says where the check is and what the unrandomized game had there, never what it holds now.</summary>
public sealed class AllCheckRow(SeedCheck check, Action<AllCheckRow, bool> setChecked) : ObservableObject
{
    private LocationStatus _status;
    private string _odds = "";
    private bool _overdue;

    public SeedCheck Check => check;

    /// <summary>For a drop of every enemy of a kind: kills, the chance a kill gives it, and how likely it is still missing.</summary>
    public string Odds
    {
        get => _odds;
        private set => Set(ref _odds, value);
    }

    /// <summary>It would usually have dropped by now.</summary>
    public bool IsOverdue
    {
        get => _overdue;
        private set => Set(ref _overdue, value);
    }
    public string Description => check.Slot.Description.Length > 0 ? check.Slot.Description : "Somewhere here";

    public string Detail { get; } = string.Join("  ·  ", new[]
    {
        check.Slot.Kind,
        $"originally {check.Slot.Replaces}",
        check.Slot.Missable ? "missable" : "",
        check.Trackable ? "" : "tick by hand",
    }.Where(s => s.Length > 0));

    public LocationStatus Status
    {
        get => _status;
        private set
        {
            Set(ref _status, value);
            Raise(nameof(IsChecked));
        }
    }

    public bool IsChecked
    {
        get => _status == LocationStatus.Checked;
        set => setChecked(this, value);
    }

    public void Update(LocationStatus status, DropOdds? odds)
    {
        Status = status;
        Odds = odds?.Text() ?? "";
        IsOverdue = odds?.IsOverdue == true;
    }
}

/// <summary>A place in the full list, with its counts.</summary>
public sealed class AllPlaceRow(string name, IReadOnlyList<AllCheckRow> rows) : ObservableObject
{
    private string _counts = "";
    private int _open;

    public string Name => name;
    public IReadOnlyList<AllCheckRow> Rows => rows;

    public string Counts
    {
        get => _counts;
        private set => Set(ref _counts, value);
    }

    public int Open
    {
        get => _open;
        private set => Set(ref _open, value);
    }

    public void Refresh()
    {
        Open = rows.Count(r => r.Status == LocationStatus.Available);
        Counts = $"{rows.Count(r => r.Status == LocationStatus.Checked)} / {rows.Count}" + (Open > 0 ? $"  ·  {Open} open" : "");
    }
}

/// <summary>
/// The All checks tab: every check the randomizer could fill in this seed, by place, with the randomizer's own
/// logic for which are open. Ticks are kept apart from the main checklist.
/// </summary>
public sealed class AllChecksViewModel : ObservableObject
{
    public const string AllPlaces = "Every place";
    private readonly Action _changed;
    private AllChecksSeed? _seed;
    private GameData? _data;
    private TrackerState? _state;
    private List<AllCheckRow> _rows = [];
    private AllPlaceRow? _place;
    private string _filter = "Open";
    private string _kind = "All";
    private string _search = "";
    private string _summary = "";
    private string _message = "Load this run's spoiler log to see every check.";

    public AllChecksViewModel(Action changed) => _changed = changed;

    public ObservableCollection<AllPlaceRow> Places { get; } = [];
    public ObservableCollection<AllCheckRow> Visible { get; } = [];
    public IReadOnlyList<string> Filters { get; } = ["Open", "Locked", "Checked", "Overdue", "All"];
    public IReadOnlyList<string> Kinds { get; } = ["All", .. CheckKinds.All];

    public bool HasList => _seed is not null;

    public string Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public AllPlaceRow? Place
    {
        get => _place;
        set
        {
            Set(ref _place, value);
            ShowVisible();
        }
    }

    public string Filter
    {
        get => _filter;
        set
        {
            Set(ref _filter, value);
            ShowVisible();
        }
    }

    public string Kind
    {
        get => _kind;
        set
        {
            Set(ref _kind, value);
            ShowVisible();
        }
    }

    public string Search
    {
        get => _search;
        set
        {
            Set(ref _search, value);
            ShowVisible();
        }
    }

    /// <summary>Shows a seed's full list, or explains why there is none.</summary>
    public void Load(AllChecksSeed? seed, GameData data, TrackerState state, string? whyNone)
    {
        _seed = seed;
        _data = data;
        _state = state;
        Places.Clear();
        _rows = [];
        if (seed is null)
        {
            Message = whyNone ?? "Load this run's spoiler log to see every check.";
            Raise(nameof(HasList));
            Visible.Clear();
            Summary = "";
            return;
        }

        _rows = seed.Checks.Select(c => new AllCheckRow(c, SetChecked)).ToList();
        var byPlace = _rows.ToLookup(r => r.Check.Place);
        Places.Add(new AllPlaceRow(AllPlaces, _rows));
        foreach (var place in seed.Places)
            Places.Add(new AllPlaceRow(place, byPlace[place].ToList()));
        Message = "";
        Raise(nameof(HasList));
        Refresh(state);
        Place = Places[0];
    }

    /// <summary>Works the statuses out again, after the run changed.</summary>
    public void Refresh(TrackerState state)
    {
        _state = state;
        if (_seed is null || _data is null)
            return;
        var logic = new AllChecksLogic(_seed.Logic, _seed.Options, _data, state, _seed.KnownItems);
        foreach (var row in _rows)
            row.Update(logic.StatusOf(row.Check), _seed.OddsOf(row.Check, state));
        foreach (var place in Places)
            place.Refresh();
        var done = _rows.Count(r => r.Status == LocationStatus.Checked);
        var open = _rows.Count(r => r.Status == LocationStatus.Available);
        Summary = $"{done} / {_rows.Count} checked  -  {open} open now  -  {_rows.Count(r => !r.Check.Trackable)} are ticked by hand";
        ShowVisible();
    }

    private void SetChecked(AllCheckRow row, bool isChecked)
    {
        if (_state is null || row.IsChecked == isChecked)
            return;
        if (isChecked)
            _state.AllChecked.Add(row.Check.Id);
        else
            _state.AllChecked.Remove(row.Check.Id);
        _changed();
    }

    private void ShowVisible()
    {
        var place = _place ?? Places.FirstOrDefault();
        var rows = (place?.Rows ?? []).Where(r => _filter switch
            {
                "Open" => r.Status == LocationStatus.Available,
                "Locked" => r.Status == LocationStatus.Locked,
                "Checked" => r.Status == LocationStatus.Checked,
                "Overdue" => r.IsOverdue,
                _ => true,
            })
            .Where(r => _kind == "All" || r.Check.Slot.Kind == _kind)
            .Where(r => _search.Length == 0
                || r.Description.Contains(_search, StringComparison.OrdinalIgnoreCase)
                || r.Check.Place.Contains(_search, StringComparison.OrdinalIgnoreCase)
                || r.Check.Slot.Replaces.Contains(_search, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (rows.SequenceEqual(Visible))
            return;
        Visible.Clear();
        foreach (var row in rows)
            Visible.Add(row);
    }
}
