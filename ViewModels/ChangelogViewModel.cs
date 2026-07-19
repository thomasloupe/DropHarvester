using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DropHarvester.Models;
using DropHarvester.Services;

namespace DropHarvester.ViewModels;

/// <summary>Backs the "What's changed" popup: loads the changelog (bundled + newer-from-GitHub) and exposes
/// it newest-first with the running and latest versions flagged for their badges.</summary>
public partial class ChangelogViewModel : ObservableViewModel
{
    readonly IChangelogService _changelog;

    /// <summary>The version entries to list, newest first.</summary>
    public ObservableCollection<ReleaseNote> Releases { get; } = new UiObservableCollection<ReleaseNote>();

    /// <summary>True while the changelog is being loaded (drives a spinner).</summary>
    [ObservableProperty] private bool _isLoading;

    /// <summary>True when there's nothing to show (load failed and no bundled notes).</summary>
    [ObservableProperty] private bool _isEmpty;

    /// <summary>Creates the view model with the changelog source.</summary>
    /// <param name="changelog">The changelog service supplying the release notes.</param>
    public ChangelogViewModel(IChangelogService changelog) => _changelog = changelog;

    /// <summary>Load (or reload) the changelog into <see cref="Releases"/>.</summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        IsEmpty = false;
        try
        {
            var notes = await _changelog.GetChangelogAsync().ConfigureAwait(true);
            Releases.Clear();
            foreach (var n in notes)
                Releases.Add(n);
            IsEmpty = Releases.Count == 0;
        }
        finally
        {
            IsLoading = false;
        }
    }
}
