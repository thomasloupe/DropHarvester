using DropHarvester.Localization;
using DropHarvester.Models;
using DropHarvester.Models.Events;

namespace DropHarvester.Services;

/// <summary>
/// Bridges the harvester event bus to the bonus features: desktop notifications, remote webhooks,
/// stats recording, and the tray status line. Instantiated once at startup (its constructor
/// subscribes to the bus).
/// </summary>
public sealed class AlertsCoordinator
{
    readonly INotificationService _notify;
    readonly IWebhookNotifier _webhook;
    readonly IStatsService _stats;
    readonly ISettingsStore _settings;
    readonly ITrayService _tray;
    readonly ISoundService _sound;

    /// <summary>Subscribes to the harvester event bus and captures the services used to raise alerts, record stats, and drive the tray.</summary>
    /// <param name="bus">Event bus whose harvester events this coordinator reacts to.</param>
    /// <param name="notify">Desktop notification sink.</param>
    /// <param name="webhook">Remote webhook notifier.</param>
    /// <param name="stats">Stats recorder.</param>
    /// <param name="settings">Settings store providing the alert toggles.</param>
    /// <param name="tray">Tray/menu-bar status sink.</param>
    /// <param name="sound">Sound playback service for claim sounds.</param>
    public AlertsCoordinator(
        IHarvesterEventBus bus,
        INotificationService notify,
        IWebhookNotifier webhook,
        IStatsService stats,
        ISettingsStore settings,
        ITrayService tray,
        ISoundService sound)
    {
        _notify = notify;
        _webhook = webhook;
        _stats = stats;
        _settings = settings;
        _tray = tray;
        _sound = sound;
        bus.Event += OnEvent;
    }

    AppSettings S => _settings.Settings;

    /// <summary>Reacts to one harvester event: records stats and fires the configured notifications, webhooks, sounds, and tray updates.</summary>
    /// <param name="e">The harvester event to handle.</param>
    void OnEvent(HarvesterEvent e)
    {
        switch (e)
        {
            case DropProgressEvent:
                _stats.RecordWatchMinute();
                break;

            case DropClaimedEvent d:
                _stats.RecordDropClaimed(d.Drop, d.Campaign);
                Fire(S.NotifyOnDropClaimed, S.WebhookOnDropClaimed,
                    Loc.T("Notif_DropClaimed"), Loc.T("Notif_NameDashGame", d.Drop.Name, d.Campaign.Game.Name));
                if (S.PlaySoundOnDropClaimed && !string.IsNullOrEmpty(S.DropClaimedSoundPath))
                    try { _sound.Play(S.DropClaimedSoundPath!, S.AudioOutputDeviceId, S.DropClaimedSoundVolume); } catch { }
                break;

            case CampaignCompletedEvent c:
                _stats.RecordCampaignCompleted(c.Campaign);
                Fire(S.NotifyOnCampaignComplete, S.WebhookOnCampaignComplete,
                    Loc.T("Notif_CampaignComplete"), Loc.T("Notif_NameDashGame", c.Campaign.Name, c.Campaign.Game.Name));
                break;

            case AllDropsHarvestedEvent:
                Fire(S.NotifyOnAllHarvested, S.WebhookOnAllHarvested,
                    Loc.T("Notif_AllHarvested"), Loc.T("Notif_AllHarvestedBody"));
                break;

            case NewDropAvailableEvent nd:
                Fire(false, S.WebhookOnNewDrop,
                    Loc.T("Notif_NewDrop"), Loc.T("Notif_NameDashGame", nd.Drop.RewardName, nd.Campaign.Game.Name));
                break;

            case LoginExpiredEvent:
                Fire(S.NotifyOnLoginExpired, S.WebhookOnLoginExpired,
                    Loc.T("Notif_LoginExpired"), Loc.T("Notif_LoginExpiredBody"));
                break;

            case HarvestingStateEvent m:
                // Tray/notification calls are WinRT/COM - run them on the UI thread.
                MainThread.BeginInvokeOnMainThread(() => { try { _tray.SetStatus(m.Summary); } catch { } });
                break;
        }
    }

    /// <summary>Raises a desktop notification and/or webhook for an alert, each gated by its enable flag.</summary>
    /// <param name="notifyEnabled">Whether to show a desktop notification.</param>
    /// <param name="webhookEnabled">Whether to send the webhook (also requires the global webhook toggle).</param>
    /// <param name="title">Alert title.</param>
    /// <param name="message">Alert body text.</param>
    void Fire(bool notifyEnabled, bool webhookEnabled, string title, string message)
    {
        if (notifyEnabled)
        {
            MainThread.BeginInvokeOnMainThread(() => { try { _notify.Notify(title, message); } catch { } });
        }
        if (webhookEnabled && S.WebhookEnabled)
        {
            _ = _webhook.SendAsync(title, message); // plain HTTP, thread-safe
        }
    }
}
