using System.Windows;
using System.Windows.Controls;
using ErRandoCompanion.App.ViewModels;
using ErRandoCompanion.Core.Persistence;

namespace ErRandoCompanion.App.Views;

/// <summary>Lists every run's backups (deleted runs included) and puts a chosen one back.</summary>
public sealed class BackupsWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ListBox _list = new() { Margin = new Thickness(0, 8, 0, 8) };

    public BackupsWindow(Window owner, MainViewModel viewModel)
    {
        _viewModel = viewModel;
        Owner = owner;
        Title = "Run backups";
        Width = 520;
        Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("Panel");

        var intro = new TextBlock
        {
            Text = "A copy of each run is kept every 10 minutes of play, and before it is deleted or restored over. "
                + "The last 10 of each run are kept. Restoring backs up what is saved now first, so it can be undone. "
                + "Backups marked \"+ game save\" also hold the game's own save from that moment; put it back with "
                + "\"Restore game save\" while Elden Ring is closed.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextMuted"),
        };
        var restore = new Button { Content = "Restore", Margin = new Thickness(0, 0, 6, 0), MinWidth = 90 };
        var restoreSave = new Button { Content = "Restore game save", Margin = new Thickness(0, 0, 6, 0), MinWidth = 130 };
        restoreSave.Click += (_, _) => RestoreGameSave();
        var close = new Button { Content = "Close", MinWidth = 90, IsCancel = true };
        restore.Click += (_, _) => Restore();
        close.Click += (_, _) => Close();
        _list.MouseDoubleClick += (_, _) => Restore();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(restoreSave);
        buttons.Children.Add(restore);
        buttons.Children.Add(close);

        var layout = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(intro, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(intro);
        layout.Children.Add(buttons);
        layout.Children.Add(_list);
        Content = layout;
        Fill();
    }

    private void Fill()
    {
        _list.Items.Clear();
        var runs = _viewModel.SeedNames.ToHashSet();
        foreach (var backup in _viewModel.Backups())
        {
            var deleted = runs.Contains(backup.SeedName) ? "" : " (deleted)";
            _list.Items.Add(new ListBoxItem
            {
                Content = $"{backup.SeedName}{deleted}   {backup.SavedAt.ToLocalTime():ddd d MMM, HH:mm}   {backup.Checked} checked"
                    + (backup.HasGameSave ? "   + game save" : ""),
                Tag = backup,
            });
        }
        if (_list.Items.Count == 0)
            _list.Items.Add(new ListBoxItem { Content = "No backups yet.", IsEnabled = false });
    }

    private void RestoreGameSave()
    {
        if (_list.SelectedItem is not ListBoxItem { Tag: RunBackup backup })
        {
            MessageBox.Show(this, "Pick a backup in the list first.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_viewModel.GameSaveInfo(backup) is not { } info)
        {
            MessageBox.Show(this, "This backup has no copy of the game save (it was made before saves were copied, or the save could not be read).",
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var answer = MessageBox.Show(this,
            $"Put the game save back as it was on {info.WrittenAt.ToLocalTime():ddd d MMM, HH:mm}?\n\n"
            + $"This replaces {info.Path}, which holds every character on that save, not just this run's.\n\n"
            + $"What is there now is kept as {System.IO.Path.GetFileName(info.Path)}.before-restore in the same folder, so this can be undone by hand. "
            + "Elden Ring must be closed.",
            Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;
        if (_viewModel.RestoreGameSave(backup) is { } problem)
        {
            MessageBox.Show(this, problem, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        MessageBox.Show(this, "The game save is back. Restore the run too if you want its checks to match.", Title,
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Restore()
    {
        if (_list.SelectedItem is not ListBoxItem { Tag: RunBackup backup })
        {
            MessageBox.Show(this, "Pick a backup in the list first.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var when = backup.SavedAt.ToLocalTime().ToString("ddd d MMM, HH:mm");
        var answer = MessageBox.Show(this, $"Put \"{backup.SeedName}\" back as it was on {when} ({backup.Checked} checked)?\n\n"
            + "What is saved now is backed up first, so this can be undone.", Title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            return;
        if (_viewModel.RestoreBackup(backup) is { } problem)
        {
            MessageBox.Show(this, problem, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Close();
    }
}
