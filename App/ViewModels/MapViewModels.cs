using System.IO;
using System.Windows.Media;
using ErRandoCompanion.Core.MapArt;
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
    private ImageSource? _image;

    public MapSheetViewModel(MapInfo map, IReadOnlyList<MapPinViewModel> pins, Action<MapSheetViewModel> select)
    {
        Map = map;
        Pins = pins;
        _select = select;
        Labels = map.Labels.Select(l => new MapLabelViewModel(l.Name, l.U * map.Width, l.V * map.Height)).ToList();
    }

    public MapInfo Map { get; }
    /// <summary>The map picture, or null until the player has made it from their game.</summary>
    public ImageSource? Image
    {
        get => _image;
        private set => Set(ref _image, value);
    }

    public void LoadImage(MapArtFiles art) => Image = MapArtFiles.Load(art.Find(Map.Image));
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
    private readonly MapArtFiles _art;
    private readonly Func<string?> _randomizerDirectory;
    private bool _hasArt;
    private bool _isExtracting;
    private string _artStatus = "";
    private double _artProgress;

    public MapViewModel(MapData data, MapArtFiles art, Func<string?> randomizerDirectory,
        IReadOnlyDictionary<string, LocationViewModel> locations)
    {
        _art = art;
        _randomizerDirectory = randomizerDirectory;
        Sheets = data.Maps
            .Select(map => new MapSheetViewModel(
                map,
                data.Pins
                    .Where(p => p.Map == map.Id && locations.ContainsKey(p.Id))
                    .Select(p => new MapPinViewModel(p, locations[p.Id], map))
                    .ToList(),
                SelectSheet))
            .ToList();
        if (Sheets.Count > 0)
            SelectSheet(Sheets[0]);
        LoadArt();
    }

    /// <summary>False until the map pictures have been made from the player's game.</summary>
    public bool HasArt
    {
        get => _hasArt;
        private set => Set(ref _hasArt, value);
    }

    public bool IsExtracting
    {
        get => _isExtracting;
        private set => Set(ref _isExtracting, value);
    }

    public string ArtStatus
    {
        get => _artStatus;
        private set => Set(ref _artStatus, value);
    }

    public double ArtProgress
    {
        get => _artProgress;
        private set => Set(ref _artProgress, value);
    }

    public string ArtDirectory => _art.ArtDirectory;

    private void LoadArt()
    {
        foreach (var sheet in Sheets)
            sheet.LoadImage(_art);
        HasArt = Sheets.All(s => s.Image is not null);
    }

    /// <summary>The game folder if it can be found without asking.</summary>
    public string? FindGameFolder() => MapArtFiles.FindGameFolder(_randomizerDirectory());

    /// <summary>Makes the map pictures from the game in <paramref name="gameFolder"/>. Never writes to that folder.</summary>
    public async Task ExtractArtAsync(string gameFolder)
    {
        if (IsExtracting)
            return;
        IsExtracting = true;
        ArtStatus = "Reading the game's map tiles...";
        ArtProgress = 0;
        var progress = new Progress<(string Status, double Done)>(p =>
        {
            ArtStatus = p.Status;
            ArtProgress = p.Done;
        });
        IProgress<(string, double)> report = progress;
        try
        {
            await Task.Run(() => _art.Extract(gameFolder, (status, done) => report.Report((status, done))));
            LoadArt();
            ArtStatus = HasArt ? "" : "The map pictures were made but could not be opened.";
        }
        catch (Exception e) when (e is MapArtException or IOException or UnauthorizedAccessException or InvalidDataException
                                       or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            ArtStatus = $"Could not make the map: {e.Message}";
        }
        finally
        {
            IsExtracting = false;
        }
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
