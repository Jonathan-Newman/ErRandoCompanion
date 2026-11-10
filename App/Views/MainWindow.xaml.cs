using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ErRandoCompanion.App.ViewModels;
using Microsoft.Win32;

namespace ErRandoCompanion.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();

        var viewModel = (MainViewModel)DataContext;
        viewModel.ListFocusRequested += OnListFocusRequested;
        viewModel.NewSeedDetected += OnNewSeedDetected;
        viewModel.RunCompleted += message =>
            MessageBox.Show(this, message, "Run complete", MessageBoxButton.OK, MessageBoxImage.Information);
        Loaded += (_, _) => viewModel.OfferNewestSeed();
        MapPane.ShowInListRequested += viewModel.ShowInList;
        SourceInitialized += (_, _) => UseDarkTitleBar();
    }

    private const int DwmUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    // Purely cosmetic: on Windows versions without this attribute the call fails and the title bar stays light.
    private void UseDarkTitleBar()
    {
        var enabled = 1;
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DwmUseImmersiveDarkMode, ref enabled, sizeof(int));
    }

    private void OnLoadSpoilerLog(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose your randomizer spoiler log",
            Filter = "Spoiler logs (*.txt)|*.txt|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == true)
            ((MainViewModel)DataContext).LoadSpoilerLog(dialog.FileName);
    }

    private void OnChooseRandomizerFolder(object sender, RoutedEventArgs e)
    {
        var viewModel = (MainViewModel)DataContext;
        var dialog = new OpenFolderDialog
        {
            Title = "Choose your randomizer folder (the one with spoiler_logs in it)",
            InitialDirectory = viewModel.RandomizerDirectory ?? "",
        };
        if (dialog.ShowDialog(this) == true)
            viewModel.SetRandomizerDirectory(dialog.FolderName);
    }

    private bool OnNewSeedDetected(string logPath, string seed)
    {
        var label = string.IsNullOrEmpty(seed) ? System.IO.Path.GetFileName(logPath) : $"seed {seed}";
        var message = $"A new randomizer run was found ({label}).\n\nStart a new run for it? Its settings will be filled in from the spoiler log.";
        return MessageBox.Show(this, message, "New seed detected",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes;
    }

    private void OnDeleteSeed(object sender, RoutedEventArgs e)
    {
        var viewModel = (MainViewModel)DataContext;
        if (viewModel.DeletableSeed is not { } name)
        {
            MessageBox.Show(this, "Pick a saved run in the seed box first.", "Delete run", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var answer = MessageBox.Show(this, $"Delete the saved run \"{name}\"?\n\nA backup is kept, so it can be brought back with Backups.", "Delete run",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes)
            viewModel.DeleteSeed(name);
    }

    private void OnBackups(object sender, RoutedEventArgs e) =>
        new BackupsWindow(this, (MainViewModel)DataContext).ShowDialog();

    // The row's container only exists once the checklist tab has laid out, so wait for that.
    private void OnListFocusRequested(LocationViewModel location) =>
        Dispatcher.BeginInvoke(() => FindRow(RegionList, location)?.BringIntoView(), DispatcherPriority.Background);

    private static FrameworkElement? FindRow(DependencyObject parent, LocationViewModel location)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ContentPresenter { DataContext: var context } presenter && ReferenceEquals(context, location))
                return presenter;
            if (FindRow(child, location) is { } found)
                return found;
        }
        return null;
    }
}
