using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DropHarvester.Localization;
using DropHarvester.Models;
using DropHarvester.Services;

namespace DropHarvester.ViewModels;

/// <summary>Stats dashboard: lifetime totals, recent history, a 7-day claims chart, and export.</summary>
public partial class StatsViewModel : ObservableViewModel
{
    readonly IStatsService _stats;

    public ObservableCollection<StatEntry> History { get; } = new UiObservableCollection<StatEntry>();

    /// <summary>Raised after the underlying stats change so the page can redraw the chart.</summary>
    public event Action? ChartInvalidated;

    /// <summary>Subscribes to stats changes and loads the initial dashboard values.</summary>
    /// <param name="stats">The stats service providing totals, history, and exports.</param>
    public StatsViewModel(IStatsService stats)
    {
        _stats = stats;
        _stats.Changed += OnStatsChanged;
        Refresh();
    }

    [ObservableProperty] private string _totalWatchTime = "0m";
    [ObservableProperty] private int _totalDropsClaimed;
    [ObservableProperty] private int _totalCampaignsCompleted;
    [ObservableProperty] private string _sinceText = "";
    [ObservableProperty] private string _exportResult = "";

    public IReadOnlyList<(DateOnly day, int count)> ChartData => _stats.ClaimsByDay(7);

    /// <summary>"Game: Drop" names claimed on a day - for the chart's hover tooltip.</summary>
    /// <param name="day">The day to list claimed drops for.</param>
    public IReadOnlyList<string> DropsOn(DateOnly day) => _stats.DropsClaimedOn(day);

    /// <summary>Exports the stats to a JSON file and reports the written path.</summary>
    [RelayCommand]
    async Task ExportJsonAsync()
    {
        var path = await _stats.ExportJsonAsync();
        ExportResult = Loc.T("Stats_ExportedJson", path);
    }

    /// <summary>Exports the stats to a CSV file and reports the written path.</summary>
    [RelayCommand]
    async Task ExportCsvAsync()
    {
        var path = await _stats.ExportCsvAsync();
        ExportResult = Loc.T("Stats_ExportedCsv", path);
    }

    /// <summary>Handles the stats-changed event by refreshing the dashboard on the UI thread.</summary>
    void OnStatsChanged() => MainThread.BeginInvokeOnMainThread(Refresh);

    /// <summary>Reloads the totals, history list, and chart from the current stats data.</summary>
    void Refresh()
    {
        var d = _stats.Data;
        TotalWatchTime = FormatMinutes(d.TotalWatchMinutes);
        TotalDropsClaimed = d.TotalDropsClaimed;
        TotalCampaignsCompleted = d.TotalCampaignsCompleted;
        SinceText = d.FirstSeenUtc is { } f ? Loc.T("Stats_Since", f.ToLocalTime().ToString("MMM d, yyyy")) : "";

        History.Clear();
        foreach (var e in d.History.AsEnumerable().Reverse())
            History.Add(e);

        ChartInvalidated?.Invoke();
    }

    /// <summary>Formats a minute count as "Hh Mm" (or just "Mm" under an hour).</summary>
    /// <param name="minutes">Total minutes to format.</param>
    static string FormatMinutes(int minutes)
    {
        var h = minutes / 60;
        var m = minutes % 60;
        return h > 0 ? $"{h}h {m}m" : $"{m}m";
    }
}
