using System.Text;
using System.Text.Json;
using DropHarvester.Models;
using DropHarvester.Models.Twitch;

namespace DropHarvester.Services;

/// <summary>
/// Tracks lifetime harvesting stats and a recent-history list, persisted as JSON in the app data
/// folder, and exports them to CSV/JSON. Raises <see cref="Changed"/> after any update.
/// </summary>
public interface IStatsService
{
    StatsData Data { get; }
    event Action? Changed;

    /// <summary>Increment total watch minutes (persisted in batches).</summary>
    void RecordWatchMinute();

    /// <summary>Record a claimed drop in the totals and recent history.</summary>
    /// <param name="drop">The drop that was claimed.</param>
    /// <param name="campaign">Campaign the drop belongs to.</param>
    void RecordDropClaimed(TimedDrop drop, DropsCampaign campaign);

    /// <summary>Record a completed campaign in the totals and recent history.</summary>
    /// <param name="campaign">The campaign that finished.</param>
    void RecordCampaignCompleted(DropsCampaign campaign);

    /// <summary>Persist any batched watch-minutes immediately (called on app close).</summary>
    void Flush();

    /// <summary>Write the stats data to a JSON file in the data folder.</summary>
    /// <param name="ct">Token to cancel the file write.</param>
    /// <returns>Full path of the written JSON file.</returns>
    Task<string> ExportJsonAsync(CancellationToken ct = default);

    /// <summary>Write the recent history to a CSV file in the data folder.</summary>
    /// <param name="ct">Token to cancel the file write.</param>
    /// <returns>Full path of the written CSV file.</returns>
    Task<string> ExportCsvAsync(CancellationToken ct = default);

    /// <summary>Drops claimed per day for the last <paramref name="days"/> days (oldest first).</summary>
    IReadOnlyList<(DateOnly day, int count)> ClaimsByDay(int days);

    /// <summary>The "Game: Drop" names claimed on a given local day (for the chart's hover tooltip).</summary>
    IReadOnlyList<string> DropsClaimedOn(DateOnly day);
}

/// <summary>JSON-file-backed implementation of <see cref="IStatsService"/>.</summary>
public sealed class StatsService : IStatsService
{
    const string FileName = "stats.json";
    const int MaxHistory = 200;

    readonly object _lock = new();
    int _unsavedMinutes;

    public StatsData Data { get; }
    public event Action? Changed;

    /// <summary>Loads persisted stats and stamps the first-seen time on first run.</summary>
    public StatsService()
    {
        Data = JsonStore.Load<StatsData>(FileName);
        Data.FirstSeenUtc ??= DateTimeOffset.UtcNow;
    }

    /// <summary>Increments total watch minutes, saving every fifth minute, and raises Changed.</summary>
    public void RecordWatchMinute()
    {
        lock (_lock)
        {
            Data.TotalWatchMinutes++;
            // Batch watch-minute saves to avoid writing every ~59s tick.
            if (++_unsavedMinutes >= 5)
            {
                _unsavedMinutes = 0;
                Save();
            }
        }
        Changed?.Invoke();
    }

