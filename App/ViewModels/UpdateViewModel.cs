using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using ErRandoCompanion.Core.Persistence;
using ErRandoCompanion.Core.Updates;

namespace ErRandoCompanion.App.ViewModels;

/// <summary>
/// The update banner: looks on GitHub for a newer release (at most about once a day, if allowed), and on request
/// installs it over this app and over the overlay in the randomizer folder. Nothing is installed without a click.
/// </summary>
public sealed class UpdateViewModel : ObservableObject
{
    private const string TestVersion = Updates.TestVersionVariable;

    private readonly Preferences _preferences;
    private readonly Action _savePreferences;
    private readonly Func<string?> _randomizerDirectory;
    private ReleaseInfo? _release;
    private string _message = "";
    private bool _busy;
    private bool _canUpdate;
    private bool _canRestart;

    public UpdateViewModel(Preferences preferences, Action savePreferences, Func<string?> randomizerDirectory)
    {
        _preferences = preferences;
        _savePreferences = savePreferences;
        _randomizerDirectory = randomizerDirectory;
        Current = Updates.CurrentOf(typeof(UpdateViewModel).Assembly);
        Update = new RelayCommand(() => _ = InstallAsync(), () => _canUpdate && !_busy);
        Later = new RelayCommand(Skip);
        Notes = new RelayCommand(() => Open(_release?.Page ?? Updates.ReleasesPage));
        Restart = new RelayCommand(RestartApp, () => _canRestart);
        Updates.CleanUp(AppFolder);
    }

    public Version Current { get; }
    public RelayCommand Update { get; }
    public RelayCommand Later { get; }
    public RelayCommand Notes { get; }
    public RelayCommand Restart { get; }

    public bool HasMessage => _message.Length > 0;

    public string Message
    {
        get => _message;
        private set
        {
            Set(ref _message, value);
            Raise(nameof(HasMessage));
        }
    }

    public bool CanUpdate
    {
        get => _canUpdate;
        private set => Set(ref _canUpdate, value);
    }

    public bool CanRestart
    {
        get => _canRestart;
        private set => Set(ref _canRestart, value);
    }

    public bool CheckForUpdates
    {
        get => _preferences.CheckForUpdates;
        set
        {
            _preferences.CheckForUpdates = value;
            _savePreferences();
            Raise();
            if (value)
                _ = CheckAsync(force: true);
        }
    }

    private static string AppFolder => Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory) ?? AppContext.BaseDirectory;

    // A published app is one exe; a development build has its DLLs beside it and is not updated in place.
    private static bool IsPublished => !File.Exists(Path.Combine(AppFolder, "ErRandoCompanion.dll"));

    /// <summary>Looks for a newer release, unless that is turned off or was done recently.</summary>
    public async Task CheckAsync(bool force = false)
    {
        if (!_preferences.CheckForUpdates)
            return;
        if (!force && _preferences.LastUpdateCheck is { } last && DateTime.UtcNow - last < Updates.CheckEvery
            && Environment.GetEnvironmentVariable(TestVersion) is null)
            return;
        try
        {
            using var client = Updates.Client();
            var release = await Updates.LatestAsync(client);
            _preferences.LastUpdateCheck = DateTime.UtcNow;
            _preferences.LatestVersion = release.Tag;
            _preferences.LatestPage = release.Page;
            _savePreferences();
            if (release.Version <= Current || (!force && _preferences.SkippedVersion == release.Tag))
                return;
            _release = release;
            CanUpdate = IsPublished && release.App is not null;
            Message = CanUpdate
                ? $"Version {release.Version} of the companion is available (you have {Current}). Update installs it here and in your randomizer folder's overlay."
                : $"Version {release.Version} of the companion is available (you have {Current}). Download it from GitHub.";
        }
        catch (UpdateException)
        {
            // No connection, or GitHub is down: not worth interrupting for. The next start tries again.
        }
    }

    private async Task InstallAsync()
    {
        if (_release?.App is not { } appAsset)
            return;
        _busy = true;
        CanUpdate = false;
        try
        {
            using var client = Updates.Client();
            Message = $"Downloading version {_release.Version}...";
            var app = await Updates.DownloadAsync(client, appAsset);
            await Task.Run(() => Updates.Install(app, AppFolder));

            // The overlay too, where it is installed: in its own folder in the randomizer folder, or straight in it.
            var overlay = OverlayFolder();
            var overlayNote = "";
            if (overlay is not null && _release.Mod is { } modAsset)
            {
                Message = $"Updating the overlay in {overlay}...";
                var mod = await Updates.DownloadAsync(client, modAsset);
                await Task.Run(() => Updates.Install(mod, overlay));
                overlayNote = " The overlay is updated too; if the game is running, it uses the new version from its next start.";
            }
            Message = $"Updated to version {_release.Version}. Restart the app to use it.{overlayNote}";
            CanRestart = true;
        }
        catch (UpdateException e)
        {
            Message = e.Message;
            CanUpdate = true;
        }
        finally
        {
            _busy = false;
        }
    }

    private string? OverlayFolder()
    {
        if (_randomizerDirectory() is not { } randomizer)
            return null;
        foreach (var folder in new[] { Path.Combine(randomizer, "ErRandoCompanion"), randomizer })
        {
            if (File.Exists(Path.Combine(folder, "ErRandoCompanionMod.dll")))
                return folder;
        }
        return null;
    }

    private void Skip()
    {
        if (_release is not null && !CanRestart)
        {
            _preferences.SkippedVersion = _release.Tag;
            _savePreferences();
        }
        Message = "";
    }

    private static void RestartApp()
    {
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true });
        Application.Current.Shutdown();
    }

    private static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }
}
