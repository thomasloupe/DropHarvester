namespace DropHarvester.Models;

/// <summary>Persisted user settings.</summary>
public sealed class AppSettings
{
    /// <summary>UI language as a culture code (e.g. "en", "es", "fr"). Falls back to English for an
    /// unknown code or any string the chosen language hasn't translated yet.</summary>
    public string Language { get; set; } = "en";

    /// <summary>Games to harvest first, in order of preference (by exact game name).</summary>
    public List<string> PriorityGames { get; set; } = new();

    /// <summary>Games to never harvest (by exact game name).</summary>
    public List<string> ExcludedGames { get; set; } = new();

    /// <summary>Games to de-duplicate: skip drops whose reward the user has already claimed
    /// (per Twitch's ~6-month claimed-drop history). Empty by default - opt in per game.</summary>
    public List<string> DedupeGames { get; set; } = new();

    /// <summary>Games whose campaigns to harvest even when the account isn't linked to that drops program.
    /// Opt-in per game; these are ALWAYS harvested at the lowest priority (after every linked campaign),
    /// and may not credit if the campaign actually requires a link.</summary>
    public List<string> HarvestUnlinkedGames { get; set; } = new();

    /// <summary>Include unlinked games' channels in the Channels tab. Off by default - skipping them
    /// saves directory fetches (API calls / rate-limit budget).</summary>
    public bool ShowUnlinkedInChannels { get; set; }

    /// <summary>Channel logins to PREFER (right-click a channel to add). When one of these is live for
    /// the game being harvested and no official/allow-listed channel is required, DropHarvester idles on it
    /// instead of a random top-viewer stream - generic drops still credit, so you support the streamer
    /// at no cost to harvesting. Priority/ending-soonest game order still decides WHICH game is harvested; an
    /// official (allow-listed) channel always wins.</summary>
    public List<string> PreferredChannels { get; set; } = new();

    /// <summary>Channel logins to AVOID (right-click a channel to add). Never idled on unless it's the
    /// only drops-enabled stream live for the game right now (last resort).</summary>
    public List<string> AvoidedChannels { get; set; } = new();

    /// <summary>When true, harvest ONLY games on the priority list; ignore everything else.</summary>
    public bool PriorityOnly { get; set; }

    /// <summary>Order eligible campaigns by soonest-ending instead of by priority-list position
    /// (so a lower-listed game expiring sooner is harvested first). Always on when no priority list.</summary>
    public bool EndingSoonest { get; set; }

    /// <summary>Factor streamer scarcity into ordering: when campaigns are otherwise tied by the current
    /// order, prefer the one whose game has fewer live drops-enabled streamers (scarce now = higher risk
    /// of no stream later). Off by default; a tiebreaker only - never overrides the priority list.</summary>
    public bool AvailabilityPriority { get; set; }

    /// <summary>Harvest a drop even if the campaign will end before it can be completed. Off by default;
    /// on for people betting on the occasional campaign extension.</summary>
    public bool HarvestImpossibleDrops { get; set; }

    /// <summary>Also harvest campaigns whose only rewards are badges/emotes (usually require no link).</summary>
    public bool EnableBadgesEmotes { get; set; }

    /// <summary>Also harvest subscription-gated drops (Twitch requiredSubs greater than zero). Off by
    /// default since they can't be earned by watch time unless you hold the required subs.</summary>
    public bool HarvestSubDrops { get; set; }

    /// <summary>Auto-claim the channel-points bonus chest on the channel being watched.</summary>
    public bool AutoClaimChannelPoints { get; set; }

    /// <summary>What clicking "Harvest" (manual override) does: harvest the whole campaign, just its next drop
    /// then resume automatic selection, or ask each time.</summary>
    public OverrideMode OverrideMode { get; set; } = OverrideMode.EntireCampaign;

    /// <summary>While an override is active, allow a campaign that newly appeared AFTER the override was
    /// set AND ranks higher in the effective order (ending-soonest, availability, or priority list) to end
    /// the override automatically so it gets harvested. A campaign already known when the override was set never
    /// ends it. Off = the override runs to completion / manual removal, uninterrupted. Remembered.</summary>
    public bool OverrideYieldsToPriority { get; set; }

    /// <summary>Optional HTTP/HTTPS/SOCKS proxy URL for all Twitch traffic.</summary>
    public string? Proxy { get; set; }

    // ----- Drop-claimed sound -----
    /// <summary>Play a sound effect when a drop is claimed.</summary>
    public bool PlaySoundOnDropClaimed { get; set; }

    /// <summary>Full path to the user-chosen sound file (wav/mp3/etc.). Null = none chosen.</summary>
    public string? DropClaimedSoundPath { get; set; }

    /// <summary>Output device the claim sound plays through (platform id; null/empty = system default).</summary>
    public string? AudioOutputDeviceId { get; set; }

    /// <summary>Claim-sound volume, 0.0 (silent) to 1.0 (full). Default full.</summary>
    public double DropClaimedSoundVolume { get; set; } = 1.0;

    // ----- App behavior -----
    public bool MinimizeToTray { get; set; } = true;
    public bool Autostart { get; set; }

    // Remembered main-window position/size (null until first saved).
    public double? WindowX { get; set; }
    public double? WindowY { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }

    /// <summary>When autostarted, launch straight into the tray (hidden window).</summary>
    public bool AutostartIntoTray { get; set; }

    // ----- Desktop notifications -----
    public bool NotifyOnDropClaimed { get; set; } = true;
    public bool NotifyOnCampaignComplete { get; set; } = true;
    public bool NotifyOnAllHarvested { get; set; } = true;
    public bool NotifyOnLoginExpired { get; set; } = true;

    // ----- Remote webhook alerts -----
    public bool WebhookEnabled { get; set; }
    public string? WebhookUrl { get; set; }
    public WebhookKind WebhookKind { get; set; } = WebhookKind.Discord;
    public bool WebhookOnNewDrop { get; set; }
    public bool WebhookOnDropClaimed { get; set; } = true;
    public bool WebhookOnCampaignComplete { get; set; } = true;
    public bool WebhookOnAllHarvested { get; set; } = true;
    public bool WebhookOnLoginExpired { get; set; } = true;

    // ----- Inventory view -----
    /// <summary>Inventory drop layout: false = compact horizontal rows (default), true = large
    /// vertical cards that wrap (max 5 per row). Persisted so the chosen view survives restarts.</summary>
    public bool InventoryVerticalDrops { get; set; }

    // ----- Log display -----
    public LogTimestampMode LogTimestampMode { get; set; } = LogTimestampMode.DateAndTime;
    public bool LogUse24Hour { get; set; } = true;

    // ----- Updates -----
    /// <summary>Check the update manifest for a newer version on startup.</summary>
    public bool AutoCheckForUpdates { get; set; } = true;

    // ----- Debug server -----
    /// <summary>Run a local HTTP debug server exposing live state + decisions (localhost only).</summary>
    public bool DebugServerEnabled { get; set; }

    /// <summary>Port for the debug server (localhost).</summary>
    public int DebugServerPort { get; set; } = 5757;
}

/// <summary>Which service's message format outgoing webhook alerts are built for.</summary>
public enum WebhookKind { Discord, Slack, Generic }

/// <summary>What a manual "Harvest" override does.</summary>
public enum OverrideMode { EntireCampaign, DropOnly, AskMe }

/// <summary>What the Log tab's per-line timestamp shows.</summary>
public enum LogTimestampMode { Date, Time, DateAndTime, TimeAndDate }
