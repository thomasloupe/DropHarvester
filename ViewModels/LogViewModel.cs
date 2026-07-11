using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DropHarvester.Localization;
using DropHarvester.Models;
using DropHarvester.Models.Events;
using DropHarvester.Services;

namespace DropHarvester.ViewModels;

/// <summary>One rendered log entry: its display text, text color, and zebra-stripe row background.</summary>
public sealed class LogLine
{
    public required string Text { get; init; }
    public required Color Color { get; init; }

    /// <summary>Alternating row background for readability. Fixed per line (from a monotonic counter) so
    /// trimming the oldest line never re-stripes the ones still on screen.</summary>
    public required Color RowBackground { get; init; }
}

/// <summary>Accumulates human-readable log/error events for the Log tab (capped to avoid growth).</summary>
public partial class LogViewModel : ObservableViewModel
{
    const int MaxLines = 500;
    // Subtle zebra stripe for odd rows; even rows stay on the card background (transparent).
    static readonly Color AltRowBackground = Color.FromArgb("#12FFFFFF");
    readonly IHarvesterEventBus _bus;
    readonly ISettingsStore _settings;
    int _lineSeq; // monotonic counter driving the stripe parity (survives trimming)

    public ObservableCollection<LogLine> Lines { get; } = new UiObservableCollection<LogLine>();

    /// <summary>Subscribes to the harvester event bus so incoming events are appended as log lines.</summary>
    /// <param name="bus">Event bus whose events become log lines.</param>
    /// <param name="settings">Settings store supplying the timestamp formatting options.</param>
    public LogViewModel(IHarvesterEventBus bus, ISettingsStore settings)
    {
        _bus = bus;
        _settings = settings;
        _bus.Event += OnEvent;
    }

    /// <summary>Format a timestamp per the user's Log settings (date/time order + 12/24h clock).</summary>
    /// <param name="t">The event timestamp to format.</param>
    /// <returns>The formatted local-time stamp.</returns>
    string FormatStamp(DateTimeOffset t)
    {
        var s = _settings.Settings;
        var local = t.ToLocalTime();
        var time = s.LogUse24Hour ? "HH:mm:ss" : "h:mm:ss tt";
        const string date = "yyyy-MM-dd";
        return s.LogTimestampMode switch
        {
            LogTimestampMode.Date => local.ToString(date),
            LogTimestampMode.Time => local.ToString(time),
            LogTimestampMode.TimeAndDate => local.ToString($"{time} {date}"),
            _ => local.ToString($"{date} {time}"), // DateAndTime
        };
    }

    /// <summary>Clears all accumulated log lines.</summary>
    [RelayCommand]
    void Clear()
    {
        Lines.Clear();
        _lineSeq = 0;
    }

    // Label flips to "Copied!" briefly for feedback.
    [ObservableProperty] private string _copyLabel = Loc.T("Log_Copy");

    /// <summary>Copies all log lines to the clipboard and briefly flips the copy label to show the result.</summary>
    [RelayCommand]
    async Task CopyAsync()
    {
        try
        {
            var text = string.Join(Environment.NewLine, Lines.Select(l => l.Text));
            await Clipboard.Default.SetTextAsync(text);
            CopyLabel = Loc.T("Log_Copied");
        }
        catch
        {
            CopyLabel = Loc.T("Log_CopyFailed");
        }
        await Task.Delay(1500);
        CopyLabel = Loc.T("Log_Copy");
    }

    /// <summary>Maps a harvester event to a colored, timestamped log line and appends it (trimming to the cap).</summary>
    /// <param name="e">The harvester event to log.</param>
    void OnEvent(HarvesterEvent e)
    {
        (string text, Color color)? line = e switch
        {
            LogEvent { Level: HarvesterLogLevel.Debug } => null, // internal-only chatter: debug server keeps it, users don't see it
            LogEvent l => (l.Message, ColorForLevel(l.Level)),
            HarvesterErrorEvent err => (err.Message, ColorFor("DhRed")),
            LoginExpiredEvent => ("Login expired - please log in again.", ColorFor("DhGold")),
            DropClaimedEvent d => (
                $"Claimed drop: {d.Campaign.Game.Name} {d.Drop.Name} "
                    + $"({d.Campaign.Drops.Count(x => x.IsClaimed)}/{d.Campaign.Drops.Count})",
                ColorFor("DhGreen")),
            CampaignCompletedEvent c => ($"Campaign complete: {c.Campaign.Name} [{c.Campaign.Game.Name}]", ColorFor("DhGreen")),
            _ => null,
        };
        if (line is not { } l2)
            return;

        var stamp = FormatStamp(e.TimestampUtc);
        var rowBg = (_lineSeq++ & 1) == 1 ? AltRowBackground : Colors.Transparent;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Lines.Add(new LogLine { Text = $"[{stamp}] {l2.text}", Color = l2.color, RowBackground = rowBg });
            while (Lines.Count > MaxLines)
                Lines.RemoveAt(0);
        });
    }

    /// <summary>Picks the log-line color for a log level (red for error, gold for warn, default text otherwise).</summary>
    /// <param name="level">The log level to map.</param>
    static Color ColorForLevel(HarvesterLogLevel level) => level switch
    {
        HarvesterLogLevel.Error => ColorFor("DhRed"),
        HarvesterLogLevel.Warn => ColorFor("DhGold"),
        _ => ColorFor("DhText"),
    };

    /// <summary>Resolves a Color from the app resource dictionary by key, falling back to white.</summary>
    /// <param name="key">The resource key to look up.</param>
    static Color ColorFor(string key)
        => Application.Current?.Resources.TryGetValue(key, out var c) == true && c is Color col ? col : Colors.White;
}
