using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DropHarvester.Localization;
using DropHarvester.Models;
using DropHarvester.Services;

namespace DropHarvester.ViewModels;

/// <summary>Backs the "What's changed" popup: loads the changelog (bundled + newer-from-GitHub) and exposes
/// it newest-first with the running and latest versions flagged for their badges. When a newer version
/// exists, an Update button pinned at the bottom of the popup downloads and applies it in place.</summary>
public partial class ChangelogViewModel : ObservableViewModel
{
    readonly IChangelogService _changelog;
    readonly IUpdateService _update;
    UpdateInfo? _info; // cached available-update info (asset url) once resolved

    /// <summary>The version entries to list, newest first.</summary>
    public ObservableCollection<ReleaseNote> Releases { get; } = new UiObservableCollection<ReleaseNote>();

    /// <summary>True while the changelog is being loaded (drives a spinner).</summary>
    [ObservableProperty] private bool _isLoading;

    /// <summary>True when there's nothing to show (load failed and no bundled notes).</summary>
    [ObservableProperty] private bool _isEmpty;

    /// <summary>True when a newer version than the running build is available - shows the Update button.</summary>
    [ObservableProperty] private bool _updateAvailable;

    /// <summary>True while the Update button is downloading/applying (disables it).</summary>
    [ObservableProperty] private bool _updating;

    /// <summary>The Update button's label: "Update", then "Updating... N%", then it restarts the app.</summary>
    [ObservableProperty] private string _updateButtonText = "";

    /// <summary>Creates the view model with the changelog source and updater.</summary>
    /// <param name="changelog">The changelog service supplying the release notes.</param>
    /// <param name="update">The update service, used to detect and apply a newer version.</param>
    public ChangelogViewModel(IChangelogService changelog, IUpdateService update)
    {
        _changelog = changelog;
        _update = update;
    }

    /// <summary>Load (or reload) the changelog into <see cref="Releases"/> and decide whether to show Update.</summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        IsEmpty = false;
        UpdateButtonText = Loc.T("Changelog_Update");
        try
        {
            var notes = await _changelog.GetChangelogAsync().ConfigureAwait(true);
            Releases.Clear();
            foreach (var n in notes)
                Releases.Add(n);
            IsEmpty = Releases.Count == 0;

            // An update is available if an installer is already downloaded and waiting, or the newest version
            // we know of isn't the one we're running. Derived from the changelog data we just loaded, so no
            // extra network call - the actual download URL is fetched only if the user clicks Update.
            UpdateAvailable = _update.PendingVersion is not null
                              || Releases.Any(r => r.IsLatest && !r.IsCurrent);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Download (if needed) and apply the newer version, then relaunch - the same installer flow the
    /// Status/Settings update banners use. Reflects progress on the button and re-enables it on failure.</summary>
    [RelayCommand]
    async Task Update()
    {
        if (Updating)
            return;
        Updating = true;
        try
        {
            // Not downloaded yet? fetch the installer for the latest release into the pending cache.
            if (_update.PendingVersion is null)
            {
                var info = _info ?? await _update.CheckAsync();
                if (!info.UpdateAvailable)
                {
                    UpdateAvailable = false; // we're actually current after all
                    return;
                }
                _info = info;
                // same percent + transfer-rate readout the Status/Settings update banners show
                var progress = new Progress<UpdateProgress>(p =>
                {
                    var speed = p.BytesPerSecond > 0 ? $" ({p.BytesPerSecond / (1024.0 * 1024.0):0.0} MB/s)" : "";
                    UpdateButtonText = Loc.T("Changelog_Updating", $"{(int)(p.Fraction * 100)}%{speed}");
                });
                var ok = await _update.DownloadAsync(info, progress);
                if (!ok)
                {
                    UpdateButtonText = Loc.T("Changelog_Update"); // let the user retry
                    return;
                }
            }

            UpdateButtonText = Loc.T("Changelog_Restarting");
            await Task.Delay(300); // let the label paint
            // ApplyPending launches the installer (waits for us to close, installs, relaunches); exit hard so
            // the hide-to-tray handler doesn't keep our files/mutex locked and block the install.
            if (_update.ApplyPending())
                Environment.Exit(0);
            else
                UpdateButtonText = Loc.T("Changelog_Update");
        }
        catch
        {
            UpdateButtonText = Loc.T("Changelog_Update");
        }
        finally
        {
            Updating = false;
        }
    }
}
