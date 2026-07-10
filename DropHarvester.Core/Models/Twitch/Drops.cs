using CommunityToolkit.Mvvm.ComponentModel;
using DropHarvester.Localization;

namespace DropHarvester.Models.Twitch;

/// <summary>
/// A time-based drop: earned by watching an eligible stream for a required number of minutes.
/// Progress and claim state update live, so this is observable for direct UI binding.
/// </summary>
public partial class TimedDrop : ObservableModel
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public int RequiredMinutes { get; init; }
    public DateTimeOffset? StartsAt { get; init; }
    public DateTimeOffset? EndsAt { get; init; }
    public IReadOnlyList<Benefit> Benefits { get; init; } = Array.Empty<Benefit>();

    /// <summary>Ids of drops that must be earned before this one becomes available.</summary>
    public IReadOnlyList<string> PreconditionDropIds { get; init; } = Array.Empty<string>();

    /// <summary>Back-reference to the owning campaign (set when the campaign is built).</summary>
    public DropsCampaign? Campaign { get; set; }

    /// <summary>Minutes reported by Twitch (authoritative when we poll the current session).</summary>
    [ObservableProperty] private int _realCurrentMinutes;

    /// <summary>Locally estimated minutes accumulated since the last authoritative sync.</summary>
    [ObservableProperty] private int _extraCurrentMinutes;

    [ObservableProperty] private string? _claimId;
    [ObservableProperty] private bool _isClaimed;

    /// <summary>True while this drop is one of the actively-harvested campaign's unclaimed drops, so its
    /// "time remaining" ticks down to the second in real time (watching earns ~1 min per real minute).
    /// Set by the orchestrator; cleared when harvesting moves on.</summary>
    [ObservableProperty] private bool _isActivelyWatched;

    // Wall-clock stamp of the last watch-minute update, used to interpolate the sub-minute countdown.
    DateTimeOffset _watchAnchorUtc = DateTimeOffset.UtcNow;

    public int CurrentMinutes => Math.Min(RealCurrentMinutes + ExtraCurrentMinutes, RequiredMinutes);

    public double Progress =>
        RequiredMinutes <= 0 ? (IsClaimed ? 1.0 : 0.0)
        : Math.Clamp((double)CurrentMinutes / RequiredMinutes, 0.0, 1.0);

    public bool IsComplete => CurrentMinutes >= RequiredMinutes;

    /// <summary>Has an earned-but-unclaimed instance still within the 24h post-campaign claim window.</summary>
    public bool CanClaim =>
        !string.IsNullOrEmpty(ClaimId)
        && !IsClaimed
        && (Campaign?.EndsAt is not { } end || DateTimeOffset.UtcNow < end + TimeSpan.FromHours(24));

    public string ProgressText => Loc.T("Model_DropProgress", CurrentMinutes, RequiredMinutes);

    /// <summary>Percent complete for this drop, e.g. "62.2%".</summary>
    public string PercentText => $"{Progress * 100:0.#}%";

    /// <summary>Percent + minutes, e.g. "62.2%  (182/360 min)" - shown on the drop cards.</summary>
    public string ProgressSummary => Loc.T("Model_DropProgressSummary", PercentText, CurrentMinutes, RequiredMinutes);

    /// <summary>Watch-minutes still needed to complete this drop.</summary>
    public int RemainingMinutes => Math.Max(0, RequiredMinutes - CurrentMinutes);

    /// <summary>Watch-time still needed, in seconds. While actively watched it interpolates down from the
    /// last whole-minute update so it ticks every second; the interpolation runs a little past a minute so
    /// a late credit keeps the seconds moving, and it caps so a stall can't run it away.</summary>
    public int RemainingSeconds
    {
        get
        {
            var trueSec = RemainingMinutes * 60;
            if (!IsActivelyWatched || IsClaimed || IsComplete)
                return trueSec;
            var elapsed = (DateTimeOffset.UtcNow - _watchAnchorUtc).TotalSeconds;
            return Math.Max(0, trueSec - (int)Math.Min(elapsed, 75));
        }
    }

    /// <summary>Whether there's watch-time left worth showing a countdown for.</summary>
    public bool HasRemaining => !IsClaimed && !IsComplete && RemainingMinutes > 0;

    /// <summary>"H:MM:SS remaining" (or "M:SS remaining" under an hour); empty once claimed/complete.</summary>
    public string RemainingText => HasRemaining ? Loc.T("Model_Remaining", FormatDuration(RemainingSeconds)) : "";

    /// <summary>Format a whole-second duration as H:MM:SS (or M:SS under an hour).</summary>
    /// <param name="totalSeconds">Total number of seconds to format.</param>
    internal static string FormatDuration(int totalSeconds)
    {
        if (totalSeconds < 0) totalSeconds = 0;
        int h = totalSeconds / 3600, m = totalSeconds % 3600 / 60, s = totalSeconds % 60;
        return h > 0 ? $"{h}:{m:D2}:{s:D2}" : $"{m}:{s:D2}";
    }

    /// <summary>Re-raise the countdown properties - called each second by a UI ticker for active drops.</summary>
    public void Tick()
    {
        OnPropertyChanged(nameof(RemainingSeconds));
        OnPropertyChanged(nameof(RemainingText));
    }

    /// <summary>Convenience inverse of <see cref="IsClaimed"/> for the inventory card (show progress
    /// text while unclaimed, the "Claimed" label once claimed) without needing a value converter.</summary>
    public bool NotClaimed => !IsClaimed;

    /// <summary>Icon of the reward this drop grants (first benefit), for the inventory view.</summary>
    public string? RewardImageUrl => Benefits.FirstOrDefault()?.ImageUrl;

    /// <summary>Reward name (falls back to the drop name).</summary>
    public string RewardName => Benefits.FirstOrDefault()?.Name ?? Name;

    /// <summary>Reset the sub-minute countdown anchor and refresh derived properties when the authoritative watch-minutes change.</summary>
    /// <param name="value">The new authoritative watch-minute count.</param>
    partial void OnRealCurrentMinutesChanged(int value) { _watchAnchorUtc = DateTimeOffset.UtcNow; RaiseDerived(); }
    /// <summary>Reset the sub-minute countdown anchor and refresh derived properties when the local estimated minutes change.</summary>
    /// <param name="value">The new locally estimated extra-minute count.</param>
    partial void OnExtraCurrentMinutesChanged(int value) { _watchAnchorUtc = DateTimeOffset.UtcNow; RaiseDerived(); }
    /// <summary>Refresh derived properties and the NotClaimed flag when the claimed state changes.</summary>
    /// <param name="value">True once the drop has been claimed.</param>
    partial void OnIsClaimedChanged(bool value)
    {
        RaiseDerived();
        OnPropertyChanged(nameof(NotClaimed));
    }
    /// <summary>Re-raise CanClaim when the earned-instance claim id changes.</summary>
    /// <param name="value">The new claim id, or null when none.</param>
    partial void OnClaimIdChanged(string? value) => OnPropertyChanged(nameof(CanClaim));
    /// <summary>Reset the countdown anchor and kick the live countdown when this drop starts being actively watched.</summary>
    /// <param name="value">True while this drop is the actively-harvested target.</param>
    partial void OnIsActivelyWatchedChanged(bool value)
    {
        _watchAnchorUtc = DateTimeOffset.UtcNow;
        Tick();
    }

    /// <summary>Re-raise change notifications for every property derived from the watch-minute fields.</summary>
    void RaiseDerived()
    {
        OnPropertyChanged(nameof(CurrentMinutes));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(IsComplete));
        OnPropertyChanged(nameof(CanClaim));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(PercentText));
        OnPropertyChanged(nameof(ProgressSummary));
        OnPropertyChanged(nameof(RemainingMinutes));
        OnPropertyChanged(nameof(RemainingSeconds));
        OnPropertyChanged(nameof(HasRemaining));
        OnPropertyChanged(nameof(RemainingText));
    }
}

