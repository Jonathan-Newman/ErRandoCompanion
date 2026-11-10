using System.IO;
using ErRandoCompanion.Core.Models;

namespace ErRandoCompanion.App.ViewModels;

public sealed class MapPinViewModel(MapPin pin, LocationViewModel location, MapInfo map) : ObservableObject
{
    private bool _isShown;
    private bool _isSelected;

    public LocationViewModel Location => location;
    public double X => pin.U * map.Width;
    public double Y => pin.V * map.Height;

    public bool IsShown
    {
        get => _isShown;
        private set => Set(ref _isShown, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public void Refresh() => IsShown = location.IsVisible;
}

public sealed record MapLabelViewModel(string Name, double X, double Y);

public sealed class MapSheetViewModel : ObservableObject
{
    private readonly Action<MapSheetViewModel> _select;
    private bool _isSelected;
    private string _title = "";

    public MapSheetViewModel(MapInfo map, string dataDirectory, IReadOnlyList<MapPinViewModel> pins, Action<MapSheetViewModel> select)
    {
        Map = map;
        Pins = pins;
        _select = select;
        ImagePath = Path.Combine(dataDirectory, map.Image);
        Labels = map.Labels.Select(l => new MapLabelViewModel(l.Name, l.U * map.Width, l.V * map.Height)).ToList();
    }

    public MapInfo Map { get; }
    public string ImagePath { get; }
    public double Width => Map.Width;
    public double Height => Map.Height;
    public IReadOnlyList<MapPinViewModel> Pins { get; }
    public IReadOnlyList<MapLabelViewModel> Labels { get; }

    public string Title
    {
        get => _title;
        private set => Set(ref _title, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (value && !_isSelected)
                _select(this);
        }
    }

    public void SetSelected(bool selected)
    {
        _isSelected = selected;
        Raise(nameof(IsSelected));
    }

    public void Refresh()
    {
        foreach (var pin in Pins)
            pin.Refresh();
        Title = $"{Map.Name} ({Pins.Count(p => p.IsShown)})";
    }
}

/// <summary>The Map tab: which sheet is open, which pin is selected, and requests to look at a pin.</summary>
public sealed class MapViewModel : ObservableObject
{
    private MapSheetViewModel? _sheet;
    private MapPinViewModel? _selectedPin;

    public MapViewModel(MapData data, string dataDirectory, IReadOnlyDictionary<string, LocationViewModel> locations)
    {
        Sheets = data.Maps
            .Select(map => new MapSheetViewModel(
                map,
                dataDirectory,
                data.Pins
                    .Where(p => p.Map == map.Id && locations.ContainsKey(p.Id))
                    .Select(p => new MapPinViewModel(p, locations[p.Id], map))
                    .ToList(),
                SelectSheet))
            .ToList();
        if (Sheets.Count > 0)
            SelectSheet(Sheets[0]);
    }

    /// <summary>Asks the view to bring a pin into view.</summary>
    public event Action<MapPinViewModel>? FocusRequested;

    public IReadOnlyList<MapSheetViewModel> Sheets { get; }

    public MapSheetViewModel? Sheet
    {
        get => _sheet;
        private set => Set(ref _sheet, value);
    }

    public MapPinViewModel? SelectedPin
    {
        get => _selectedPin;
        private set
        {
            if (_selectedPin is not null)
                _selectedPin.IsSelected = false;
            Set(ref _selectedPin, value);
            if (value is not null)
                value.IsSelected = true;
        }
    }

    public void Select(MapPinViewModel? pin) => SelectedPin = pin;

    public void Focus(LocationViewModel location)
    {
        var sheet = Sheets.FirstOrDefault(s => s.Pins.Any(p => p.Location == location));
        if (sheet is null)
            return;

        SelectSheet(sheet);
        var pin = sheet.Pins.First(p => p.Location == location);
        SelectedPin = pin;
        FocusRequested?.Invoke(pin);
    }

    public void Refresh()
    {
        foreach (var sheet in Sheets)
            sheet.Refresh();
    }

    private void SelectSheet(MapSheetViewModel sheet)
    {
        foreach (var other in Sheets)
            other.SetSelected(other == sheet);
        if (Sheet != sheet)
            SelectedPin = null;
        Sheet = sheet;
    }
}
