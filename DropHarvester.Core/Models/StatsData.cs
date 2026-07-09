namespace DropHarvester.Models;

/// <summary>One recorded harvesting event, kept for the history list and charts.</summary>
public sealed class StatEntry
{
    public DateTimeOffset TimeUtc { get; set; }
    public string Kind { get; set; } = "";   // "DropClaimed" | "CampaignComplete"
    public string Game { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Event time in the user's local zone (TimeUtc is UTC; formatting a DateTimeOffset uses
    /// its own offset, so bind this instead of TimeUtc to avoid showing UTC).</summary>
    public string LocalTimeText => TimeUtc.ToLocalTime().ToString("MM/dd HH:mm");

    /// <summary>Short tag distinguishing a campaign-complete entry from a drop-claimed one in the list,
    /// since both can share the same name.</summary>
    public string KindLabel => Kind == "CampaignComplete" ? "[Campaign]" : "[Drop]";
}

/// <summary>Persisted lifetime harvesting statistics plus a bounded recent-history list.</summary>
public sealed class StatsData
{
    public int TotalWatchMinutes { get; set; }
    public int TotalDropsClaimed { get; set; }
    public int TotalCampaignsCompleted { get; set; }
    public DateTimeOffset? FirstSeenUtc { get; set; }

    /// <summary>Most recent events (newest last), capped by the service.</summary>
    public List<StatEntry> History { get; set; } = new();
}