/// <summary>Where a campaign sits relative to now: not yet started, running, or ended.</summary>
public enum CampaignStatus { Upcoming, Active, Expired }

/// <summary>A drops campaign for a game over a time window, containing one or more timed drops.</summary>
public partial class DropsCampaign : ObservableModel
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required Game Game { get; init; }
    public DateTimeOffset StartsAt { get; init; }
    public DateTimeOffset EndsAt { get; init; }
    public string? ImageUrl { get; init; }
    public string? LinkUrl { get; init; }

    /// <summary>Channel logins the campaign is restricted to (empty = any drops-enabled channel).</summary>
    public IReadOnlyList<string> AllowedChannels { get; init; } = Array.Empty<string>();

    public IReadOnlyList<TimedDrop> Drops { get; init; } = Array.Empty<TimedDrop>();

    /// <summary>Whether the user has linked their account to this campaign's program.</summary>
    [ObservableProperty] private bool _linked;

    /// <summary>The campaign whose drop the harvester is watching right now (for the Inventory badge).</summary>
    [ObservableProperty] private bool _isHarvesting;

    public string LinkedText => Linked ? Loc.T("Model_Linked") : Loc.T("Model_LinkNeeded");

    /// <summary>Re-raise LinkedText when the account-linked state changes.</summary>
    /// <param name="value">True once the account is linked to this campaign's program.</param>
    partial void OnLinkedChanged(bool value) => OnPropertyChanged(nameof(LinkedText));

    public bool HasBadgeOrEmote => Drops.Any(d => d.Benefits.Any(b => b.IsBadgeOrEmote));

    public CampaignStatus Status
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            if (now < StartsAt) return CampaignStatus.Upcoming;
            if (now > EndsAt) return CampaignStatus.Expired;
            return CampaignStatus.Active;
        }
    }

    public bool IsActive => Status == CampaignStatus.Active;

    /// <summary>Expiry shown in the user's LOCAL time. EndsAt is stored as UTC (a DateTimeOffset with
    /// a zero offset), and formatting a DateTimeOffset uses its own offset - so binding it directly
    /// shows UTC, not local. ToLocalTime() converts to the machine's zone to match twitch.tv.</summary>
    public string EndsLocalText => Loc.T("Model_Ends", EndsAt.ToLocalTime().ToString("MMM d, yyyy HH:mm"));

    /// <summary>All drops earned/claimed - nothing left to harvest here.</summary>
    public bool IsFinished => Drops.All(d => d.IsClaimed || d.IsComplete);

    /// <summary>The next drop still needing watch-time, in order.</summary>
    public TimedDrop? FirstUnharvestedDrop =>
        Drops.OrderBy(d => d.RequiredMinutes).FirstOrDefault(d => !d.IsClaimed && !d.IsComplete);

    /// <summary>The longest / final drop - completing it means the whole campaign is complete (all
    /// shorter tiers finish before it, as watch-time accrues to every drop together).</summary>
    TimedDrop? LastDrop => Drops.OrderByDescending(d => d.RequiredMinutes).FirstOrDefault();

    /// <summary>Overall progress toward finishing the campaign = progress on the longest (last) drop.</summary>
    public double Progress => LastDrop?.Progress ?? 0;

    /// <summary>Overall percent, e.g. "75.9%".</summary>
    public string PercentText => $"{Progress * 100:0.#}%";

    /// <summary>Done drops out of total, e.g. "(2/4)".</summary>
    public string ClaimedCountText =>
        Drops.Count == 0 ? "" : $"({Drops.Count(d => d.IsClaimed || d.IsComplete)}/{Drops.Count})";

    /// <summary>Overall percent + done count, e.g. "75.9% (2/4)".</summary>
    public string OverallText => Drops.Count == 0 ? "" : $"{PercentText} {ClaimedCountText}";

    IEnumerable<TimedDrop> UnfinishedDrops => Drops.Where(d => !d.IsClaimed && !d.IsComplete);

    /// <summary>Watch-minutes to finish the WHOLE campaign = its longest-remaining unclaimed drop (all
    /// tiers accrue watch-time together, so the highest tier is the last to complete).</summary>
    public int RemainingMinutes => UnfinishedDrops.Select(d => d.RemainingMinutes).DefaultIfEmpty(0).Max();

    public int RemainingSeconds => UnfinishedDrops.Select(d => d.RemainingSeconds).DefaultIfEmpty(0).Max();

    public bool HasRemaining => UnfinishedDrops.Any();

    /// <summary>"H:MM:SS remaining" to finish the whole campaign; empty when nothing is left.</summary>
    public string RemainingText => HasRemaining ? Loc.T("Model_Remaining", TimedDrop.FormatDuration(RemainingSeconds)) : "";

    /// <summary>Re-raise the countdown + overall-progress display - called each second by a UI ticker
    /// while harvesting, so the campaign %, done-count and remaining time all advance live.</summary>
    public void Tick()
    {
        OnPropertyChanged(nameof(RemainingSeconds));
        OnPropertyChanged(nameof(RemainingText));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(PercentText));
        OnPropertyChanged(nameof(ClaimedCountText));
        OnPropertyChanged(nameof(OverallText));
    }

    /// <summary>Reward (benefit) id -> the active window(s) [start, end] of every campaign granting it.
    /// A reward reused across concurrent campaigns (e.g. World of Tanks' generic "Credits"/"Tech") has
    /// several windows, so a claim can only be attributed to a campaign when exactly ONE granting
    /// campaign's window contains the claim time - which is how we tell "claimed THIS campaign" (Diablo
    /// "Final Headache") apart from "owns a shared reward from a concurrent campaign" (WoT).</summary>
    /// <param name="campaigns">The campaigns to index.</param>
    /// <returns>A map from reward match-key to the active [start, end] window of every campaign granting it.</returns>
    public static Dictionary<string, List<(DateTimeOffset start, DateTimeOffset end)>> BenefitWindows(IEnumerable<DropsCampaign> campaigns)
    {
        var map = new Dictionary<string, List<(DateTimeOffset, DateTimeOffset)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in campaigns)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // one window per campaign per reward
            foreach (var d in c.Drops)
                foreach (var b in d.Benefits)
                    if (seen.Add(b.MatchKey))
                    {
                        if (!map.TryGetValue(b.MatchKey, out var list)) { list = new(); map[b.MatchKey] = list; }
                        list.Add((c.StartsAt, c.EndsAt));
                    }
        }
        return map;
    }
}