    /// <summary>Increments the claimed-drops total, appends a history entry, saves, and raises Changed.</summary>
    /// <param name="drop">The drop that was claimed.</param>
    /// <param name="campaign">Campaign the drop belongs to.</param>
    public void RecordDropClaimed(TimedDrop drop, DropsCampaign campaign)
    {
        lock (_lock)
        {
            Data.TotalDropsClaimed++;
            AddHistory("DropClaimed", campaign.Game.Name, drop.Name);
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>Increments the completed-campaigns total, appends a history entry, saves, and raises Changed.</summary>
    /// <param name="campaign">The campaign that finished.</param>
    public void RecordCampaignCompleted(DropsCampaign campaign)
    {
        lock (_lock)
        {
            Data.TotalCampaignsCompleted++;
            AddHistory("CampaignComplete", campaign.Game.Name, campaign.Name);
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>Appends a history entry (stamped now) and trims the list to the maximum length.</summary>
    /// <param name="kind">Entry kind, e.g. "DropClaimed" or "CampaignComplete".</param>
    /// <param name="game">Game/category name for the entry.</param>
    /// <param name="name">Drop or campaign name for the entry.</param>
    void AddHistory(string kind, string game, string name)
    {
        Data.History.Add(new StatEntry { TimeUtc = DateTimeOffset.UtcNow, Kind = kind, Game = game, Name = name });
        while (Data.History.Count > MaxHistory)
            Data.History.RemoveAt(0);
    }

    /// <summary>Counts claimed drops per local day for the last given number of days (oldest first).</summary>
    /// <param name="days">Number of days back to include, ending today.</param>
    /// <returns>One (day, count) pair per day in the window, in ascending date order.</returns>
    public IReadOnlyList<(DateOnly day, int count)> ClaimsByDay(int days)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var buckets = new Dictionary<DateOnly, int>();
        for (int i = days - 1; i >= 0; i--)
            buckets[today.AddDays(-i)] = 0;

        lock (_lock)
        {
            foreach (var e in Data.History.Where(h => h.Kind == "DropClaimed"))
            {
                var day = DateOnly.FromDateTime(e.TimeUtc.ToLocalTime().DateTime);
                if (buckets.ContainsKey(day))
                    buckets[day]++;
            }
        }
        return buckets.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>Lists the "Game: Drop" (or just drop) names claimed on a given local day.</summary>
    /// <param name="day">Local day to list claimed drops for.</param>
    public IReadOnlyList<string> DropsClaimedOn(DateOnly day)
    {
        lock (_lock)
            return Data.History
                .Where(h => h.Kind == "DropClaimed"
                            && DateOnly.FromDateTime(h.TimeUtc.ToLocalTime().DateTime) == day)
                .Select(h => string.IsNullOrEmpty(h.Game) ? h.Name : $"{h.Game}: {h.Name}")
                .ToList();
    }

    /// <summary>Serializes the stats data to an indented JSON file in the data folder.</summary>
    /// <param name="ct">Token to cancel the file write.</param>
    /// <returns>Full path of the written JSON file.</returns>
    public async Task<string> ExportJsonAsync(CancellationToken ct = default)
    {
        var path = Path.Combine(AppPaths.DataDir,"dropharvester-stats-export.json");
        string json;
        lock (_lock)
            json = JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
        return path;
    }

    /// <summary>Writes the history entries as CSV rows to a file in the data folder.</summary>
    /// <param name="ct">Token to cancel the file write.</param>
    /// <returns>Full path of the written CSV file.</returns>
    public async Task<string> ExportCsvAsync(CancellationToken ct = default)
    {
        var path = Path.Combine(AppPaths.DataDir,"dropharvester-history.csv");
        var sb = new StringBuilder();
        sb.AppendLine("time_utc,kind,game,name");
        lock (_lock)
        {
            foreach (var e in Data.History)
                sb.AppendLine($"{e.TimeUtc:o},{Csv(e.Kind)},{Csv(e.Game)},{Csv(e.Name)}");
        }
        await File.WriteAllTextAsync(path, sb.ToString(), ct).ConfigureAwait(false);
        return path;
    }

    /// <summary>Escapes a value for CSV, quoting and doubling quotes when it contains a comma or quote.</summary>
    /// <param name="v">Field value to escape.</param>
    static string Csv(string v) => v.Contains(',') || v.Contains('"')
        ? "\"" + v.Replace("\"", "\"\"") + "\""
        : v;

    /// <summary>Resets the unsaved-minute counter and persists the stats immediately.</summary>
    public void Flush()
    {
        lock (_lock)
        {
            _unsavedMinutes = 0;
            Save();
        }
    }

    /// <summary>Persists the current stats data to disk.</summary>
    void Save() => JsonStore.Save(FileName, Data);
}
