using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ErRandoCompanion.App.ViewModels;

namespace ErRandoCompanion.App.Views;

/// <summary>Pans and zooms the map. Pins and labels are scaled against the zoom so they stay a constant size.</summary>
public partial class MapView : UserControl
{
    private const double MaxZoom = 4.0;
    private const double ZoomStep = 1.25;
    private const double ButtonZoomStep = 1.6;
    private const double FocusZoom = 1.2;

    // Shared by every pin and label, so changing zoom updates one object instead of hundreds.
    private readonly ScaleTransform _counterScale = new();

    private double _zoom = 1;
    private double _offsetX;
    private double _offsetY;
    private Point? _dragStart;
    private MapViewModel? _map;
    private MapSheetViewModel? _fittedSheet;
    private MapPinViewModel? _pendingFocus;

    public MapView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>Raised when the player asks to see the selected check in the checklist.</summary>
    public event Action<LocationViewModel>? ShowInListRequested;

    private double MinZoom => FitZoom() * 0.8;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_map is not null)
        {
            _map.FocusRequested -= OnFocusRequested;
            _map.PropertyChanged -= OnMapPropertyChanged;
        }

        _map = e.NewValue as MapViewModel;
        if (_map is not null)
        {
            _map.FocusRequested += OnFocusRequested;
            _map.PropertyChanged += OnMapPropertyChanged;
        }
    }

    private void OnMapPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MapViewModel.Sheet))
            FitIfSheetChanged();
    }

    private void OnFocusRequested(MapPinViewModel pin)
    {
        _pendingFocus = pin;

        // The tab may only just have been opened, so wait until the view has a size.
        Dispatcher.BeginInvoke(ApplyPendingFocus, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void ApplyPendingFocus()
    {
        if (_pendingFocus is not { } pin || Viewport.ActualWidth <= 0)
            return;

        _pendingFocus = null;
        _fittedSheet = _map?.Sheet;
        _zoom = Math.Max(_zoom, FocusZoom);
        _offsetX = Viewport.ActualWidth / 2 - pin.X * _zoom;
        _offsetY = Viewport.ActualHeight / 2 - pin.Y * _zoom;
        ApplyTransform();
    }

    private void OnViewportResized(object sender, SizeChangedEventArgs e)
    {
        if (_pendingFocus is not null)
            ApplyPendingFocus();
        else if (_fittedSheet is null || e.PreviousSize.Width <= 0)
            Fit();
        else
            ApplyTransform();
    }

    private void FitIfSheetChanged()
    {
        if (_map?.Sheet != _fittedSheet)
            Fit();
    }

    private double FitZoom()
    {
        var sheet = _map?.Sheet;
        if (sheet is null || Viewport.ActualWidth <= 0)
            return 1;
        return Math.Min(Viewport.ActualWidth / sheet.Width, Viewport.ActualHeight / sheet.Height);
    }

    private void Fit()
    {
        var sheet = _map?.Sheet;
        if (sheet is null || Viewport.ActualWidth <= 0)
            return;

        _fittedSheet = sheet;
        _zoom = FitZoom();
        _offsetX = (Viewport.ActualWidth - sheet.Width * _zoom) / 2;
        _offsetY = (Viewport.ActualHeight - sheet.Height * _zoom) / 2;
        ApplyTransform();
    }

    private void ZoomAround(Point center, double factor)
    {
        var zoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        var applied = zoom / _zoom;
        _offsetX = center.X - (center.X - _offsetX) * applied;
        _offsetY = center.Y - (center.Y - _offsetY) * applied;
        _zoom = zoom;
        ApplyTransform();
    }

    private void ApplyTransform()
    {
        WorldTransform.Matrix = new Matrix(_zoom, 0, 0, _zoom, _offsetX, _offsetY);
        _counterScale.ScaleX = 1 / _zoom;
        _counterScale.ScaleY = 1 / _zoom;
    }

    private Point ViewportCenter => new(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2);

    private void OnWheel(object sender, MouseWheelEventArgs e) =>
        ZoomAround(e.GetPosition(Viewport), e.Delta > 0 ? ZoomStep : 1 / ZoomStep);

    private void OnZoomIn(object sender, RoutedEventArgs e) => ZoomAround(ViewportCenter, ButtonZoomStep);

    private void OnZoomOut(object sender, RoutedEventArgs e) => ZoomAround(ViewportCenter, 1 / ButtonZoomStep);

    private void OnFit(object sender, RoutedEventArgs e) => Fit();

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(Viewport);
        Viewport.CaptureMouse();
        _map?.Select(null);
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start)
            return;

        var now = e.GetPosition(Viewport);
        _offsetX += now.X - start.X;
        _offsetY += now.Y - start.Y;
        _dragStart = now;
        ApplyTransform();
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        Viewport.ReleaseMouseCapture();
    }

    private void OnCounterScaled(object sender, RoutedEventArgs e) =>
        ((FrameworkElement)sender).RenderTransform = _counterScale;

    private void OnPinClicked(object sender, RoutedEventArgs e) =>
        _map?.Select((MapPinViewModel)((FrameworkElement)sender).DataContext);

    private void OnShowInList(object sender, RoutedEventArgs e)
    {
        if (_map?.SelectedPin is { } pin)
            ShowInListRequested?.Invoke(pin.Location);
    }
}
