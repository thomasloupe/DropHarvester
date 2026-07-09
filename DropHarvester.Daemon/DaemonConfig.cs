using DropHarvester.Models;
using Microsoft.Extensions.Logging;

namespace DropHarvester.Daemon;

/// <summary>
/// Reads the daemon's environment-variable configuration and overlays it onto the persisted
/// <see cref="AppSettings"/> before harvesting starts. Env values WIN over whatever is in settings.json
/// on the data volume, so a compose file / -e flags are the source of truth for a container.
/// </summary>
public static class DaemonConfig
{
    public static int HealthPort => IntEnv("DH_HEALTH_PORT", 8080);
    public static bool HealthEnabled => BoolEnv("DH_HEALTH_ENABLED", true);

    /// <summary>Overlays daemon environment-variable overrides onto the given settings, mutating them in place.</summary>
    /// <param name="s">The persisted settings to overlay env values onto.</param>
    /// <param name="logger">Logger used to report the resulting configuration.</param>
    public static void Apply(AppSettings s, ILogger logger)
    {
        if (ListEnv("DH_PRIORITY_GAMES") is { } pri) s.PriorityGames = pri;
        if (ListEnv("DH_EXCLUDE_GAMES") is { } exc) s.ExcludedGames = exc;
        if (ListEnv("DH_DEDUPE_GAMES") is { } ded) s.DedupeGames = ded;
        if (ListEnv("DH_HARVEST_UNLINKED_GAMES") is { } unl) s.HarvestUnlinkedGames = unl;
        if (ListEnv("DH_PREFERRED_CHANNELS") is { } prf) s.PreferredChannels = prf;
        if (ListEnv("DH_AVOIDED_CHANNELS") is { } avd) s.AvoidedChannels = avd;

        if (BoolEnvN("DH_PRIORITY_ONLY") is { } po) s.PriorityOnly = po;
        if (BoolEnvN("DH_ENDING_SOONEST") is { } es) s.EndingSoonest = es;
        if (BoolEnvN("DH_AVAILABILITY_PRIORITY") is { } ap) s.AvailabilityPriority = ap;
        if (BoolEnvN("DH_ENABLE_BADGES_EMOTES") is { } be) s.EnableBadgesEmotes = be;
        if (BoolEnvN("DH_CLAIM_CHANNEL_POINTS") is { } cp) s.AutoClaimChannelPoints = cp;
        if (BoolEnvN("DH_HARVEST_IMPOSSIBLE_DROPS") is { } mid) s.HarvestImpossibleDrops = mid;

        if (StrEnv("DH_PROXY") is { } proxy) s.Proxy = proxy;

        // Webhook: providing a URL enables webhooks unless explicitly disabled.
        if (StrEnv("DH_WEBHOOK_URL") is { } url) { s.WebhookUrl = url; s.WebhookEnabled = true; }
        if (BoolEnvN("DH_WEBHOOK_ENABLED") is { } we) s.WebhookEnabled = we;
        if (EnumEnv<WebhookKind>("DH_WEBHOOK_KIND") is { } kind) s.WebhookKind = kind;
        if (ListEnv("DH_WEBHOOK_EVENTS") is { } evs) ApplyWebhookEvents(s, evs);

        // Optional rich debug server (/snapshot, /log, /claims-raw), exposed on 0.0.0.0 when on.
        if (BoolEnvN("DH_DEBUG_SERVER") is { } dbg) s.DebugServerEnabled = dbg;
        if (IntEnvN("DH_DEBUG_SERVER_PORT") is { } dbgp) s.DebugServerPort = dbgp;

        // Desktop-only concepts have no meaning in a container.
        s.MinimizeToTray = false;
        s.PlaySoundOnDropClaimed = false;
        s.AutoCheckForUpdates = false;
        s.Autostart = false;

        logger.LogInformation(
            "Config: {PriCount} priority game(s), {ExcCount} excluded, webhook {Webhook}, health port {Port}.",
            s.PriorityGames.Count, s.ExcludedGames.Count,
            s.WebhookEnabled && !string.IsNullOrWhiteSpace(s.WebhookUrl) ? $"enabled ({s.WebhookKind})" : "off",
            HealthPort);
    }

    /// <summary>Enables exactly the webhook event flags named in the list (or all of them when it contains "all") and disables the rest.</summary>
    /// <param name="s">The settings whose per-event webhook flags are set.</param>
    /// <param name="events">The full set of event names to enable; unlisted events are disabled.</param>
    static void ApplyWebhookEvents(AppSettings s, List<string> events)
    {
        // Treat the provided list as the FULL set: enable the ones named, disable the rest. "all" enables all.
        var set = events.Select(e => e.Trim().ToLowerInvariant()).ToHashSet();
        /// <summary>Reports whether the named event should be enabled given the requested set.</summary>
        bool On(string name) => set.Contains("all") || set.Contains(name);
        s.WebhookOnDropClaimed = On("drop-claimed");
        s.WebhookOnCampaignComplete = On("campaign-complete");
        s.WebhookOnAllHarvested = On("all-harvested");
        s.WebhookOnNewDrop = On("new-drop");
        s.WebhookOnLoginExpired = On("login-expired");
    }

    /// <summary>Reads an environment variable, returning its trimmed value or null when unset or blank.</summary>
    /// <param name="key">The environment variable name.</param>
    /// <returns>The trimmed value, or null when the variable is missing or whitespace.</returns>
    static string? StrEnv(string key)
    {
        var v = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    /// <summary>Reads a comma-separated environment variable into a trimmed list of non-empty entries.</summary>
    /// <param name="key">The environment variable name.</param>
    /// <returns>The parsed list, or null when the variable is unset or blank.</returns>
    static List<string>? ListEnv(string key) => StrEnv(key)?
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();

    /// <summary>Parses an environment variable as a tri-state boolean (1/true/yes/on vs 0/false/no/off).</summary>
    /// <param name="key">The environment variable name.</param>
    /// <returns>True or false for a recognized value; null when unset or unrecognized.</returns>
    static bool? BoolEnvN(string key) => StrEnv(key) switch
    {
        null => null,
        var v when v is "1" or "true" or "yes" or "on" => true,
        var v when v is "0" or "false" or "no" or "off" => false,
        _ => null,
    };

    /// <summary>Parses an environment variable as a boolean, falling back to a default when unset or unrecognized.</summary>
    /// <param name="key">The environment variable name.</param>
    /// <param name="fallback">The value returned when the variable is unset or unrecognized.</param>
    static bool BoolEnv(string key, bool fallback) => BoolEnvN(key) ?? fallback;

    /// <summary>Parses an environment variable as an integer.</summary>
    /// <param name="key">The environment variable name.</param>
    /// <returns>The parsed integer, or null when unset or not a valid integer.</returns>
    static int? IntEnvN(string key) => int.TryParse(StrEnv(key), out var n) ? n : null;

    /// <summary>Parses an environment variable as an integer, falling back to a default when unset or invalid.</summary>
    /// <param name="key">The environment variable name.</param>
    /// <param name="fallback">The value returned when the variable is unset or invalid.</param>
    static int IntEnv(string key, int fallback) => IntEnvN(key) ?? fallback;

    /// <summary>Parses an environment variable as a value of the given enum type, case-insensitively.</summary>
    /// <param name="key">The environment variable name.</param>
    /// <returns>The parsed enum value, or null when unset or not a valid member.</returns>
    static TEnum? EnumEnv<TEnum>(string key) where TEnum : struct =>
        Enum.TryParse<TEnum>(StrEnv(key), ignoreCase: true, out var e) ? e : null;
}
