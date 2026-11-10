using System.Windows.Input;
using ErRandoCompanion.Core.Logic;
using ErRandoCompanion.Core.Models;

namespace ErRandoCompanion.App.ViewModels;

public enum LocationFilter
{
    Available,
    Checked,
    Locked,
    All,
}

public sealed class LocationViewModel : ObservableObject
{
    private readonly Location _location;
    private readonly Action<Location, bool> _setChecked;
    private LocationStatus _status;
    private bool _isVisible;
    private bool _isHighlighted;
    private int? _targetNumber;
    private bool _isActiveTarget;

    private bool _isSkipped;

    public LocationViewModel(
        Location location, string regionName, bool hasPin, Action<Location, bool> setChecked, Action<LocationViewModel> showOnMap,
        Action<Location, int?> setTarget, Action<int> followTarget, Action<Location> toggleSkip)
    {
        ToggleSkip = new RelayCommand(() => toggleSkip(location));
        _location = location;
        _setChecked = setChecked;
        RegionName = regionName;
        HasPin = hasPin;
        ShowOnMap = new RelayCommand(() => showOnMap(this));
        SetTarget = new ParameterCommand(n => setTarget(location, int.TryParse(n, out var number) ? number : null));
        ClearTarget = new RelayCommand(() => setTarget(location, null));
        Follow = new RelayCommand(() =>
        {
            if (_targetNumber is { } number)
                followTarget(number);
        });
    }

    public Location Location => _location;
    public string Name => _location.Name;
    public string Details => _location.Details;
    public string RegionName { get; }
    public LocationCategory Category => _location.Category;
    public bool HasPin { get; }
    public ICommand ShowOnMap { get; }

    /// <summary>Takes the target number as its parameter.</summary>
    public ICommand SetTarget { get; }
    public ICommand ClearTarget { get; }

    /// <summary>Makes this target the one the guide arrow follows.</summary>
    public ICommand Follow { get; }

    /// <summary>Puts the check aside for now, or back.</summary>
    public ICommand ToggleSkip { get; }

    /// <summary>Put aside for now: left out of what counts as open.</summary>
    public bool IsSkipped
    {
        get => _isSkipped;
        set
        {
            if (_isSkipped == value)
                return;
            _isSkipped = value;
            Raise();
            Raise(nameof(SkipLabel));
            Raise(nameof(ShowsSkipped));
        }
    }

    public string SkipLabel => _isSkipped ? "Unskip" : "Skip for now";

    /// <summary>Skipped and still to do, so marked as skipped.</summary>
    public bool ShowsSkipped => _isSkipped && _status == LocationStatus.Available;

    /// <summary>This check's target number, or null when it is not a target.</summary>
    public int? TargetNumber
    {
        get => _targetNumber;
        private set
        {
            if (_targetNumber == value)
                return;
            _targetNumber = value;
            Raise();
            Raise(nameof(IsTarget));
        }
    }

    public bool IsTarget => _targetNumber is not null;

    public bool IsActiveTarget
    {
        get => _isActiveTarget;
        private set => Set(ref _isActiveTarget, value);
    }

    public void UpdateTarget(int? number, bool isActive)
    {
        TargetNumber = number;
        IsActiveTarget = isActive;
    }

    public LocationStatus Status
    {
        get => _status;
        private set
        {
            if (_status == value)
                return;
            _status = value;
            Raise();
            Raise(nameof(IsChecked));
            Raise(nameof(IsEnabled));
            Raise(nameof(ShowsSkipped));
        }
    }

    public bool IsChecked
    {
        get => _status == LocationStatus.Checked;
        set => _setChecked(_location, value);
    }

    public bool IsEnabled => _status != LocationStatus.Excluded;

    public bool IsVisible
    {
        get => _isVisible;
        private set => Set(ref _isVisible, value);
    }

    /// <summary>Marks the row the map just sent the player to.</summary>
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set => Set(ref _isHighlighted, value);
    }

    public void Update(LocationStatus status, LocationFilter filter)
    {
        Status = status;
        IsVisible = filter switch
        {
            LocationFilter.Available => status == LocationStatus.Available,
            LocationFilter.Checked => status == LocationStatus.Checked,
            LocationFilter.Locked => status == LocationStatus.Locked,
            _ => true,
        };
    }
}

public sealed class RegionGroupViewModel(string name, IReadOnlyList<LocationViewModel> locations) : ObservableObject
{
    private string _progress = "";
    private int _done;
    private int _total;
    private bool _isVisible;
    private bool _isExpanded = true;

    public string Name => name;
    public IReadOnlyList<LocationViewModel> Locations => locations;

    public string Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    public int Done
    {
        get => _done;
        private set => Set(ref _done, value);
    }

    public int Total
    {
        get => _total;
        private set => Set(ref _total, value);
    }

    public bool IsVisible
    {
        get => _isVisible;
        private set => Set(ref _isVisible, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    /// <summary>Call after the locations have been updated.</summary>
    public void Refresh()
    {
        Total = locations.Count(l => l.Status != LocationStatus.Excluded);
        Done = locations.Count(l => l.Status == LocationStatus.Checked);
        var open = locations.Count(l => l.Status == LocationStatus.Available && !l.IsSkipped);
        Progress = Done == Total ? $"{Done} / {Total}" : $"{open} open   {Done} / {Total}";
        IsVisible = locations.Any(l => l.IsVisible);
    }
}
