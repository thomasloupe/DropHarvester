using DropHarvester.Models.Twitch;

namespace DropHarvester.Models.Events;

/// <summary>Severity of a log line emitted by the harvester. Debug is internal-only: it reaches the debug
/// server's /log but is hidden from the in-app Log page so regular users aren't shown per-tick chatter.</summary>
public enum HarvesterLogLevel { Info, Warn, Error, Debug }

/// <summary>
/// Strongly-typed events emitted by the harvesting engine. The UI, notifications, stats and webhook
/// notifier all subscribe to these via <c>IHarvesterEventBus</c> without coupling to the orchestrator.
/// </summary>
public abstract record HarvesterEvent
{
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>A line for the output log.</summary>
public sealed record LogEvent(string Message, HarvesterLogLevel Level = HarvesterLogLevel.Info) : HarvesterEvent;

/// <summary>Login state changed (logged in/out, or session became invalid).</summary>
public sealed record LoginStateEvent(bool LoggedIn, string? Username) : HarvesterEvent;

/// <summary>The stored token was rejected and the user must log in again.</summary>
public sealed record LoginExpiredEvent : HarvesterEvent;

/// <summary>Overall harvesting run state changed (started / paused / idle).</summary>
public sealed record HarvestingStateEvent(bool Active, string Summary) : HarvesterEvent;

/// <summary>Watch-time progressed on the active drop.</summary>
public sealed record DropProgressEvent(TimedDrop Drop) : HarvesterEvent;

/// <summary>A drop was claimed.</summary>
public sealed record DropClaimedEvent(TimedDrop Drop, DropsCampaign Campaign) : HarvesterEvent;

/// <summary>Channel-points bonus was auto-claimed on the watched channel.</summary>
public sealed record PointsClaimedEvent(string Channel, int Points) : HarvesterEvent;

/// <summary>Every drop in a campaign has been harvested/claimed.</summary>
public sealed record CampaignCompletedEvent(DropsCampaign Campaign) : HarvesterEvent;

/// <summary>No more available drops to harvest anywhere - the harvester went idle.</summary>
public sealed record AllDropsHarvestedEvent : HarvesterEvent;

/// <summary>A newly-available drop was discovered for a game (fires once per drop).</summary>
public sealed record NewDropAvailableEvent(DropsCampaign Campaign, TimedDrop Drop) : HarvesterEvent;

/// <summary>The active channel changed (null when nothing is being watched).</summary>
public sealed record ChannelSwitchedEvent(TwitchChannel? Channel, string Reason) : HarvesterEvent;

/// <summary>The current harvesting target: channel + campaign + drop (any may be null when idle).</summary>
public sealed record ActiveTargetEvent(TwitchChannel? Channel, DropsCampaign? Campaign, TimedDrop? Drop) : HarvesterEvent;

/// <summary>What the harvester will move to NEXT after the active target (the next harvestable campaign in
/// order + its first harvestable drop). Refreshed as the ordering changes; both null when nothing is
/// queued. Powers the Status tab's "Up next" preview.</summary>
public sealed record NextUpEvent(DropsCampaign? Campaign, TimedDrop? Drop) : HarvesterEvent;

/// <summary>One row of the harvesting queue (ordered harvestable campaigns) for the Status tab.</summary>
public sealed record HarvestingQueueItem(
    string CampaignId, string Game, string CampaignName, string? DropName, string? DropImageUrl,
    bool IsActive, bool IsOverride);

/// <summary>The full ordered harvesting queue + whether a manual campaign override is in effect. The user
/// can click a row to force that campaign (override) and clear it to resume automatic selection.</summary>
public sealed record HarvestingQueueEvent(IReadOnlyList<HarvestingQueueItem> Items, bool OverrideActive) : HarvesterEvent;

/// <summary>Websocket pool health for the Status tab.</summary>
public sealed record WebsocketStatusEvent(int Shards, int Topics, bool AllConnected) : HarvesterEvent;

/// <summary>A non-fatal error worth surfacing.</summary>
public sealed record HarvesterErrorEvent(string Message) : HarvesterEvent;

/// <summary>Twitch Drops appear to be down / the connection was lost (HasIssue=true), or it has
/// recovered (HasIssue=false). Harvesting pauses/idles while true and resumes automatically when it clears.</summary>
public sealed record ConnectionIssueEvent(bool HasIssue) : HarvesterEvent;

/// <summary>Watches are being acknowledged but nothing is crediting across channels (Active=true), i.e. a
/// Twitch-side drops outage, or crediting has resumed (Active=false). Harvesting keeps running the whole
/// time so it picks back up the instant Twitch restores crediting.</summary>
public sealed record DropsOutageEvent(bool Active) : HarvesterEvent;
