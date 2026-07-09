using DropHarvester.Models.Events;
using DropHarvester.Models.Twitch;

namespace DropHarvester.Daemon;

/// <summary>
/// Live, thread-safe snapshot of what the daemon is doing, updated from harvester events and served by
/// the /status endpoint. Holds references to the active drop/campaign models so /status can report the
/// live "time remaining" (they self-interpolate to the current second). Eventual consistency is fine.
/// </summary>
public sealed class DaemonStatus
{
    readonly object _gate = new();

    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;

    public bool LoggedIn { get; private set; }
    public string? Account { get; private set; }
    public bool Harvesting { get; private set; }
    public string Summary { get; private set; } = "starting";
    public string Websocket { get; private set; } = "disconnected";
    public long DropsClaimedThisRun { get; private set; }
    public string? LastDropClaimed { get; private set; }
    public DateTimeOffset? LastDropClaimedUtc { get; private set; }
    public DateTimeOffset LastEventUtc { get; private set; } = DateTimeOffset.UtcNow;

    TimedDrop? _activeDrop;
    DropsCampaign? _activeCampaign;
    string? _activeChannel;
    string? _nextGame;
    string? _nextDrop;

    public bool ConnectionIssue { get; private set; }

    IReadOnlyList<HarvestingQueueItem> _queue = Array.Empty<HarvestingQueueItem>();
    bool _overrideActive;

    /// <summary>Records the current login state and account label.</summary>
    /// <param name="loggedIn">Whether a Twitch session is active.</param>
    /// <param name="account">The logged-in account label, or null when signed out.</param>
    public void SetAuth(bool loggedIn, string? account) => Mutate(() => { LoggedIn = loggedIn; Account = account; });

    /// <summary>Records whether harvesting is active and a human-readable summary of it.</summary>
    /// <param name="harvesting">Whether the harvester is currently running.</param>
    /// <param name="summary">A short description of the harvesting state.</param>
    public void SetHarvesting(bool harvesting, string summary) => Mutate(() => { Harvesting = harvesting; Summary = summary; });

    /// <summary>Records the current websocket connection description.</summary>
    /// <param name="status">A short description of the websocket state.</param>
    public void SetWebsocket(string status) => Mutate(() => Websocket = status);

    /// <summary>Records whether a connection issue is currently affecting harvesting.</summary>
    /// <param name="issue">True when Twitch Drops or the connection is down.</param>
    public void SetConnectionIssue(bool issue) => Mutate(() => ConnectionIssue = issue);

    /// <summary>Records the current harvesting queue and whether a manual override target is active.</summary>
    /// <param name="queue">The ordered harvesting queue items.</param>
    /// <param name="overrideActive">Whether a manual override target is in effect.</param>
    public void SetQueue(IReadOnlyList<HarvestingQueueItem> queue, bool overrideActive) =>
        Mutate(() => { _queue = queue; _overrideActive = overrideActive; });

    /// <summary>Records the channel, campaign, and drop the daemon is currently harvesting.</summary>
    /// <param name="channel">The channel being watched, or null when none.</param>
    /// <param name="campaign">The active campaign, or null when none.</param>
    /// <param name="drop">The active drop, or null when none.</param>
    public void SetTarget(TwitchChannel? channel, DropsCampaign? campaign, TimedDrop? drop) => Mutate(() =>
    {
        _activeChannel = channel?.DisplayName;
        _activeCampaign = campaign;
        _activeDrop = drop;
    });

    /// <summary>Updates the active drop model used for live progress reporting.</summary>
    /// <param name="drop">The drop currently being harvested.</param>
    public void SetDrop(TimedDrop drop) => Mutate(() => _activeDrop = drop);

    /// <summary>Records the game and drop queued up next after the current target.</summary>
    /// <param name="game">The next game name, or null when none.</param>
    /// <param name="drop">The next drop name, or null when none.</param>
    public void SetNextUp(string? game, string? drop) => Mutate(() => { _nextGame = game; _nextDrop = drop; });

    /// <summary>Increments the run's claimed-drop count and records the most recently claimed drop.</summary>
    /// <param name="name">The name of the drop that was just claimed.</param>
    public void RecordClaim(string name) => Mutate(() =>
    {
        DropsClaimedThisRun++;
        LastDropClaimed = name;
        LastDropClaimedUtc = DateTimeOffset.UtcNow;
    });

    /// <summary>Runs a state mutation under the lock and stamps the last-event time.</summary>
    /// <param name="a">The state mutation to perform while holding the lock.</param>
    void Mutate(Action a) { lock (_gate) { a(); LastEventUtc = DateTimeOffset.UtcNow; } }

    /// <summary>An anonymous object safe to JSON-serialize for the /status endpoint.</summary>
    public object Snapshot()
    {
        lock (_gate)
        {
            var drop = _activeDrop;
            var campaign = _activeCampaign;
            return new
            {
                version = DaemonInfo.Version,
                status = Harvesting ? "harvesting" : LoggedIn ? "idle" : "awaiting-login",
                loggedIn = LoggedIn,
                account = Account,
                harvesting = Harvesting,
                summary = Summary,
                activeGame = campaign?.Game.Name,
                activeChannel = _activeChannel,
                activeCampaign = campaign?.Name,
                activeDrop = drop?.RewardName,
                dropProgress = drop is null ? 0 : Math.Round(drop.Progress, 4),
                dropPercent = drop?.PercentText,
                dropProgressText = drop?.ProgressText,
                dropRemaining = drop?.RemainingText,
                dropRemainingSeconds = drop?.RemainingSeconds,
                campaignProgress = campaign is null ? 0 : Math.Round(campaign.Progress, 4),
                campaignPercent = campaign?.PercentText,
                campaignOverall = campaign?.OverallText,
                campaignRemaining = campaign?.RemainingText,
                campaignRemainingSeconds = campaign?.RemainingSeconds,
                nextUp = _nextGame is null && _nextDrop is null ? null : new { game = _nextGame, drop = _nextDrop },
                overrideActive = _overrideActive,
                queue = _queue.Select(i => new { game = i.Game, campaign = i.CampaignName, drop = i.DropName, active = i.IsActive, isOverride = i.IsOverride }).ToList(),
                websocket = Websocket,
                connectionIssue = ConnectionIssue,
                dropsClaimedThisRun = DropsClaimedThisRun,
                lastDropClaimed = LastDropClaimed,
                lastDropClaimedUtc = LastDropClaimedUtc,
                startedUtc = StartedUtc,
                uptimeSeconds = (long)(DateTimeOffset.UtcNow - StartedUtc).TotalSeconds,
                lastEventUtc = LastEventUtc,
            };
        }
    }
}
