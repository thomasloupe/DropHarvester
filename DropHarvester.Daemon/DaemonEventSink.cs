using DropHarvester.Models;
using DropHarvester.Models.Events;
using DropHarvester.Services;
using Microsoft.Extensions.Logging;

namespace DropHarvester.Daemon;

/// <summary>
/// The headless equivalent of the app's AlertsCoordinator: subscribes to the harvester event bus and
/// (a) logs notable events to stdout, (b) records lifetime stats, (c) fires webhook alerts, (d) keeps
/// <see cref="DaemonStatus"/> current, and (e) trips the <see cref="ReauthGate"/> when the session
/// expires. No tray / notifications / sound - those are desktop-only. Instantiated once at startup
/// (its constructor subscribes to the bus).
/// </summary>
public sealed class DaemonEventSink
{
    readonly IStatsService _stats;
    readonly IWebhookNotifier _webhook;
    readonly ISettingsStore _settings;
    readonly DaemonStatus _status;
    readonly ReauthGate _reauth;
    readonly ILogger<DaemonEventSink> _log;

    /// <summary>Subscribes to the harvester event bus so daemon events are logged, recorded, mirrored to status, and webhooked.</summary>
    /// <param name="bus">The harvester event bus to subscribe to.</param>
    /// <param name="stats">Lifetime stats service updated on claim and progress events.</param>
    /// <param name="webhook">Notifier used to send webhook alerts.</param>
    /// <param name="settings">Settings store providing the current webhook configuration.</param>
    /// <param name="status">Live daemon status updated from events.</param>
    /// <param name="reauth">Gate tripped when the Twitch session expires.</param>
    /// <param name="log">Logger for notable events.</param>
    public DaemonEventSink(
        IHarvesterEventBus bus,
        IStatsService stats,
        IWebhookNotifier webhook,
        ISettingsStore settings,
        DaemonStatus status,
        ReauthGate reauth,
        ILogger<DaemonEventSink> log)
    {
        _stats = stats;
        _webhook = webhook;
        _settings = settings;
        _status = status;
        _reauth = reauth;
        _log = log;
        bus.Event += OnEvent;
    }

    AppSettings S => _settings.Settings;

    /// <summary>Handles a single harvester event, updating logs, stats, status, webhooks, and the reauth gate as appropriate.</summary>
    /// <param name="e">The harvester event to process.</param>
    void OnEvent(HarvesterEvent e)
    {
        switch (e)
        {
            case LogEvent l:
                _log.Log(Map(l.Level), "{Message}", l.Message);
                break;

            case HarvesterErrorEvent er:
                _log.LogError("{Message}", er.Message);
                break;

            case HarvestingStateEvent m:
                _status.SetHarvesting(m.Active, m.Summary);
                _log.LogInformation("Harvesting: {Summary}", m.Summary);
                break;

            case ActiveTargetEvent t:
                _status.SetTarget(t.Channel, t.Campaign, t.Drop);
                if (t.Channel is not null || t.Campaign is not null)
                    _log.LogInformation("Target: {Game} on {Channel} -> {Drop}",
                        t.Campaign?.Game.Name ?? "-", t.Channel?.DisplayName ?? "-", t.Drop?.RewardName ?? "-");
                break;

            case NextUpEvent nu:
                _status.SetNextUp(nu.Campaign?.Game.Name, nu.Drop?.RewardName);
                if (nu.Campaign is not null)
                    _log.LogInformation("Up next: {Game} -> {Drop}", nu.Campaign.Game.Name, nu.Drop?.RewardName ?? "-");
                break;

            case HarvestingQueueEvent q:
                _status.SetQueue(q.Items, q.OverrideActive);
                break;

            case DropProgressEvent d:
                _stats.RecordWatchMinute();
                _status.SetDrop(d.Drop);
                break;

            case DropClaimedEvent dc:
                _stats.RecordDropClaimed(dc.Drop, dc.Campaign);
                _status.RecordClaim(dc.Drop.Name);
                _log.LogInformation("Drop claimed: {Drop} - {Game}", dc.Drop.Name, dc.Campaign.Game.Name);
                Fire(S.WebhookOnDropClaimed, "Drop claimed", $"{dc.Drop.Name} - {dc.Campaign.Game.Name}");
                break;

            case CampaignCompletedEvent c:
                _stats.RecordCampaignCompleted(c.Campaign);
                _log.LogInformation("Campaign complete: {Campaign} - {Game}", c.Campaign.Name, c.Campaign.Game.Name);
                Fire(S.WebhookOnCampaignComplete, "Campaign complete", $"{c.Campaign.Name} - {c.Campaign.Game.Name}");
                break;

            case AllDropsHarvestedEvent:
                _log.LogInformation("All available drops harvested - harvester idle.");
                Fire(S.WebhookOnAllHarvested, "All drops harvested", "No more drops available to harvest right now.");
                break;

            case NewDropAvailableEvent nd:
                Fire(S.WebhookOnNewDrop, "New drop available", $"{nd.Drop.RewardName} - {nd.Campaign.Game.Name}");
                break;

            case ChannelSwitchedEvent cs when cs.Channel is not null:
                _log.LogInformation("Watching {Channel} ({Reason}).", cs.Channel.DisplayName, cs.Reason);
                break;

            case PointsClaimedEvent p:
                _log.LogInformation("Claimed {Points} channel points on {Channel}.", p.Points, p.Channel);
                break;

            case ConnectionIssueEvent ci:
                _status.SetConnectionIssue(ci.HasIssue);
                if (ci.HasIssue)
                    _log.LogWarning("Twitch Drops down / connection lost - harvesting paused; will auto-resume.");
                else
                    _log.LogInformation("Connection re-established - resuming harvesting.");
                break;

            case WebsocketStatusEvent w:
                _status.SetWebsocket(w.Shards == 0
                    ? "disconnected"
                    : $"{w.Shards} shard(s), {w.Topics} topic(s), {(w.AllConnected ? "connected" : "connecting")}");
                break;

            case LoginExpiredEvent:
                _log.LogWarning("Twitch session expired - re-authentication required.");
                _status.SetAuth(false, null);
                Fire(S.WebhookOnLoginExpired, "Login expired", "Your Twitch session expired - re-authenticate via the container logs.");
                _reauth.Request();
                break;
        }
    }

    /// <summary>Sends a webhook alert when the per-event flag and the global webhook settings are all enabled.</summary>
    /// <param name="enabled">Whether this specific event's webhook is enabled.</param>
    /// <param name="title">The alert title.</param>
    /// <param name="message">The alert body.</param>
    void Fire(bool enabled, string title, string message)
    {
        if (enabled && S.WebhookEnabled && !string.IsNullOrWhiteSpace(S.WebhookUrl))
            _ = _webhook.SendAsync(title, message);
    }

    /// <summary>Maps a harvester log level onto the corresponding logging framework level.</summary>
    /// <param name="l">The harvester log level.</param>
    /// <returns>The equivalent LogLevel.</returns>
    static LogLevel Map(HarvesterLogLevel l) => l switch
    {
        HarvesterLogLevel.Warn => LogLevel.Warning,
        HarvesterLogLevel.Error => LogLevel.Error,
        _ => LogLevel.Information,
    };
}
