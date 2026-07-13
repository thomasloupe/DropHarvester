using System.Collections.ObjectModel;
using System.Text.Json;
using DropHarvester.Localization;
using DropHarvester.Models;
using DropHarvester.Models.Events;
using DropHarvester.Models.Twitch;

namespace DropHarvester.Services.Twitch;

/// <summary>
/// The harvesting controller. Owns the async run loop: discover campaigns,
/// pick the highest-priority campaign with unharvested drops, choose a live drops-enabled channel,
/// send the stream-less watch heartbeat, sync/claim progress, and switch channels when the
/// current one goes offline or a higher-priority game comes online. All notable events are
/// published on the <see cref="IHarvesterEventBus"/>.
/// </summary>
public interface IHarvesterOrchestrator
{
    bool IsRunning { get; }
    TwitchChannel? ActiveChannel { get; }
    DropsCampaign? ActiveCampaign { get; }
    TimedDrop? ActiveDrop { get; }
    ObservableCollection<DropsCampaign> Campaigns { get; }
    ObservableCollection<TwitchChannel> TrackedChannels { get; }

    /// <summary>Harvestable games in harvesting order (for the Channels tab's group order, incl. games with
    /// no live channels right now).</summary>
    IReadOnlyList<string> HarvestableGames { get; }

    /// <summary>True while the Channels tab's channel list is being (re)gathered - drives a spinner.</summary>
    bool IsRefreshingChannels { get; }
    event Action? ChannelRefreshStateChanged;

    /// <summary>Raised when the preferred/avoided channel lists change (so Settings can refresh).</summary>
    event Action? ChannelPreferencesChanged;

    /// <summary>Toggle a channel on/off the PREFERRED list (persisted); updates its row flag.</summary>
    /// <param name="channel">The channel to prefer or un-prefer.</param>
    void TogglePreferChannel(TwitchChannel channel);

    /// <summary>Toggle a channel on/off the AVOIDED list (persisted); updates its row flag.</summary>
    /// <param name="channel">The channel to avoid or un-avoid.</param>
    void ToggleAvoidChannel(TwitchChannel channel);

    /// <summary>Re-apply the preferred/avoided flags to the tracked channel rows from settings (e.g.
    /// after a list was edited in Settings), so the Channels tab's badges stay in sync.</summary>
    void SyncChannelPreferences();

    /// <summary>Start harvesting (no-op if already running or not logged in).</summary>
    Task StartAsync();

    /// <summary>Stop harvesting and tear down the websocket pool.</summary>
    Task StopAsync();

    /// <summary>Force a fresh campaign discovery on the next loop tick.</summary>
    void RequestRefresh();

    /// <summary>Manually switch to a specific channel (of the current game) on the next tick.</summary>
    /// <param name="channel">The channel to switch to.</param>
    void RequestSwitchTo(TwitchChannel channel);

    /// <summary>The campaign id pinned as a manual override (harvest ONLY this), or null for automatic.</summary>
    string? OverrideCampaignId { get; }

    /// <summary>Pin a campaign as a manual override. <paramref name="dropOnly"/> true = harvest just its
    /// next drop then resume automatic selection; false = harvest the whole campaign until cleared.</summary>
    /// <param name="campaignId">Id of the campaign to pin.</param>
    /// <param name="dropOnly">True to release the override after one drop is claimed; false to hold it until cleared.</param>
    void SetCampaignOverride(string campaignId, bool dropOnly);

    /// <summary>Clear the manual override and let DropHarvester resume automatic campaign selection.</summary>
    void ClearCampaignOverride();

    /// <summary>Debug/testing: pin the harvester onto the campaign that owns the given campaign OR drop id
    /// (held until cleared), so one specific drop can be exercised on demand. Returns a human-readable
    /// result describing the target, or why nothing matched.</summary>
    /// <param name="id">A campaign id or a drop id to resolve to its campaign and harvest.</param>
    string DebugForceHarvest(string id);

    /// <summary>Debug/testing: the campaigns the harvester could pin right now, each with its id and first
    /// unclaimed drop id, so a /harvest target can be picked without guessing ids.</summary>
    IReadOnlyList<object> DebugHarvestTargets();

    /// <summary>Debug/testing: send one minute-watched heartbeat for the ACTIVE channel right now and return
    /// the full round-trip (exact payload, HTTP status, raw response, sent headers) to diagnose a
    /// 204-acked-but-not-credited watch.</summary>
    /// <param name="ct">Token to cancel the probe.</param>
    Task<object> DebugWatchProbeAsync(CancellationToken ct = default);

    /// <summary>Debug/testing: the current auth/session context (ids present, token length, validated-at),
    /// with secrets redacted, to confirm the request is authenticated as expected.</summary>
    object DebugAuthState();

    /// <summary>Whether the harvester considers this campaign (by id) finished - all drops claimed/earned per
    /// its authoritative, continuously-updated claim state. The Inventory's Finished filter uses this so
    /// it can't disagree with what the harvester will actually harvest.</summary>
    /// <param name="campaignId">Id of the campaign to test.</param>
    bool IsCampaignFinished(string campaignId);

    /// <summary>Whether every reward in this campaign has actually been CLAIMED per the harvester's ledger
    /// (Twitch's own self.isClaimed, or our claim ledger when Twitch's self lags at 0/unclaimed). Unlike
    /// <see cref="IsCampaignFinished"/> this does NOT count a merely 100%-watched-but-unclaimed drop, so the
    /// Inventory's Finished filter can move a claimed campaign there without hiding one still awaiting a claim.
    /// A sub-only reward we can't earn (a "buy drop" tier) doesn't block a campaign from counting as claimed.</summary>
    /// <param name="campaignId">Id of the campaign to test.</param>
    bool AreAllRewardsClaimed(string campaignId);

    /// <summary>Whether the harvester recorded this specific drop (by its definition id) as claimed in its
    /// per-tier ledger this session - the truth even when Twitch's per-drop self has lagged back to
    /// 0/unclaimed. The Inventory uses it to render a claimed drop as done, not 0%.</summary>
    /// <param name="dropId">The drop-definition id to test.</param>
    bool WasDropClaimed(string dropId);

    /// <summary>Whether the harvester has a drop in this campaign (by id) it would harvest RIGHT NOW (eligible,
    /// an unclaimed/unfinished/finishable drop). The Inventory's Finished filter uses this as a HARD
    /// override: a campaign the harvester would actively harvest can never be shown as finished - even if its
    /// rewards are already owned from a PAST campaign that reused the same reward ids (SMITE2 "Market
    /// Coins" bundles) or only some of its tiers are claimed (R6S "Esports Pack").</summary>
    /// <param name="campaignId">Id of the campaign to test.</param>
    bool IsCampaignHarvestable(string campaignId);

    /// <summary>A serializable snapshot of the harvester's live state + per-campaign/drop decisions, for
    /// the debug server (why each campaign is harvested/skipped/finished, claim attribution, etc.).</summary>
    object GetDebugSnapshot();

    /// <summary>Every discovered campaign (NOT just the harvestable candidates the snapshot shows), each
    /// with its Status/IsActive/IsFinished and per-drop state, so a campaign the harvester is skipping or
    /// treating as finished can be inspected even though it's filtered out of the main snapshot.</summary>
    object DebugAllCampaigns();
}

public sealed class HarvesterOrchestrator : IHarvesterOrchestrator
{
    readonly ITwitchAuth _auth;
    readonly IInventoryService _inventory;
    readonly IChannelManager _channels;
    readonly IWatchService _watch;
    readonly IWebsocketPool _ws;
    readonly IHarvesterEventBus _bus;
    readonly ISettingsStore _settings;
    readonly IChannelPointsService _points;
    readonly IClaimLedger _ledger;

    CancellationTokenSource? _cts;
    Task? _loop;
    volatile bool _refreshRequested;
    volatile bool _switchRequested;
    // a drop-claim pubsub message arrived: sync + claim now instead of waiting for the next watch tick
    volatile bool _claimNowRequested;
    // last time the all-campaign safety-net claim sweep ran (throttles the idle/startup sweep)
    DateTimeOffset _lastClaimSweep = DateTimeOffset.MinValue;
    // most recent status line (incl. the reason when idle, e.g. "waiting for a stream") - for the debug snapshot
    volatile string _lastSummary = "Idle";
    TwitchChannel? _forcedChannel;
    // when set, harvest ONLY this campaign (id) until cleared, bypassing the automatic order
    volatile string? _forcedCampaignId;
    // "drop only": release the override automatically once one drop of the forced campaign is claimed
    volatile bool _forcedDropOnly;
    // campaign ids that existed when the current override was set (used to yield only to NEWER campaigns)
    HashSet<string> _knownAtOverride = new(StringComparer.OrdinalIgnoreCase);
    // when the current override was set - a campaign can only yield the override if it also STARTED after
    // this (so a campaign released earlier that we merely discovered late never counts as "new")
    DateTimeOffset _overrideSetUtc = DateTimeOffset.MinValue;
    DateTimeOffset _lastCampaignFetch = DateTimeOffset.MinValue;
    // Re-discover campaigns this often, even mid-harvest, so a newly-started campaign (e.g. a fresh
    // higher-priority drop) is picked up promptly instead of only when the current campaign finishes.
    const int RediscoverMinutes = 8;
    // harvesting loop's private snapshot: replaced (never mutated in place) so it can't be enumerated while
    // the UI-bound Campaigns collection is rebuilt on the UI thread ("Collection was modified")
    List<DropsCampaign> _campaigns = new();

    // reward id -> when last awarded (from claim history). Used to skip drops already claimed in the
    // current campaign, and (opt-in per the de-dupe list) any owned reward at all.
    readonly Dictionary<string, DateTimeOffset?> _claimedBenefits = new(StringComparer.OrdinalIgnoreCase);
    // per-TIER claim set (drop-definition ids), so a campaign whose tiers share one reward id (Marbles'
    // 15-coin drops) finishes tier-by-tier; survives Twitch's per-drop self lagging back to 0
    readonly HashSet<string> _claimedDropIds = new(StringComparer.OrdinalIgnoreCase);
    // reward id -> windows of all campaigns granting it, to attribute a claim to the right campaign
    Dictionary<string, List<(DateTimeOffset start, DateTimeOffset end)>> _benefitWindows = new(StringComparer.OrdinalIgnoreCase);
    bool _claimHistoryLogged; // logged once to confirm award dates are present
    // drops benched after giving up (id -> when to retry). Temporary so a run can't permanently drain its
    // candidate pool to "waiting for a stream" and need a restart - benched drops come back on their own.
    readonly Dictionary<string, DateTimeOffset> _skipDrops = new();
    const int GiveUpAfterMinutes = 15;
    const int SkipRetryMinutes = 25; // how long a given-up drop stays benched before it's retried
    // whether a drop is currently benched (its retry window hasn't passed yet)
    bool IsSkipped(string dropId) => _skipDrops.TryGetValue(dropId, out var until) && DateTimeOffset.UtcNow < until;
    // no progress on the CURRENT channel this long = it isn't crediting us -> bench it, try another
    // (OPEN campaigns only). Kept above the ~1-2 min Twitch inventory can lag so we don't bench falsely.
    const int StallSwitchMinutes = 6;
    const int ChannelCooldownMinutes = 10;
    // login -> time until which a stalled channel is skipped in selection
    readonly Dictionary<string, DateTimeOffset> _channelCooldownUntil = new(StringComparer.OrdinalIgnoreCase);
    // channel id -> tracked channel subscribed for real-time stream up/down, so the Channels tab reacts
    // instantly instead of only on the periodic directory refresh
    readonly Dictionary<string, TwitchChannel> _watchedChannels = new(StringComparer.OrdinalIgnoreCase);
    // game id -> live drops-enabled streamer count from the last channel gather, for availability-aware
    // ordering (capped at the gather's fetch limit, which is enough to tell scarce from plentiful)
    readonly Dictionary<string, int> _liveCountByGame = new(StringComparer.OrdinalIgnoreCase);
    // last skip reason logged per game, so a passed-over campaign is explained once, not every retry
    readonly Dictionary<string, string> _skipReasonLogged = new(StringComparer.OrdinalIgnoreCase);
    DateTimeOffset _lastPreemptCheck = DateTimeOffset.MinValue;
    // announced new-drop ids already seen (seeded silently on the first fetch, so no startup flood)
    readonly HashSet<string> _announcedDropIds = new();
    bool _announceSeeded;
    // _stallMinutes = no-progress ticks on the CURRENT channel (drives the fast switch); _dropStallMinutes
    // = no-progress ticks for the CURRENT drop across channels (drives give-up, so hopping can't dodge it)
    int _lastDropMinutes;
    int _stallMinutes;
    int _dropStallMinutes;
    string? _stallDropId;
    // When NOTHING credits across channels for this long while actively watching online streams, it's a
    // Twitch-side drops outage (watches are 204-acked but not credited), not a per-drop issue - surface a
    // calm banner and keep running so it resumes the instant Twitch restores crediting. Only used as the
    // fallback threshold when there are no backup transports to try first.
    const int OutageAfterMinutes = 12;
    DateTimeOffset _lastCreditUtc = DateTimeOffset.UtcNow;       // last time OUR TARGET drop advanced (stall clock)
    // Outage clock: last time Twitch credited ANYTHING (our target advanced, OR the credit-check session
    // advanced - proving Twitch's crediting pipeline is up even when it's not OUR exact target, e.g. we're
    // parked on an event campaign whose specific tier isn't earning here). Keyed separately from the stall
    // clock so being stuck on a locally-uncreditable campaign can't masquerade as a Twitch-wide outage.
    DateTimeOffset _lastTwitchCreditUtc = DateTimeOffset.UtcNow;
    string? _lastSessionDropId;   // last credit-check session drop id, to detect that session advancing
    int _lastSessionMins;         // last credit-check session minutes
    bool _outageActive;

    // Self-healing watch transport (mirrors the community "rotate the watch method" design): if nothing
    // credits for a while though watches are accepted, the current transport may be the one Twitch just
    // stopped crediting. We then walk the backup transports ONCE (a single bounded pass), giving each a
    // couple of minutes to prove it credits; whichever restores progress becomes the new primary, and if
    // none do it's a real outage - settle back and raise the banner. Set above StallSwitchMinutes so a
    // cheaper channel switch is tried first; the credit clock is global so it survives channel hops.
    const int NoProgressSwitchMinutes = 8;
    const int RotationStepMinutes = 2; // minutes each backup transport gets to prove it credits
    bool _rotating;                    // mid self-heal pass over the backup transports
    int _rotationStepMinutes;          // minutes spent on the current backup transport
    bool _selfHealExhausted;           // a full pass restored nothing; don't cycle again until a real credit

    /// <summary>What to do about a drop that isn't advancing: keep going, switch channel, or give up.</summary>
    enum StallAction { None, SwitchChannel, GiveUp }

    public bool IsRunning { get; private set; }
    public TwitchChannel? ActiveChannel { get; private set; }
    public DropsCampaign? ActiveCampaign { get; private set; }
    public TimedDrop? ActiveDrop { get; private set; }
    public ObservableCollection<DropsCampaign> Campaigns { get; } = new UiObservableCollection<DropsCampaign>();
    public ObservableCollection<TwitchChannel> TrackedChannels { get; } = new UiObservableCollection<TwitchChannel>();
    public IReadOnlyList<string> HarvestableGames { get; private set; } = Array.Empty<string>();
    public bool IsRefreshingChannels { get; private set; }
    public event Action? ChannelRefreshStateChanged;
    public event Action? ChannelPreferencesChanged;
    int _refreshingChannels;                                    // guard: only one channel-list gather at a time
    DateTimeOffset _lastChannelRefresh = DateTimeOffset.MinValue; // throttle background refreshes

    /// <summary>Set the "refreshing channels" flag and raise the change event when it flips.</summary>
    /// <param name="value">True while a channel gather is in progress.</param>
    void SetRefreshingChannels(bool value)
    {
        if (IsRefreshingChannels == value) return;
        IsRefreshingChannels = value;
        ChannelRefreshStateChanged?.Invoke();
    }

    /// <summary>Wire up the orchestrator's dependencies and subscribe to websocket events.</summary>
    /// <param name="auth">Twitch auth / token state.</param>
    /// <param name="inventory">Campaign discovery, inventory sync, and drop claiming.</param>
    /// <param name="channels">Live-channel directory lookups and per-channel refresh.</param>
    /// <param name="watch">Sends the minute-watched heartbeat that advances drops.</param>
    /// <param name="ws">Websocket pool for PubSub topics.</param>
    /// <param name="bus">Event bus notable harvesting events are published on.</param>
    /// <param name="settings">Persisted app settings store.</param>
    /// <param name="points">Channel-points bonus-chest claiming.</param>
    /// <param name="ledger">Persistent local claim ledger (survives Twitch self lag).</param>
    public HarvesterOrchestrator(
        ITwitchAuth auth,
        IInventoryService inventory,
        IChannelManager channels,
        IWatchService watch,
        IWebsocketPool ws,
        IHarvesterEventBus bus,
        ISettingsStore settings,
        IChannelPointsService points,
        IClaimLedger ledger)
    {
        _auth = auth;
        _inventory = inventory;
        _channels = channels;
        _watch = watch;
        _ws = ws;
        _bus = bus;
        _settings = settings;
        _points = points;
        _ledger = ledger;

        _ws.MessageReceived += OnPubSubMessage;
        _ws.StatusChanged += () => _bus.Publish(new WebsocketStatusEvent(_ws.ShardCount, _ws.TopicCount, _ws.AllConnected));
    }

    AppSettings Settings => _settings.Settings;

    /// <summary>Start harvesting (no-op if already running or not logged in).</summary>
    public Task StartAsync()
    {
        if (IsRunning || !_auth.IsLoggedIn)
            return Task.CompletedTask;

        IsRunning = true;
        _skipDrops.Clear(); // retry any previously given-up drops on a fresh run
        _channelCooldownUntil.Clear();
        _skipReasonLogged.Clear();
        _lastCreditUtc = DateTimeOffset.UtcNow; // don't inherit a stale "no credit" clock across restarts
        _lastTwitchCreditUtc = DateTimeOffset.UtcNow;
        _lastSessionDropId = null;
        _lastSessionMins = 0;
        _rotating = false;
        _rotationStepMinutes = 0;
        _selfHealExhausted = false;
        _watch.ResetTransport(); // start fresh on the preferred transport, not wherever a prior run rotated to
        ClearOutage();
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
        Log(Loc.T("Log_HarvestingStarted"));
        _bus.Publish(new HarvestingStateEvent(true, Loc.T("Status_Starting")));
        return Task.CompletedTask;
    }

    /// <summary>Stop harvesting, cancel the loop, and tear down the websocket pool.</summary>
    public async Task StopAsync()
    {
        if (!IsRunning)
            return;
        IsRunning = false;
        try { _cts?.Cancel(); } catch { }
        try { if (_loop is not null) await _loop.ConfigureAwait(false); } catch { }
        await _ws.StopAsync().ConfigureAwait(false);
        ClearOutage();
        SetActive(null, null, null, Loc.T("Status_Stopped"));
        _bus.Publish(new HarvestingStateEvent(false, Loc.T("Status_Stopped")));
        Log(Loc.T("Log_HarvestingStopped"));
    }

    /// <summary>Force a fresh campaign discovery on the next loop tick.</summary>
    public void RequestRefresh()
    {
        _refreshRequested = true;
        Wake(); // don't wait out the ~59s watch tick
    }

    /// <summary>Queue a manual switch to a specific channel, processed on the next tick.</summary>
    /// <param name="channel">The channel to switch to.</param>
    public void RequestSwitchTo(TwitchChannel channel)
    {
        _forcedChannel = channel;
        _channelCooldownUntil.Remove(channel.Login); // a manual pick overrides any stall bench
        _switchRequested = true; // break the current harvest loop (without a full campaign refetch)
        Wake();                  // process the switch now, not on the next watch tick
        Log($"Switching to {channel.DisplayName}...");
    }

    public string? OverrideCampaignId => _forcedCampaignId;

    /// <summary>Pin a campaign as a manual override and re-pick now.</summary>
    /// <param name="campaignId">Id of the campaign to pin.</param>
    /// <param name="dropOnly">True to release the override after one drop is claimed; false to hold it until cleared.</param>
    public void SetCampaignOverride(string campaignId, bool dropOnly)
    {
        _forcedCampaignId = campaignId;
        _forcedDropOnly = dropOnly;
        var known = _campaigns; // capture the atomically-swapped ref before enumerating
        _knownAtOverride = new HashSet<string>(known.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
        _overrideSetUtc = DateTimeOffset.UtcNow; // the "released after this = new" cutoff for yielding
        _switchRequested = true; // re-pick now, honoring the override
        Wake();
        Log(dropOnly
            ? "Manual override: harvesting the chosen campaign's next drop, then resuming automatic selection."
            : "Manual override: harvesting only the chosen campaign until you remove the override.");
    }

    /// <summary>Clear the manual override and resume automatic campaign selection.</summary>
    public void ClearCampaignOverride()
    {
        if (_forcedCampaignId is null)
            return;
        _forcedCampaignId = null;
        _forcedDropOnly = false;
        _switchRequested = true; // re-pick using the normal automatic order
        Wake();
        Log("Override removed - resuming automatic campaign selection.");
    }

    /// <summary>Debug/testing: pin the harvester onto the campaign owning the given campaign OR drop id and
    /// re-pick now, so a specific drop can be exercised on demand. Resolves an id that is either a campaign
    /// id or one of its drops' ids. Held until cleared (dropOnly=false) so crediting can be watched over
    /// several minutes.</summary>
    /// <param name="id">A campaign id or a drop id to resolve to its campaign and harvest.</param>
    /// <returns>A human-readable result describing the pinned target, or why nothing matched.</returns>
    public string DebugForceHarvest(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return "No id supplied. Use /harvest?id=<campaignId or dropId>, /harvest?clear=1, or /harvest to list targets.";

        var wanted = id.Trim();
        var known = _campaigns; // capture the atomically-swapped ref before enumerating

        var campaign = known.FirstOrDefault(c => string.Equals(c.Id, wanted, StringComparison.OrdinalIgnoreCase));
        TimedDrop? drop = null;
        if (campaign is null)
        {
            foreach (var c in known)
            {
                drop = c.Drops.FirstOrDefault(d => string.Equals(d.Id, wanted, StringComparison.OrdinalIgnoreCase));
                if (drop is not null) { campaign = c; break; }
            }
        }

        if (campaign is null)
            return $"No campaign or drop matches id '{wanted}'. Hit /harvest (no query) to list harvestable campaign and drop ids.";

        SetCampaignOverride(campaign.Id, dropOnly: false);
        var target = drop ?? campaign.FirstUnharvestedDrop;
        return target is null
            ? $"Pinned '{campaign.Name}' [{campaign.Game.Name}] (id {campaign.Id}) - no unclaimed drop left to harvest."
            : $"Pinned '{campaign.Name}' [{campaign.Game.Name}] -> drop '{target.RewardName}' ({target.CurrentMinutes}/{target.RequiredMinutes}m, id {target.Id}). Watch /snapshot for movement; /harvest?clear=1 to release.";
    }

    /// <summary>Debug/testing: the campaigns the harvester could pin right now, each with its id and first
    /// unclaimed drop, so a /harvest target can be picked without guessing ids.</summary>
    /// <returns>An anonymous-object list (campaign id/name/game/linked/eligible + first unclaimed drop).</returns>
    public IReadOnlyList<object> DebugHarvestTargets()
    {
        var known = _campaigns;
        var list = new List<object>();
        foreach (var c in known)
        {
            var next = c.FirstUnharvestedDrop;
            list.Add(new
            {
                CampaignId = c.Id,
                Campaign = c.Name,
                Game = c.Game.Name,
                c.Linked,
                LiveStreamers = _liveCountByGame.TryGetValue(c.Game.Id, out var lc) ? lc : 0,
                Finished = IsCampaignFinishedForHarvesting(c),
                NextDropId = next?.Id,
                NextDrop = next?.RewardName,
                NextDropProgress = next is null ? null : $"{next.CurrentMinutes}/{next.RequiredMinutes}m",
            });
        }
        return list;
    }

    /// <summary>Debug/testing: send one minute-watched heartbeat for the active channel and return the full
    /// round-trip for inspection, or an explanation when there is no active channel to probe.</summary>
    /// <param name="ct">Token to cancel the probe.</param>
    /// <returns>The watch-probe diagnostic, or a note that nothing is being watched.</returns>
    public async Task<object> DebugWatchProbeAsync(CancellationToken ct = default)
    {
        if (ActiveChannel is not { } ch)
            return new { Error = "No active channel - the harvester isn't watching anything right now. Pin one with /harvest?id=<id> first." };
        return await _watch.ProbeAsync(ch, ct).ConfigureAwait(false);
    }

    /// <summary>Debug/testing: the current auth/session context with secrets redacted.</summary>
    /// <returns>An anonymous object describing the logged-in identity and token presence.</returns>
    public object DebugAuthState()
    {
        var s = _auth.State;
        return new
        {
            LoggedIn = _auth.IsLoggedIn,
            s.UserId,
            s.Username,
            s.DisplayName,
            s.ClientId,
            ClientIdIsAndroidApp = string.Equals(s.ClientId, TwitchConstants.AndroidAppClientId, StringComparison.Ordinal),
            DeviceIdPresent = !string.IsNullOrEmpty(s.DeviceId),
            DeviceIdLength = s.DeviceId?.Length ?? 0,
            AccessTokenPresent = !string.IsNullOrEmpty(s.AccessToken),
            AccessTokenLength = s.AccessToken?.Length ?? 0,
            s.ValidatedAtUtc,
        };
    }

    /// <summary>Release a manual override whose target campaign is finished (or no longer present), so the
    /// harvester resumes automatic selection instead of staying pinned to a done campaign. Without this, an
    /// override that runs to completion strands the loop: CandidateCampaigns() stays filtered to the
    /// finished campaign, PickTarget finds nothing, and it sits at "no drops" while other campaigns wait.</summary>
    void ReleaseOverrideIfFinished()
    {
        if (_forcedCampaignId is not { } fid)
            return;
        var forced = _campaigns.FirstOrDefault(c => string.Equals(c.Id, fid, StringComparison.OrdinalIgnoreCase));
        if (forced is not null && !IsCampaignFinishedForHarvesting(forced))
            return; // still has something to harvest - keep the override
        Log("Override target is finished - resuming automatic campaign selection.");
        ClearCampaignOverride();
    }

    /// <summary>Whether the harvester considers the campaign (by id) finished for harvesting.</summary>
    /// <param name="campaignId">Id of the campaign to test.</param>
    public bool IsCampaignFinished(string campaignId)
        => _campaigns.Any(c => string.Equals(c.Id, campaignId, StringComparison.OrdinalIgnoreCase)
                               && IsCampaignFinishedForHarvesting(c));

    /// <summary>Whether every reward is CLAIMED per Twitch's self OR our ledger (not just watched to 100%).</summary>
    /// <param name="campaignId">Id of the campaign to test.</param>
    /// <returns>True when all drops are claimed (self or ledger); false otherwise or if unknown.</returns>
    public bool AreAllRewardsClaimed(string campaignId)
    {
        var c = _campaigns.FirstOrDefault(x => string.Equals(x.Id, campaignId, StringComparison.OrdinalIgnoreCase));
        return c is not null && c.Drops.Count > 0
            // a sub-gated "buy drop" tier we can't earn doesn't hold the campaign out of Finished
            && c.Drops.All(d => d.IsClaimed || IsClaimedThisCampaign(d) || WeClaimedDrop(d) || !d.SubRequirementMet);
    }

    /// <summary>Whether this drop-definition id is in the harvester's per-tier claimed ledger.</summary>
    /// <param name="dropId">The drop-definition id to test.</param>
    /// <returns>True when we recorded this tier as claimed this session.</returns>
    public bool WasDropClaimed(string dropId) => _claimedDropIds.Contains(dropId);

    /// <summary>Whether the harvester has a drop in the campaign (by id) it would harvest right now.</summary>
    /// <param name="campaignId">Id of the campaign to test.</param>
    public bool IsCampaignHarvestable(string campaignId)
        => _campaigns.Any(c => string.Equals(c.Id, campaignId, StringComparison.OrdinalIgnoreCase)
                               && HarvestBlockReason(c) is null);

    /// <summary>Toggle a channel on/off the PREFERRED list, persist, and re-sync the row flags.</summary>
    /// <param name="channel">The channel to prefer or un-prefer.</param>
    public void TogglePreferChannel(TwitchChannel channel)
    {
        var login = channel.Login;
        var pref = Settings.PreferredChannels;
        if (pref.Contains(login, StringComparer.OrdinalIgnoreCase))
        {
            pref.RemoveAll(l => string.Equals(l, login, StringComparison.OrdinalIgnoreCase));
            Log($"{channel.DisplayName} removed from preferred channels.");
        }
        else
        {
            pref.Add(login);
            Settings.AvoidedChannels.RemoveAll(l => string.Equals(l, login, StringComparison.OrdinalIgnoreCase));
            Log($"Preferring {channel.DisplayName}: DropHarvester will idle here when they're live and no official channel is required - generic drops still credit.");
        }
        _settings.Save();
        SyncChannelPreferences();
        ChannelPreferencesChanged?.Invoke();
    }

    /// <summary>Toggle a channel on/off the AVOIDED list, persist, and re-sync the row flags.</summary>
    /// <param name="channel">The channel to avoid or un-avoid.</param>
    public void ToggleAvoidChannel(TwitchChannel channel)
    {
        var login = channel.Login;
        var avoid = Settings.AvoidedChannels;
        if (avoid.Contains(login, StringComparer.OrdinalIgnoreCase))
        {
            avoid.RemoveAll(l => string.Equals(l, login, StringComparison.OrdinalIgnoreCase));
            Log($"{channel.DisplayName} removed from avoided channels.");
        }
        else
        {
            avoid.Add(login);
            Settings.PreferredChannels.RemoveAll(l => string.Equals(l, login, StringComparison.OrdinalIgnoreCase));
            Log($"Avoiding {channel.DisplayName}: it won't be idled on unless it's the only stream live for the game.");
        }
        _settings.Save();
        SyncChannelPreferences();
        ChannelPreferencesChanged?.Invoke();
    }

    /// <summary>Re-apply the preferred/avoided flags to the tracked channel rows from settings.</summary>
    public void SyncChannelPreferences()
    {
        var pref = Settings.PreferredChannels;
        var avoid = Settings.AvoidedChannels;
        UiDispatch.Current.Post(() =>
        {
            foreach (var ch in TrackedChannels)
            {
                ch.IsPreferred = pref.Contains(ch.Login, StringComparer.OrdinalIgnoreCase);
                ch.IsAvoided = avoid.Contains(ch.Login, StringComparer.OrdinalIgnoreCase);
            }
        });
    }

    // signaled to break the watch-interval delay early when a switch/refresh is requested, so a
    // "Watch" click takes effect within a second or two instead of up to a full watch tick
    readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>Signal any wakeable delay (watch tick OR an idle wait) to return early, so a settings change
    /// or manual action takes effect now instead of after the delay (idempotent; never over-releases).</summary>
    void Wake()
    {
        try { if (_wake.CurrentCount == 0) _wake.Release(); } catch { }
    }

    /// <summary>Wait out a delay, but return early if <see cref="Wake"/> is signaled. Used for BOTH the watch
    /// tick and the main loop's idle waits, so a priority/exclude/unlinked edit (via <see cref="RequestRefresh"/>)
    /// or a manual switch is picked up immediately rather than waiting out a multi-minute idle sleep.</summary>
    /// <param name="delay">Maximum time to wait.</param>
    /// <param name="ct">Cancels the wait on shutdown.</param>
    async Task WakeableDelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await _wake.WaitAsync(delay, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    /// <summary>The main harvesting loop: keep auth and campaigns fresh, pick a watchable target, and harvest
    /// it, retrying after a short backoff on any transient error until cancelled.</summary>
    /// <param name="ct">Cancels the loop on shutdown / login expiry.</param>
    async Task RunAsync(CancellationToken ct)
    {
        try
        {
            // keep the global drop-events topic live for the whole session
            await ResubscribeAsync(ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!await EnsureAuthAsync(ct).ConfigureAwait(false))
                        return;

                    // A settings edit (priority / exclude / harvest-unlinked) requested a refresh while the
                    // loop was idle: force a fresh discovery so the new lists take effect now. This matters
                    // most for "harvest unlinked", which changes WHICH campaigns get fetched at all. (Mid-
                    // harvest the same flag is handled in HarvestChannelAsync, which breaks its watch loop.)
                    if (_refreshRequested)
                    {
                        _refreshRequested = false;
                        _lastCampaignFetch = DateTimeOffset.MinValue;
                    }

                    await EnsureCampaignsAsync(ct).ConfigureAwait(false);
                    await SweepClaimsAsync(ct).ConfigureAwait(false);
                    ReleaseOverrideIfFinished();

                    if (PickTarget() is null)
                    {
                        SetActive(null, null, null, Loc.T("Status_NoDropsToHarvest"));
                        _bus.Publish(new AllDropsHarvestedEvent());
                        await WakeableDelayAsync(TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
                        continue;
                    }

                    // pick the first priority-ordered game with a live stream, so an all-offline game
                    // doesn't stall the loop
                    var picked = await PickWatchableTargetAsync(ct).ConfigureAwait(false);
                    if (picked is null)
                    {
                        SetActive(null, null, null, Loc.T("Status_WaitingForStream"));
                        QueueChannelRefresh(null, ct); // still show the harvestable games (offline) while waiting
                        await WakeableDelayAsync(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
                        continue;
                    }

                    var (campaign, channel, drop) = picked.Value;
                    SetActive(channel, campaign, drop, Loc.T("Status_WatchingChannelGame", channel.DisplayName, campaign.Game.Name));
                    QueueChannelRefresh(channel, ct, force: true); // fresh pick -> refresh the tab now (background)
                    await ResubscribeAsync(ct).ConfigureAwait(false);
                    await HarvestChannelAsync(campaign, channel, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; } // shutdown / login-expired unwind
                catch (Exception ex)
                {
                    // A single bad tick (transient API/parse/timing hiccup) must NOT kill harvesting -
                    // log it and retry after a short backoff instead of exiting the loop.
                    ReportConnection(false); // a run of these = Twitch down / connection lost
                    _bus.Publish(new HarvesterErrorEvent(ex.Message));
                    Log($"Connection to Twitch failed: {ex.Message} - retrying in 15s.", HarvesterLogLevel.Warn);
                    await WakeableDelayAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            _bus.Publish(new HarvesterErrorEvent(ex.Message));
            Log($"Harvesting loop error: {ex.Message}", HarvesterLogLevel.Error);
        }
    }

    /// <summary>Watch a single channel until its active drop completes, it goes offline, or a
    /// higher-priority target appears.</summary>
    /// <param name="campaign">The campaign being harvested on this channel.</param>
    /// <param name="channel">The channel to watch.</param>
    /// <param name="ct">Cancels the watch on shutdown.</param>
    async Task HarvestChannelAsync(DropsCampaign campaign, TwitchChannel channel, CancellationToken ct)
    {
        // channel-specific campaigns are watched FIRST for time efficiency: their drop only credits on
        // this official channel, while generic/cumulative drops keep accruing on any channel meanwhile
        if (campaign.AllowedChannels.Count > 0)
            Log($"Watching official channel {channel.DisplayName} [{campaign.Game.Name}] first for time efficiency: its channel-specific drop only credits here, while generic drops keep progressing on any channel.");
        else
            Log($"Watching: {channel.DisplayName} [{campaign.Game.Name}]");
        _lastPreemptCheck = DateTimeOffset.UtcNow; // don't re-evaluate priority for a few minutes

        // stream-less watch: send the minute-watched heartbeat on the active transport, then read the
        // real progress back from the inventory afterwards
        await SendWatchAsync(channel, ct).ConfigureAwait(false);
        await SyncInventorySafeAsync(ct).ConfigureAwait(false);
        var initial = FirstHarvestableDrop(campaign);
        if (initial is not null)
            _bus.Publish(new DropProgressEvent(initial));

        _lastDropMinutes = initial?.CurrentMinutes ?? 0;
        ResetStall();

        while (!ct.IsCancellationRequested)
        {
            if (_claimNowRequested)
            {
                _claimNowRequested = false;
                await SyncInventorySafeAsync(ct).ConfigureAwait(false);
                await ClaimAllReadyAsync(ct).ConfigureAwait(false);
            }
            if (_refreshRequested)
            {
                _refreshRequested = false;
                _lastCampaignFetch = DateTimeOffset.MinValue;
                return;
            }
            if (_switchRequested)
            {
                _switchRequested = false;
                return; // re-pick target; ChooseChannelAsync will honor the forced channel
            }

            await _channels.RefreshChannelAsync(channel, ct).ConfigureAwait(false);
            if (!channel.Online)
            {
                _bus.Publish(new ChannelSwitchedEvent(null, Loc.T("Status_ChannelWentOffline", channel.DisplayName)));
                Log($"{channel.DisplayName} goes OFFLINE, switching...");
                return; // the next channel logs its own "Watching: X [Game]"
            }

            var drop = FirstHarvestableDrop(campaign);
            if (drop is null)
            {
                // the last drop here just finished: claim it (and anything else ready) BEFORE moving on -
                // a completed drop must never be left behind because we walked away to the next game
                await SyncInventorySafeAsync(ct).ConfigureAwait(false);
                await ClaimAllReadyAsync(ct).ConfigureAwait(false);
                // channel-specific drop done: rotate to the next official channel now rather than parking
                // here - moving on collects the other channels' drops sooner (generics already accrued)
                if (campaign.AllowedChannels.Count > 0)
                    Log($"Done with {channel.DisplayName}'s channel-specific drop - moving to the next official channel to save time (staying here would just repeat those hours later).");
                _bus.Publish(new CampaignCompletedEvent(campaign));
                return;
            }
            if (!ReferenceEquals(drop, ActiveDrop))
            {
                SetActive(channel, campaign, drop, Loc.T("Status_WatchingChannelGame", channel.DisplayName, campaign.Game.Name));
                _lastDropMinutes = drop.CurrentMinutes; // new drop: reset the stall baseline
                ResetStall();
            }

            // send the minute-watched heartbeat (this advances the drop), then read progress back
            await SendWatchAsync(channel, ct).ConfigureAwait(false);
            await SyncInventorySafeAsync(ct).ConfigureAwait(false);

            // Report which drop Twitch says this account is progressing on THIS channel right now
            // (dropCurrentSession). This is the ground-truth "is Twitch's crediting pipeline up" signal - a
            // live allow-listed stream not airing drop-eligible content reports nothing.
            if (!string.IsNullOrEmpty(channel.Id))
            {
                var (sessionDropId, sessionMins) = await _inventory.FetchCurrentSessionAsync(channel.Id!, ct).ConfigureAwait(false);
                // If Twitch's own session for this channel is ADVANCING (same drop, more minutes), its
                // crediting pipeline is working RIGHT NOW - even if that drop isn't OUR target (we're parked
                // on an event campaign whose specific tier doesn't credit here). Refresh the outage clock so
                // that can't be misread as a Twitch-wide outage; the stall clock still handles our target.
                if (!string.IsNullOrEmpty(sessionDropId)
                    && sessionDropId == _lastSessionDropId && sessionMins > _lastSessionMins)
                    _lastTwitchCreditUtc = DateTimeOffset.UtcNow;
                _lastSessionDropId = sessionDropId;
                _lastSessionMins = sessionMins;
                Log(string.IsNullOrEmpty(sessionDropId)
                    ? $"Credit check on {channel.DisplayName} [{campaign.Game.Name}]: Twitch reports NO drop crediting (target {drop.Id})."
                    : $"Credit check on {channel.DisplayName} [{campaign.Game.Name}]: crediting drop {sessionDropId} at {sessionMins}m (target {drop.Id}).",
                    HarvesterLogLevel.Debug);
            }

            _bus.Publish(new DropProgressEvent(drop));
            // CheckStall first: it updates the credit clock (and OnConfirmedProgress) when minutes advance,
            // so the transport self-heal below reads a fresh "how long since a real credit" signal.
            var stallAction = CheckStall(drop);
            EvaluateWatchHealth(channel, campaign);
            switch (stallAction)
            {
                case StallAction.GiveUp:
                    _skipDrops[drop.Id] = DateTimeOffset.UtcNow.AddMinutes(SkipRetryMinutes);
                    // A tier stalled at ZERO real watch-minutes for the whole give-up window whose reward was
                    // already awarded within THIS campaign's window is almost certainly claimed with self
                    // permanently lagging at 0 (the Marbles shared-reward case the uniqueness guard can't
                    // resolve). Persist it claimed so it isn't re-picked. The RealCurrentMinutes==0 guard is
                    // essential: a tier that HAS real progress (a Marbles 8h/10h coin tier at 361/480) shares
                    // that reward too, but it's genuinely still being earned - marking it claimed off a
                    // sibling tier's claim would strand it forever.
                    if (drop.RealCurrentMinutes == 0 && drop.Benefits.Any(b => ClaimBelongsTo(b.MatchKey, campaign)))
                    {
                        RecordDropClaimed(drop, DateTimeOffset.UtcNow);
                        Log($"'{drop.RewardName}' [{campaign.Game.Name}] never progressed and its reward is already in your history for this campaign - marking it claimed so it isn't retried.", HarvesterLogLevel.Warn);
                    }
                    else
                        // giving up isn't progress - keep the global "no progress" banner until something advances
                        Log($"No progress on '{drop.RewardName}' [{campaign.Game.Name}] after {GiveUpAfterMinutes} min across channels - skipping this drop for now.", HarvesterLogLevel.Warn);
                    return; // re-pick a target; this drop is now excluded this session
                case StallAction.SwitchChannel:
                    // channel-specific drops can ONLY credit on this channel, so switching away is pointless
                    // (and a mid-stream inventory lag can look like a stall while it's really crediting).
                    // The fast-switch only helps OPEN campaigns; the cross-channel give-up still applies here.
                    if (campaign.AllowedChannels.Count == 0)
                    {
                        _channelCooldownUntil[channel.Login] = DateTimeOffset.UtcNow.AddMinutes(ChannelCooldownMinutes);
                        // switching channels isn't progress: keep the stall banner
                        Log($"No progress on '{drop.RewardName}' [{campaign.Game.Name}] via {channel.DisplayName} for {StallSwitchMinutes} min - it isn't crediting us, trying another live channel (or waiting if none are online).");
                        return;
                    }
                    break; // official channel: keep watching (only place this drop can credit)
            }

            // claim across ALL campaigns each tick, not just the one being watched, so a drop that
            // completed elsewhere (or whose claim instance only just became available) is never left behind
            await ClaimAllReadyAsync(ct).ConfigureAwait(false);

            if (Settings.AutoClaimChannelPoints)
            {
                try
                {
                    var earned = await _points.TryClaimAsync(channel.Login, ct).ConfigureAwait(false);
                    if (earned is > 0)
                    {
                        _bus.Publish(new PointsClaimedEvent(channel.DisplayName, earned.Value));
                        Log($"Claimed {earned} channel points on {channel.DisplayName}.");
                    }
                }
                catch (GqlAuthException) { throw; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log($"Channel-points claim failed on {channel.DisplayName}: {ex.Message}", HarvesterLogLevel.Warn);
                }
            }

            // every few minutes, check if a higher-priority game (e.g. one skipped earlier while offline)
            // now has a live stream, and switch back to it if so
            if (DateTimeOffset.UtcNow - _lastPreemptCheck >= TimeSpan.FromMinutes(5))
            {
                _lastPreemptCheck = DateTimeOffset.UtcNow;
                QueueChannelRefresh(channel, ct); // keep the Channels tab fresh (background)

                // Discovery is stale: return to the main loop so it re-fetches campaigns and re-picks. This
                // is how a campaign that started AFTER we settled on the current one (a new higher-priority
                // drop) gets picked up without waiting for this campaign to finish or a manual pause/resume.
                // (A plain override stays pinned - only re-discover when not force-harvesting one campaign.)
                if (_forcedCampaignId is null
                    && DateTimeOffset.UtcNow - _lastCampaignFetch >= TimeSpan.FromMinutes(RediscoverMinutes))
                    return;

                if (_forcedCampaignId is not null)
                {
                    // harvesting a fallback because the override target was offline? snap back the moment its
                    // stream is live again
                    if (!string.Equals(campaign.Id, _forcedCampaignId, StringComparison.OrdinalIgnoreCase)
                        && await ForcedTargetWatchableAsync(ct).ConfigureAwait(false))
                    {
                        _switchRequested = true;
                        return;
                    }
                    if (Settings.OverrideYieldsToPriority && await TryEndOverrideForNewHigherPriorityAsync(campaign, ct).ConfigureAwait(false))
                        return;
                }
                else
                {
                    foreach (var c in OrderedCampaigns())
                    {
                        if (ReferenceEquals(c, campaign))
                            break; // reached the current campaign; nothing higher-priority is left
                        // Switch only if this higher-priority campaign has a channel we can ACTUALLY watch
                        // right now (an allow-listed one for a restricted campaign), using the same picker
                        // the switch will use - so we never abandon the current watch for a game whose
                        // campaign has nothing eligible live.
                        if (await ChooseChannelAsync(c, ct).ConfigureAwait(false) is not null)
                        {
                            Log($"Higher-priority game available - switching from {campaign.Game.Name} to {c.Game.Name}.");
                            return;
                        }
                    }
                }
            }

            // the drop we were watching just got claimed/finished this tick -> advance to the next one NOW
            // instead of idling a full watch interval on a completed drop: re-send the watch immediately
            // after a claim so the next drop starts advancing right away
            if (!ReferenceEquals(FirstHarvestableDrop(campaign), drop))
                continue;

            await WakeableDelayAsync(TwitchConstants.WatchInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Track whether the active drop is advancing and decide what to do. One watch tick is
    /// ~1 minute, so the counters are effectively in minutes. Returns SwitchChannel once the current
    /// channel has gone a few minutes without crediting us (it isn't delivering progress - move on),
    /// or GiveUp once the drop has stalled across channels for the full window (it won't credit
    /// anywhere - precondition/eligibility/region).</summary>
    /// <param name="drop">The drop currently being harvested.</param>
    StallAction CheckStall(TimedDrop drop)
    {
        var mins = drop.CurrentMinutes;

        // new drop -> fresh cross-channel stall tally
        if (drop.Id != _stallDropId)
        {
            _stallDropId = drop.Id;
            _dropStallMinutes = 0;
            _stallMinutes = 0;
            _lastDropMinutes = mins;
        }

        if (mins > _lastDropMinutes)
        {
            _lastDropMinutes = mins;
            _stallMinutes = 0;
            _dropStallMinutes = 0;
            OnConfirmedProgress(); // a real credit landed: reset the clock, keep the transport, clear the banner
            return StallAction.None;
        }

        _stallMinutes++;
        _dropStallMinutes++;
        // give up on the drop entirely (across channels) once it's clearly un-earnable right now
        if (_dropStallMinutes >= GiveUpAfterMinutes)
            return StallAction.GiveUp;
        // this channel isn't crediting us - try another (or wait)
        if (_stallMinutes >= StallSwitchMinutes)
            return StallAction.SwitchChannel;
        return StallAction.None;
    }

    /// <summary>Reset the per-channel stall counter (called when a new channel starts).</summary>
    void ResetStall()
    {
        // cross-drop tally + the global no-progress timer/banner are LEFT ALONE so they survive a channel
        // switch - switching channels isn't progress
        _stallMinutes = 0;
    }

    /// <summary>Watch-transport self-heal, run once per watch tick while online. If progress has stalled
    /// long enough, walk the backup transports one at a time (a single bounded pass), giving each a couple
    /// of minutes to restore progress; if the whole pass fails, settle back on the known-good primary and
    /// raise the outage banner. A real credit (via <see cref="OnConfirmedProgress"/>) cancels all of this.</summary>
    /// <param name="channel">The channel currently being watched.</param>
    /// <param name="campaign">The campaign being harvested.</param>
    void EvaluateWatchHealth(TwitchChannel channel, DropsCampaign campaign)
    {
        if (!channel.Online)
            return;

        // The outage banner means "your NORMAL (linked) drops aren't crediting - Twitch looks down". An
        // UNLINKED opt-in ("harvest unlinked") campaign not crediting is NOT that: these event/participation
        // drops are an unreliable gamble that often don't credit on an allow-listed channel not airing the
        // live event, and Twitch reports no session for them (nothing to refresh the outage clock). A real
        // outage still surfaces on the linked campaigns the harvester prioritizes. So while on an unlinked
        // campaign, keep the outage clock fresh and clear a false banner - but DON'T touch the stall clock,
        // so the give-up path still benches the un-crediting drop and moves us on.
        if (!campaign.Linked)
        {
            _lastTwitchCreditUtc = DateTimeOffset.UtcNow;
            _rotating = false;
            _rotationStepMinutes = 0;
            _selfHealExhausted = false;
            ClearOutage();
            return;
        }

        // Outage/rotation keys off "how long since Twitch credited ANYTHING" (our target advancing, OR the
        // credit-check session advancing on a different drop), NOT just our target - so a linked campaign
        // where Twitch is crediting some other tier can't fake a Twitch-wide outage. The stall clock
        // (_lastCreditUtc) still benches our un-advancing target and moves us on.
        var stalledMin = (DateTimeOffset.UtcNow - _lastTwitchCreditUtc).TotalMinutes;

        if (!_rotating)
        {
            if (_selfHealExhausted || _outageActive)
                return; // already tried everything - wait for a real credit to reset us
            if (_watch.HasBackupTransports)
            {
                if (stalledMin >= NoProgressSwitchMinutes && _watch.RotateToNextTransport())
                {
                    _rotating = true;
                    _rotationStepMinutes = 0;
                    Log($"No confirmed drop progress for {NoProgressSwitchMinutes}m - trying backup watch method '{_watch.CurrentTransport}'.", HarvesterLogLevel.Debug);
                }
            }
            else if (stalledMin >= OutageAfterMinutes)
            {
                _selfHealExhausted = true;
                RaiseOutage();
            }
            return;
        }

        // On a backup transport: give it a couple of minutes to prove it credits before moving on.
        _rotationStepMinutes++;
        if (_rotationStepMinutes < RotationStepMinutes)
            return;
        _rotationStepMinutes = 0;
        if (_watch.RotateToNextTransport())
        {
            Log($"Backup watch method didn't restore progress - trying next '{_watch.CurrentTransport}'.", HarvesterLogLevel.Debug);
        }
        else
        {
            _watch.SettleToPrimary();
            _rotating = false;
            _selfHealExhausted = true;
            Log($"No watch method restored progress - back on '{_watch.CurrentTransport}'. Twitch drop crediting looks down or the endpoint is blocked here.", HarvesterLogLevel.Warn);
            RaiseOutage();
        }
    }

    /// <summary>Raise the "Twitch drops outage" banner (kept until a real credit clears it). The harvester
    /// keeps running throughout so it resumes the moment Twitch restores crediting.</summary>
    void RaiseOutage()
    {
        if (_outageActive)
            return;
        _outageActive = true;
        _bus.Publish(new DropsOutageEvent(true));
        Log("Nothing has credited across channels or watch methods though watches are being accepted - Twitch drop crediting looks down. Still running; it will resume automatically when Twitch restores it.", HarvesterLogLevel.Warn);
    }

    /// <summary>A real credit landed: adopt the active transport as known-good, cancel any in-flight
    /// self-heal pass, and clear the outage banner. Called from the stall check when minutes advance.</summary>
    void OnConfirmedProgress()
    {
        _lastCreditUtc = DateTimeOffset.UtcNow;
        _lastTwitchCreditUtc = DateTimeOffset.UtcNow; // our target advancing is also proof Twitch is crediting
        _watch.MarkCurrentGood();
        if (_rotating)
            Log($"Backup watch method '{_watch.CurrentTransport}' restored drop progress - staying on it.", HarvesterLogLevel.Debug);
        _rotating = false;
        _rotationStepMinutes = 0;
        _selfHealExhausted = false;
        ClearOutage();
    }

    /// <summary>Clear the drops-outage banner (called when a real credit lands or harvesting stops).</summary>
    void ClearOutage()
    {
        if (!_outageActive)
            return;
        _outageActive = false;
        _bus.Publish(new DropsOutageEvent(false));
        Log("Drop crediting resumed.");
    }

    /// <summary>Whether a channel (by login) is currently benched from selection.</summary>
    /// <param name="login">The channel login to test.</param>
    bool OnCooldown(string login)
        => _channelCooldownUntil.TryGetValue(login, out var until) && DateTimeOffset.UtcNow < until;

    /// <summary>Publish a NewDropAvailableEvent for drops not seen before. The first fetch seeds the
    /// set silently (no flood); later fetches announce genuinely new drops.</summary>
    /// <param name="campaigns">The freshly fetched campaigns to scan for new drops.</param>
    void AnnounceNewDrops(IReadOnlyList<DropsCampaign> campaigns)
    {
        foreach (var c in campaigns.Where(c => c.IsActive && !c.IsFinished))
            foreach (var d in c.Drops.Where(d => !d.IsClaimed && !d.IsComplete))
                if (_announcedDropIds.Add(d.Id) && _announceSeeded)
                    _bus.Publish(new NewDropAvailableEvent(c, d));
        _announceSeeded = true;
    }

    /// <summary>Send one minute-watched heartbeat; surfaces auth expiry, swallows transient errors.</summary>
    /// <param name="channel">The channel to send the heartbeat for.</param>
    /// <param name="ct">Cancels the request on shutdown.</param>
    async Task SendWatchAsync(TwitchChannel channel, CancellationToken ct)
    {
        try
        {
            var ok = await _watch.SendWatchAsync(channel, ct).ConfigureAwait(false);
            if (ok)
                ReportConnection(true);
            else
            {
                // A single un-acked beacon heartbeat is a routine transient blip that the next tick retries;
                // keep it out of the user-facing log (Debug). Sustained failure surfaces via ReportConnection.
                Log($"Watch heartbeat not acknowledged - retrying in {TwitchConstants.WatchInterval.TotalSeconds:0}s.", HarvesterLogLevel.Debug);
                ReportConnection(false);
            }
        }
        catch (GqlAuthException)
        {
            _bus.Publish(new LoginExpiredEvent());
            Log("Login expired - please log in again.", HarvesterLogLevel.Warn);
            IsRunning = false;
            throw new OperationCanceledException(); // unwind the loop cleanly
        }
        catch
        {
            Log($"Watch heartbeat failed (transient) - retrying in {TwitchConstants.WatchInterval.TotalSeconds:0}s.", HarvesterLogLevel.Debug);
            ReportConnection(false); // next watch tick retries
        }
    }

    DateTimeOffset? _connFailStartUtc;
    bool _connIssue;
    static readonly TimeSpan ConnIssueAfter = TimeSpan.FromMinutes(20); // sustained failure before we call it "down"

    /// <summary>Track heartbeat / fetch success. After ~20 min of SUSTAINED failure we surface a
    /// "connection issues, will auto-resume" state; the first success clears it. Twitch Drops go down
    /// fairly often, so we ride out brief blips and only tell the user when it's clearly a real outage.</summary>
    /// <param name="ok">True if the last heartbeat/fetch succeeded.</param>
    void ReportConnection(bool ok)
    {
        if (ok)
        {
            _connFailStartUtc = null;
            if (_connIssue)
            {
                _connIssue = false;
                _bus.Publish(new ConnectionIssueEvent(false));
                Log(Loc.T("Log_ConnectionReestablished"));
            }
            return;
        }
        _connFailStartUtc ??= DateTimeOffset.UtcNow;
        if (!_connIssue && DateTimeOffset.UtcNow - _connFailStartUtc >= ConnIssueAfter)
        {
            _connIssue = true;
            _bus.Publish(new ConnectionIssueEvent(true));
            Log(Loc.T("Log_ConnectionLost"), HarvesterLogLevel.Warn);
        }
    }

    /// <summary>Sync inventory progress, tolerating a transient hiccup (log + continue) so one bad
    /// read doesn't drop the channel we're on. Auth expiry and shutdown still propagate.</summary>
    /// <param name="ct">Cancels the sync on shutdown.</param>
    async Task SyncInventorySafeAsync(CancellationToken ct)
    {
        try
        {
            await _inventory.SyncInventoryAsync(_campaigns, ct).ConfigureAwait(false);
            CaptureSelfClaimedDrops();  // lock in any tier self currently reports claimed, before it lags
            PurgeStaleLedgerClaims();   // ...and undo any tier wrongly marked claimed that's still in progress
        }
        catch (GqlAuthException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log($"Inventory sync hiccup: {ex.Message} - continuing.", HarvesterLogLevel.Warn); }
    }

    /// <summary>Whenever Twitch's per-drop <c>self.isClaimed</c> is TRUE it's reliable - but it lags back
    /// to false for older claims. So every sync we fold any currently-claimed tier into the permanent
    /// per-drop ledger; once captured it stays "done" even after self resets. This is how a shared-reward
    /// campaign (Marbles) gets marked finished tier-by-tier from Twitch's own data, not a guess.</summary>
    void CaptureSelfClaimedDrops()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var c in _campaigns)
            foreach (var d in c.Drops)
                if (d.IsClaimed && !_claimedDropIds.Contains(d.Id))
                    RecordDropClaimed(d, now);
    }

    /// <summary>Undo a per-tier ledger "claimed" mark when Twitch shows that tier genuinely still IN PROGRESS
    /// (real watch-minutes above 0 but below the requirement, and not claimed). A claimed tier never reads as
    /// mid-progress, so this can only be a stale shared-reward mis-attribution (a Marbles coin tier marked
    /// claimed off a sibling tier's claim, or a stall during a crediting outage) - clear it from the ledger so
    /// the tier is harvested to completion instead of being stranded as "already earned".</summary>
    void PurgeStaleLedgerClaims()
    {
        foreach (var c in _campaigns)
            foreach (var d in c.Drops)
                if (_claimedDropIds.Contains(d.Id)
                    && !d.IsClaimed
                    && d.RealCurrentMinutes > 0
                    && d.RealCurrentMinutes < d.RequiredMinutes)
                {
                    _claimedDropIds.Remove(d.Id);
                    _ledger.RemoveDrop(d.Id);
                    Log($"'{d.RewardName}' [{c.Game.Name}] is still in progress on Twitch ({d.RealCurrentMinutes}/{d.RequiredMinutes} min) - clearing a stale 'claimed' mark so it resumes.", HarvesterLogLevel.Warn);
                }
    }

    /// <summary>Claim every drop in the campaign that's ready, recording each claim in the ledger and
    /// releasing a "drop only" override once one lands.</summary>
    /// <param name="campaign">The campaign whose ready drops to claim.</param>
    /// <param name="ct">Cancels the claims on shutdown.</param>
    async Task ClaimReadyDropsAsync(DropsCampaign campaign, CancellationToken ct)
    {
        var claimedAny = false;
        foreach (var drop in campaign.Drops)
        {
            if (!drop.CanClaim)
                continue;
            try
            {
                // ClaimDropAsync already retries transient failures inside the GQL client, so a failure
                // here is genuine: log it and leave the drop claimable for the next tick
                var claimed = await _inventory.ClaimDropAsync(drop, ct).ConfigureAwait(false);
                if (claimed)
                {
                    claimedAny = true;
                    var now = DateTimeOffset.UtcNow;
                    foreach (var b in drop.Benefits)
                        _ledger.Record(b.MatchKey, now); // remember OUR claim forever, immune to Twitch lag
                    RecordDropClaimed(drop, now);        // per-TIER record: distinguishes shared-reward tiers
                    _bus.Publish(new DropClaimedEvent(drop, campaign));
                    // "drop only" override: one drop of the pinned campaign claimed -> release, back to auto
                    if (_forcedDropOnly && string.Equals(campaign.Id, _forcedCampaignId, StringComparison.OrdinalIgnoreCase))
                        ClearCampaignOverride();
                }
            }
            catch (GqlAuthException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log($"Couldn't claim '{drop.RewardName}' [{campaign.Game.Name}]: {ex.Message} - will retry.", HarvesterLogLevel.Warn);
            }
        }
        if (campaign.IsFinished)
            _bus.Publish(new CampaignCompletedEvent(campaign));
        if (claimedAny)
            // refresh inventory/claim-history on the next target pick: a claim can unlock the next drop
            // and updates what's already owned
            _lastCampaignFetch = DateTimeOffset.MinValue;
    }

    /// <summary>Claim any ready drop across every campaign - used when a drop-claim pubsub message arrives,
    /// since the claimable drop may not be in the campaign currently being watched.</summary>
    /// <param name="ct">Cancels the claims on shutdown.</param>
    async Task ClaimAllReadyAsync(CancellationToken ct)
    {
        foreach (var campaign in _campaigns.ToList())
            if (campaign.Drops.Any(d => d.CanClaim))
                await ClaimReadyDropsAsync(campaign, ct).ConfigureAwait(false);
    }

    /// <summary>The claim safety net: re-sync inventory (to surface any earned-but-unclaimed instance) and
    /// claim every ready drop across all campaigns, throttled to once a minute. Runs on the harvesting loop at
    /// startup and on every re-pick/idle cycle, so a completed drop is claimed even if it wasn't being
    /// watched, the claim pubsub was missed (websocket drop), or the app was restarted after a crash - and
    /// a claim that fails stays claimable, so it's retried on the next sweep until it lands.</summary>
    /// <param name="ct">Cancels the sweep on shutdown.</param>
    async Task SweepClaimsAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - _lastClaimSweep < TimeSpan.FromMinutes(1))
            return;
        _lastClaimSweep = DateTimeOffset.UtcNow;
        await SyncInventorySafeAsync(ct).ConfigureAwait(false);
        await ClaimAllReadyAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The first campaign (in harvesting order) that has a drop we can harvest right now, or null.</summary>
    (DropsCampaign campaign, TimedDrop drop)? PickTarget()
    {
        foreach (var campaign in OrderedCampaigns())
        {
            var drop = FirstHarvestableDrop(campaign);
            if (drop is not null)
                return (campaign, drop);
        }
        return null;
    }

    /// <summary>Walk the candidate campaigns (soonest-ending or priority order) and return the first
    /// one we can actually harvest right now: eligible, with an unharvested drop, and a live drops-enabled
    /// stream. Every campaign we pass over is logged with the reason (not linked, reward already owned,
    /// not enough time left, no streams online) so it's clear why a sooner-ending game was skipped.</summary>
    /// <param name="ct">Cancels the lookups on shutdown.</param>
    async Task<(DropsCampaign campaign, TwitchChannel channel, TimedDrop drop)?> PickWatchableTargetAsync(CancellationToken ct)
    {
        // a manual "Watch" click wins outright: harvest the campaign for THAT channel's GAME on THAT channel,
        // bypassing the normal order (else clicking a Division 2 channel could harvest an R6S channel instead)
        if (_forcedChannel is { } forced)
        {
            _forcedChannel = null;
            var forcedPick = await TryForcedChannelAsync(forced, ct).ConfigureAwait(false);
            if (forcedPick is not null)
                return forcedPick;
            Log($"Can't watch {forced.DisplayName} right now (offline, or nothing left to harvest for {forced.Game?.Name}) - continuing with the normal order.");
        }

        var skips = new List<(string game, string reason)>(); // games passed over, in order
        var pick = await WalkForWatchableAsync(CandidateCampaigns(), skips, ct).ConfigureAwait(false);

        // the override target has no live stream right now: rather than sit idle, harvest the best OTHER
        // harvestable campaign meanwhile. The override stays active and resumes automatically the moment its
        // stream is back (checked on the periodic preempt pass), so no watch-time is wasted idling.
        if (pick is null && _forcedCampaignId is not null)
        {
            var fallback = await WalkForWatchableAsync(
                CandidateCampaignsRaw().Where(c => !string.Equals(c.Id, _forcedCampaignId, StringComparison.OrdinalIgnoreCase)),
                skips, ct).ConfigureAwait(false);
            if (fallback is { } fb)
            {
                Log($"Override target has no live stream right now - harvesting {fb.campaign.Game.Name} meanwhile; resuming the override once it's back online.");
                ClearSkip(fb.campaign.Game.Name);
                return fb;
            }
        }

        if (pick is { } p)
        {
            LogSkips(skips, exceptGame: p.campaign.Game.Name);
            ClearSkip(p.campaign.Game.Name);
            return p;
        }

        LogSkips(skips, exceptGame: null);
        return null;
    }

    /// <summary>Walk the given candidate campaigns in order and return the first one watchable right now
    /// (eligible, with an unharvested drop, and a live drops-enabled stream), appending a (game, reason) entry
    /// to <paramref name="skips"/> for each one passed over. Returns null if none is watchable.</summary>
    /// <param name="candidates">Candidate campaigns to walk, already in harvesting order.</param>
    /// <param name="skips">Accumulates the games passed over and why, for the caller to log.</param>
    /// <param name="ct">Cancels the lookups on shutdown.</param>
    async Task<(DropsCampaign campaign, TwitchChannel channel, TimedDrop drop)?> WalkForWatchableAsync(
        IEnumerable<DropsCampaign> candidates, List<(string game, string reason)> skips, CancellationToken ct)
    {
        foreach (var campaign in candidates)
        {
            var game = campaign.Game.Name;

            var reason = HarvestBlockReason(campaign);
            if (reason is not null)
            {
                // a finished campaign (all drops claimed/earned) isn't a "skip" worth announcing - it
                // belongs in Finished and is silently ignored
                if (!IsCampaignFinishedForHarvesting(campaign))
                    skips.Add((game, reason));
                continue;
            }

            var drop = FirstHarvestableDrop(campaign);
            if (drop is null) // reason==null already implies a harvestable drop; guard anyway
                continue;

            var channel = await ChooseChannelAsync(campaign, ct).ConfigureAwait(false);
            if (channel is null)
            {
                skips.Add((game, campaign.AllowedChannels.Count > 0
                    ? "its official channel(s) are offline"
                    : "no streams online"));
                continue;
            }

            return (campaign, channel, drop);
        }
        return null;
    }

    /// <summary>Honor a manual "Watch" pick: watch <paramref name="forced"/> on the harvestable campaign
    /// whose GAME matches it. Returns null if the channel is offline or its game has nothing to harvest.</summary>
    /// <param name="forced">The manually picked channel.</param>
    /// <param name="ct">Cancels the lookups on shutdown.</param>
    async Task<(DropsCampaign campaign, TwitchChannel channel, TimedDrop drop)?> TryForcedChannelAsync(TwitchChannel forced, CancellationToken ct)
    {
        _channelCooldownUntil.Remove(forced.Login); // a manual pick overrides any stall bench
        await _channels.RefreshChannelAsync(forced, ct).ConfigureAwait(false);
        if (!forced.Online || string.IsNullOrEmpty(forced.Id))
            return null;

        var gameId = forced.Game?.Id;
        if (string.IsNullOrEmpty(gameId))
            return null;

        foreach (var c in CandidateCampaigns())
        {
            if (!string.Equals(c.Game.Id, gameId, StringComparison.OrdinalIgnoreCase))
                continue;
            if (HarvestBlockReason(c) is not null)
                continue;
            var drop = FirstHarvestableDrop(c);
            if (drop is not null)
                return (c, forced, drop);
        }
        return null;
    }

    /// <summary>The campaign has nothing left to harvest because every drop is claimed/complete (per
    /// Twitch's per-drop state, or claimed this campaign per the claim history) - it belongs in
    /// "Finished", not a skip to announce.</summary>
    /// <param name="c">The campaign to test.</param>
    bool IsCampaignFinishedForHarvesting(DropsCampaign c)
        => c.Drops.Count > 0 && c.Drops.All(d => d.IsClaimed || d.IsComplete || IsClaimedThisCampaign(d) || WeClaimedDrop(d)
                                                 // a sub-gated drop we can't meet doesn't keep the campaign "open"
                                                 || !d.SubRequirementMet);

    /// <summary>Log each skipped game's reason once (deduped by game within this pass, and again by
    /// (game, reason) across passes via <see cref="_skipReasonLogged"/>), so the log isn't spammed.</summary>
    /// <param name="skips">The (game, reason) pairs passed over this pass, in order.</param>
    /// <param name="exceptGame">A game to omit (the one actually being watched), or null for none.</param>
    void LogSkips(List<(string game, string reason)> skips, string? exceptGame)
    {
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (game, reason) in skips)
        {
            if (exceptGame is not null && game.Equals(exceptGame, StringComparison.OrdinalIgnoreCase))
                continue;
            if (done.Add(game))
                LogSkip(game, reason);
        }
    }

    /// <summary>Log a game's skip reason, suppressing a repeat of the same reason for that game.</summary>
    /// <param name="game">The game being skipped.</param>
    /// <param name="reason">Why it's skipped.</param>
    void LogSkip(string game, string reason)
    {
        if (_skipReasonLogged.TryGetValue(game, out var prev) && prev == reason)
            return; // same reason already reported; don't repeat every tick
        _skipReasonLogged[game] = reason;
        Log($"Skipping {game}: {reason}.");
    }

    /// <summary>Forget the last-logged skip reason for a game, so its next skip logs again.</summary>
    /// <param name="game">The game to reset.</param>
    void ClearSkip(string game) => _skipReasonLogged.Remove(game);

    /// <summary>Null when the campaign has a drop we can harvest right now (eligible + an unharvested,
    /// not-owned, finishable drop); otherwise a short human reason it's skipped. Mirrors the filters
    /// in <see cref="OrderedCampaigns"/>/<see cref="FirstHarvestableDrop"/> so the log matches reality.</summary>
    /// <param name="c">The campaign to evaluate.</param>
    string? HarvestBlockReason(DropsCampaign c)
    {
        if (!IsEligible(c))
            return c.HasBadgeOrEmote
                ? "badge/emote campaign (enable badges & emotes in Settings)"
                : "account not linked (add the game to 'Harvest unlinked games' in Settings to try anyway)";
        if (FirstHarvestableDrop(c) is not null)
            return null;

        var pending = c.Drops.Where(d => !d.IsClaimed && !d.IsComplete && !WeClaimedDrop(d)).ToList();
        if (pending.Count == 0)
            return "all drops already earned";
        if (pending.All(IsClaimedThisCampaign))
            return "already claimed this campaign";
        if (DedupeEnabledFor(c) && pending.All(d => IsAlreadyOwned(d)))
            return "reward already owned (on your de-dupe list)";
        if (pending.All(d => IsSkipped(d.Id)))
            return "benched after no progress (retrying shortly)";
        if (!Settings.HarvestImpossibleDrops && pending.All(d => !CanFinishInTime(c, d)))
            return "not enough time left to finish before it ends (turn on 'Harvest impossible drops' to try anyway)";
        return "nothing left to harvest";
    }

    /// <summary>De-dupe (skip drops whose reward is already in the claim history) is OPT-IN per game:
    /// only for games the user added to the de-dupe list. A reward being "owned" does NOT mean a new
    /// campaign's drop can't be earned (re-runs grant the same item as a fresh drop), so skipping it
    /// unconditionally wrongly blocked earnable drops. Empty list = de-dupe nothing.</summary>
    /// <param name="c">The campaign to test.</param>
    bool DedupeEnabledFor(DropsCampaign c)
        => Settings.DedupeGames.Contains(c.Game.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The first drop worth harvesting in a campaign: unclaimed, unfinished, not one we've given
    /// up on this session, finishable in time, and - only for games on the de-dupe list - not already
    /// owned. "Claimed/complete" comes from Twitch's authoritative per-drop <c>self</c> data
    /// (isClaimed / currentMinutesWatched) - NOT from matching reward ids in the claim history, which
    /// are reused across campaigns and wrongly marked still-earnable drops as done.</summary>
    /// <param name="c">The campaign to search.</param>
    TimedDrop? FirstHarvestableDrop(DropsCampaign c)
    {
        var dedupe = DedupeEnabledFor(c);
        return c.Drops.OrderBy(d => d.RequiredMinutes)
                      .FirstOrDefault(d => !d.IsClaimed && !d.IsComplete
                                           && !WeClaimedDrop(d)                   // this exact tier is in our ledger
                                           && !IsClaimedThisCampaign(d)          // claimed this run (self can lag)
                                           && (!dedupe || !IsAlreadyOwned(d))     // opt-in: skip owned rewards
                                           && d.SubRequirementMet                 // skip sub-gated drops we don't hold the subs for
                                           && !IsSkipped(d.Id)
                                           && (Settings.HarvestImpossibleDrops || CanFinishInTime(c, d)));
    }

    /// <summary>Whether the remaining watch-time for a drop fits before its campaign ends.</summary>
    /// <param name="c">The campaign that owns the drop (for its end time).</param>
    /// <param name="d">The drop whose remaining watch-time is checked.</param>
    static bool CanFinishInTime(DropsCampaign c, TimedDrop d)
    {
        var needed = d.RequiredMinutes - d.CurrentMinutes;
        if (needed <= 0)
            return true;
        return (c.EndsAt - DateTimeOffset.UtcNow).TotalMinutes >= needed;
    }

    /// <summary>The reward is somewhere in the user's ~6-month claim history (any time). Used only for
    /// the OPT-IN de-dupe list, since owning a reward from a past campaign doesn't stop a re-run from
    /// granting it fresh. EXEMPTS rewards shared by 2+ drops in the campaign (same as
    /// <see cref="RewardIsUniqueInCampaign"/>): a reward listed on several drops is a REPEATABLE one
    /// (Marbles on Stream re-releases identical same-named drops; R6S "Esports Pack" tiers), which stays
    /// claimable every time - so de-dupe must not blanket-skip it just because a past copy is owned.</summary>
    /// <param name="drop">The drop whose reward is checked against the claim history.</param>
    bool IsAlreadyOwned(TimedDrop drop)
        => _claimedBenefits.Count > 0
           && drop.Campaign is { } c
           && drop.Benefits.Any(b => RewardIsUniqueInCampaign(b.MatchKey, c) && _claimedBenefits.ContainsKey(b.MatchKey));

    /// <summary>The drop was claimed IN THIS campaign - so it can't be earned again now and should be
    /// treated as done, even when Twitch's per-drop <c>self.isClaimed</c> lags (the Diablo "shows 0/120
    /// but claimed days ago" case). Inferred from the claim history: the reward was awarded within THIS
    /// campaign's window, and this is the ONLY campaign granting that reward whose window contains the
    /// claim time. A reward shared with a concurrent campaign (World of Tanks) is ambiguous -> stays
    /// harvestable; a reward owned only from a past run was awarded before this campaign started -> also
    /// stays harvestable.</summary>
    /// <param name="drop">The drop to test.</param>
    bool IsClaimedThisCampaign(TimedDrop drop)
        => drop.Campaign is { } c
           && drop.Benefits.Any(b => ClaimBelongsTo(b.MatchKey, c)
                                     && (RewardIsUniqueInCampaign(b.MatchKey, c) || RewardSelfUntracked(c, b.MatchKey)));

    /// <summary>Every drop in the campaign granting <paramref name="matchKey"/> shows NO per-drop self
    /// signal - none claimed, none with any watch minutes. A shared reward that looks like this yet was
    /// awarded within this campaign's window means Twitch's self has fully LAGGED: the reward is one
    /// bundle listed on several tiers, claimed once, all now reading 0/unclaimed (Marbles' "15 Community
    /// Coins" at 2h/4h/8h/10h - the reused-across-tiers "overlap"). Here the claim history is the truth,
    /// so we let it override the shared-reward guard. If instead ANY tier shows a claim or watch progress
    /// (R6S "Esports Pack", each tier separately earned and self-tracked), self is authoritative and we do
    /// NOT infer the untouched tiers from one shared award - that would lose genuinely-earnable tiers.</summary>
    /// <param name="c">The campaign whose tiers are inspected.</param>
    /// <param name="matchKey">The shared reward's match key.</param>
    bool RewardSelfUntracked(DropsCampaign c, string matchKey)
        => c.Drops.Where(d => d.Benefits.Any(b => string.Equals(b.MatchKey, matchKey, StringComparison.OrdinalIgnoreCase)))
                  .All(d => !d.IsClaimed && !d.IsComplete && d.CurrentMinutes == 0);

    /// <summary>True when exactly ONE of the campaign's drops grants reward <paramref name="matchKey"/>.
    /// Many campaigns list the SAME reward on several time-tiered drops - e.g. R6S "Esports Pack" at
    /// 1h / 3h / 6h / 9h, each a SEPARATE claimable drop that just shares a name + reward id. The claim
    /// history keeps only one award time per reward id, so it can't tell WHICH tier was claimed;
    /// attributing that single claim to every tier would wrongly mark the still-unclaimed tiers as done
    /// and skip the campaign. So when a reward is shared by multiple drops we do NOT infer
    /// claimed-this-campaign from history - we rely on Twitch's per-drop <c>self</c> (isClaimed /
    /// complete), which distinguishes the tiers correctly. Rewards unique within a campaign (Diablo,
    /// World of Tanks) keep the history-based inference, so the lag fix there is unaffected.</summary>
    /// <param name="matchKey">The reward's match key.</param>
    /// <param name="c">The campaign to count grants within.</param>
    bool RewardIsUniqueInCampaign(string matchKey, DropsCampaign c)
        => c.Drops.Count(d => d.Benefits.Any(b => string.Equals(b.MatchKey, matchKey, StringComparison.OrdinalIgnoreCase))) == 1;

    /// <summary>We have this SPECIFIC tier recorded as claimed in our local ledger (we claimed it, or saw
    /// Twitch's self report it claimed). Keyed by the drop-definition id, so it's per-tier and never
    /// mis-attributes across the shared-reward tiers that <see cref="IsClaimedThisCampaign"/> deliberately
    /// won't infer from history. Immune to Twitch's per-drop self lagging back to 0/unclaimed.</summary>
    /// <param name="drop">The drop (tier) to test.</param>
    bool WeClaimedDrop(TimedDrop drop) => _claimedDropIds.Contains(drop.Id);

    /// <summary>Record a tier as claimed both in-memory and in the persistent ledger, so it stays done
    /// across restarts and self lag.</summary>
    /// <param name="drop">The drop (tier) that was claimed.</param>
    /// <param name="at">When it was claimed.</param>
    void RecordDropClaimed(TimedDrop drop, DateTimeOffset at)
    {
        if (string.IsNullOrEmpty(drop.Id) || !_claimedDropIds.Add(drop.Id))
            return;
        _ledger.RecordDrop(drop.Id, at);
    }

    /// <summary>True when the claim of reward <paramref name="benefitId"/> can be attributed to campaign
    /// <paramref name="c"/>: awarded within c's window, and c is the only granting campaign whose window
    /// contains that time.</summary>
    /// <param name="benefitId">The reward's match key.</param>
    /// <param name="c">The candidate granting campaign.</param>
    bool ClaimBelongsTo(string benefitId, DropsCampaign c)
    {
        if (!_claimedBenefits.TryGetValue(benefitId, out var awarded) || awarded is not { } t)
            return false;
        var end = c.EndsAt.AddHours(24); // drops stay claimable ~24h after a campaign ends
        if (t < c.StartsAt || t > end)
            return false;
        if (!_benefitWindows.TryGetValue(benefitId, out var windows))
            return true; // no other campaign grants it -> unambiguous
        return windows.Count(w => t >= w.start && t <= w.end.AddHours(24)) <= 1;
    }

    /// <summary>Active, unfinished, non-excluded campaigns in harvesting order: priority-list position
    /// first, or purely soonest-expiry when 'ending soonest' is set or there's no priority list.
    /// Unlike <see cref="OrderedCampaigns"/> this KEEPS campaigns that currently have nothing harvestable
    /// so callers can log why they're skipped and list their channels.</summary>
    IEnumerable<DropsCampaign> CandidateCampaigns()
        => _forcedCampaignId is { } fid
            ? CandidateCampaignsRaw().Where(c => string.Equals(c.Id, fid, StringComparison.OrdinalIgnoreCase))
            : CandidateCampaignsRaw();

    /// <summary>The full ordered candidate set, IGNORING any manual override - used to build the queue
    /// the user picks from. The override-filtered <see cref="CandidateCampaigns"/> is what the harvester harvests.</summary>
    IEnumerable<DropsCampaign> CandidateCampaignsRaw()
    {
        var priority = Settings.PriorityGames;
        var excluded = Settings.ExcludedGames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        /// <summary>Position of a campaign's game in the priority list, or int.MaxValue if absent.</summary>
        int PriorityIndex(DropsCampaign c)
        {
            var i = priority.FindIndex(g => string.Equals(g, c.Game.Name, StringComparison.OrdinalIgnoreCase));
            return i < 0 ? int.MaxValue : i;
        }

        var cands = _campaigns
            .Where(c => c.IsActive && !c.IsFinished)
            .Where(c => !excluded.Contains(c.Game.Name))
            .Where(c => !Settings.PriorityOnly || PriorityIndex(c) != int.MaxValue)
            .ToList();

        // only campaigns with something left to earn drive a game's deadline/rank - a fully-claimed one
        // (per claim history, even when self still says unclaimed) counting would drag its game ahead of a
        // priority game whose only harvestable campaign ends later
        var schedulable = cands.Where(c => !IsCampaignFinishedForHarvesting(c)).ToList();

        // rank each GAME by its soonest-ending HARVESTABLE campaign, decided per game so that preferring
        // channel-specific campaigns within a game can't deprioritize a game whose drops expire sooner
        var gameSoonest = schedulable
            .GroupBy(c => c.Game.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Min(c => c.EndsAt), StringComparer.OrdinalIgnoreCase);

        /// <summary>Soonest end among a game's harvestable campaigns; finished-only games get MaxValue so they sort last.</summary>
        DateTimeOffset Soonest(DropsCampaign c) => gameSoonest.GetValueOrDefault(c.Game.Id, DateTimeOffset.MaxValue);

        // within a game, prefer CHANNEL-SPECIFIC (allow-listed) campaigns first: their drop only credits
        // on its channel while generic drops accrue anywhere, so harvest per-channel drops in sequence and
        // let the generic timer fill in passively (strictly more drops/hour)
        /// <summary>Whether a campaign is channel-specific (has an allow-list).</summary>
        static bool IsChannelSpecific(DropsCampaign c) => c.AllowedChannels.Count > 0;

        /// <summary>Availability tie-break key for a campaign's game: its live drops-enabled streamer count
        /// (fewer first, so scarce-stream games are grabbed while live). 0 when the availability setting is
        /// off (no effect); games not yet gathered sort last so an unknown game is never assumed scarce.</summary>
        int Availability(DropsCampaign c)
            => Settings.AvailabilityPriority
                ? (_liveCountByGame.TryGetValue(c.Game.Id, out var n) ? n : int.MaxValue)
                : 0;

        // unlinked (opt-in) campaigns are ALWAYS ordered last; the primary game order then depends on the
        // two settings below

        // no priority list: pure ending-soonest (games by soonest expiry)
        if (priority.Count == 0)
            return cands.OrderBy(c => IsUnlinked(c) ? 1 : 0)
                        .ThenBy(Soonest)
                        .ThenBy(Availability)
                        .ThenByDescending(c => IsChannelSpecific(c))
                        .ThenBy(c => c.EndsAt);

        // ending-soonest ON with a priority list: earliest-deadline-first base (never loses an earnable
        // drop), but a priority game is harvested ahead of a sooner non-priority one whenever deferring the
        // latter wouldn't lose it - see ScheduleGameRanks
        if (Settings.EndingSoonest)
        {
            var ranks = ScheduleGameRanks(schedulable, PriorityIndex);
            /// <summary>The scheduling rank of a campaign's game (int.MaxValue if unscheduled).</summary>
            int GameRank(DropsCampaign c) => ranks.TryGetValue(c.Game.Id, out var r) ? r : int.MaxValue;
            return cands.OrderBy(c => IsUnlinked(c) ? 1 : 0)
                        .ThenBy(GameRank)
                        .ThenBy(Availability)
                        .ThenByDescending(c => IsChannelSpecific(c))
                        .ThenBy(c => c.EndsAt);
        }

        // ending-soonest OFF: strict priority-list order, expiry only as a within/tiebreak
        return cands.OrderBy(c => IsUnlinked(c) ? 1 : 0)
                    .ThenBy(PriorityIndex)
                    .ThenBy(Soonest)
                    .ThenBy(Availability)
                    .ThenByDescending(c => IsChannelSpecific(c))
                    .ThenBy(c => c.EndsAt);
    }

    /// <summary>One harvestable game as a scheduling job: when its soonest drop expires (Deadline, minutes
    /// from now), how much watch-time that drop still needs (Needed, minutes), and whether the game is
    /// on the priority list.</summary>
    readonly record struct GameJob(string GameId, double Deadline, double Needed, bool IsPriority, int PriorityIndex);

    /// <summary>
    /// Priority-preferring earliest-deadline-first (EDF) schedule over the harvestable GAMES - used only
    /// when "ending soonest" is on AND a priority list exists. Returns each game id's rank (0 = harvest
    /// first). Unlinked games aren't scheduled here (they're always ordered last, outside this).
    ///
    /// Base order is EDF (soonest-expiring drop first), which provably never loses a drop that some
    /// order could still earn. The single deviation encodes the user's rule: if the soonest-ending
    /// game is NOT on the priority list, and harvesting it first would push a priority game past its own
    /// deadline (losing that priority drop) even though we could still save it by harvesting it right now,
    /// harvest the PRIORITY game first instead. So we "reprioritize the priority list as soon as it's
    /// safe" without ever chasing ending-soonest so hard that a priority drop is lost.
    /// </summary>
    /// <param name="cands">Schedulable (harvestable) campaigns to rank.</param>
    /// <param name="priorityIndex">Maps a campaign to its priority-list index (int.MaxValue if absent).</param>
    /// <returns>A map from game id to rank, where 0 = harvest first.</returns>
    Dictionary<string, int> ScheduleGameRanks(List<DropsCampaign> cands, Func<DropsCampaign, int> priorityIndex)
    {
        var now = DateTimeOffset.UtcNow;

        // one job per LINKED game: its soonest-ending campaign sets the deadline and the watch-time its
        // next unharvested drop still needs
        var jobs = cands
            .Where(c => !IsUnlinked(c))
            .GroupBy(c => c.Game.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var soonest = g.OrderBy(c => c.EndsAt).First();
                var drop = soonest.FirstUnharvestedDrop;
                var needed = drop is null ? 0.0 : Math.Max(0, drop.RequiredMinutes - drop.CurrentMinutes);
                var pri = g.Min(priorityIndex); // all campaigns of a game share the same index
                return new GameJob(g.Key, (soonest.EndsAt - now).TotalMinutes, needed, pri != int.MaxValue, pri);
            })
            .ToList();

        var remaining = jobs.ToList();
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var t = 0.0;   // simulated clock, minutes from now
        var rank = 0;
        while (remaining.Count > 0)
        {
            var pick = ChooseNextGame(remaining, t);
            ranks[pick.GameId] = rank++;
            t += pick.Needed;
            remaining.Remove(pick);
        }
        return ranks;
    }

    /// <summary>The next game to harvest under the priority-preferring EDF rule (see ScheduleGameRanks).</summary>
    /// <param name="remaining">Jobs not yet ranked.</param>
    /// <param name="t">Simulated clock (minutes from now) after the already-ranked jobs.</param>
    static GameJob ChooseNextGame(List<GameJob> remaining, double t)
    {
        // earliest-deadline-first; priority as the tiebreak when deadlines are equal
        var soonest = remaining
            .OrderBy(j => j.Deadline)
            .ThenByDescending(j => j.IsPriority)
            .ThenBy(j => j.PriorityIndex)
            .ThenBy(j => j.GameId, StringComparer.OrdinalIgnoreCase)
            .First();

        if (soonest.IsPriority)
            return soonest;

        // soonest is non-priority: protect the soonest priority game that harvesting 'soonest' first would
        // LOSE but that we could still save by harvesting now - otherwise fall through to EDF
        foreach (var p in remaining.Where(j => j.IsPriority)
                                   .OrderBy(j => j.Deadline)
                                   .ThenBy(j => j.PriorityIndex))
        {
            var savableNow = t + p.Needed <= p.Deadline;                  // p finishes if harvested right now
            var lostIfDeferred = t + soonest.Needed + p.Needed > p.Deadline; // ...but not after 'soonest'
            if (savableNow && lostIfDeferred)
                return p;
        }

        return soonest;
    }

    /// <summary>Candidate campaigns that have a drop we can actually harvest right now (eligible, with an
    /// unharvested/not-owned/finishable drop) - the set used to pick a target and check for preemption.</summary>
    IEnumerable<DropsCampaign> OrderedCampaigns()
        => CandidateCampaigns().Where(c => HarvestBlockReason(c) is null);

    /// <summary>Harvestable campaigns in order IGNORING any override - used to detect a higher-priority
    /// campaign that should end an override (when the user allows it).</summary>
    IEnumerable<DropsCampaign> OrderedCampaignsAll()
        => CandidateCampaignsRaw().Where(c => HarvestBlockReason(c) is null);

    /// <summary>When an override is active and allowed to yield, end it only for a campaign that both newly
    /// appeared AFTER the override was set (isn't in <see cref="_knownAtOverride"/>) and ranks higher than
    /// the override target in the effective order (ending-soonest, availability, or priority list), and
    /// currently has a live channel. A campaign already known when the override was set - even one that
    /// just came online - never ends it. Returns whether the override was ended.</summary>
    /// <param name="target">The campaign the override is pinned to.</param>
    /// <param name="ct">Cancels the live-channel lookups on shutdown.</param>
    /// <summary>Whether the current override target is watchable right now (still harvestable and has a live
    /// drops-enabled channel) - used to snap back to it after harvesting a fallback while its stream was down.</summary>
    /// <param name="ct">Cancels the live-channel lookup on shutdown.</param>
    async Task<bool> ForcedTargetWatchableAsync(CancellationToken ct)
    {
        var forced = _campaigns.FirstOrDefault(c => string.Equals(c.Id, _forcedCampaignId, StringComparison.OrdinalIgnoreCase));
        if (forced is null || HarvestBlockReason(forced) is not null)
            return false;
        return await ChooseChannelAsync(forced, ct).ConfigureAwait(false) is not null;
    }

    async Task<bool> TryEndOverrideForNewHigherPriorityAsync(DropsCampaign target, CancellationToken ct)
    {
        foreach (var c in OrderedCampaignsAll())
        {
            if (ReferenceEquals(c, target))
                break;
            // "new" means it was BOTH not known when the override was set AND actually started afterwards.
            // A campaign released earlier (e.g. this morning's daily) that we simply hadn't fetched yet at
            // override time must NOT end the override - the user deliberately overrode past it. Requiring
            // StartsAt > cutoff makes this robust to fetch timing and daily-instance id churn.
            if (_knownAtOverride.Contains(c.Id) || c.StartsAt <= _overrideSetUtc)
                continue;
            var live = await _channels.FetchLiveChannelsForGameAsync(c.Game, 1, ct).ConfigureAwait(false);
            if (live.Any())
            {
                Log($"New higher-priority campaign ({c.Game.Name}) came online after the override - ending it to harvest that instead.");
                ClearCampaignOverride();
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether the account can harvest the campaign: badge/emote campaigns need that setting on;
    /// others need the game linked or on the "harvest unlinked" list.</summary>
    /// <param name="c">The campaign to test.</param>
    bool IsEligible(DropsCampaign c) => c.HasBadgeOrEmote
        ? Settings.EnableBadgesEmotes
        : c.Linked || Settings.HarvestUnlinkedGames.Contains(c.Game.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>An opt-in unlinked campaign (harvestable only because its game is on the "harvest unlinked"
    /// list). These are always ordered LAST - below every linked/badge campaign, regardless of the
    /// priority list or ending-soonest.</summary>
    /// <param name="c">The campaign to test.</param>
    static bool IsUnlinked(DropsCampaign c) => !c.Linked && !c.HasBadgeOrEmote;

    /// <summary>The concurrent harvestable campaigns for the same game as <paramref name="campaign"/>
    /// (including it). A single watch credits EVERY open one for free and every restricted one whose
    /// allow-list contains the watched channel, so these drive the "credit the most at once" channel
    /// preference below. Drawn from the UNFILTERED set so even an active override still banks the other
    /// same-game drops as a free bonus.</summary>
    /// <param name="campaign">The target campaign whose same-game siblings to gather.</param>
    List<DropsCampaign> SameGameHarvestableSiblings(DropsCampaign campaign)
        => CandidateCampaignsRaw()
            .Where(c => string.Equals(c.Game.Id, campaign.Game.Id, StringComparison.OrdinalIgnoreCase)
                        && HarvestBlockReason(c) is null
                        && FirstHarvestableDrop(c) is not null)
            .ToList();

    /// <summary>How many of <paramref name="siblings"/> a single watch on <paramref name="login"/> would
    /// credit: every OPEN campaign (empty allow-list) always counts; a RESTRICTED one counts only when its
    /// allow-list contains the channel. Higher = one watch banks more concurrent same-game drops at once,
    /// which is what we steer channel selection toward (e.g. an Albion channel on a restricted campaign's
    /// list also credits the game's open campaign, so watching it advances both instead of one).</summary>
    /// <param name="login">The channel login being scored.</param>
    /// <param name="siblings">The concurrent same-game harvestable campaigns.</param>
    static int CrossCreditScore(string login, IReadOnlyList<DropsCampaign> siblings)
        => siblings.Count(s => s.AllowedChannels.Count == 0
                               || s.AllowedChannels.Contains(login, StringComparer.OrdinalIgnoreCase));

    /// <summary>Choose a live, drops-crediting channel to watch for the campaign, honoring cooldowns and
    /// the prefer/avoid lists, or null if none is online right now. When the game has several concurrent
    /// harvestable campaigns, prefers a channel that credits the MOST of them so one watch advances every
    /// same-game drop together instead of leaving some behind.</summary>
    /// <param name="campaign">The campaign to find a channel for.</param>
    /// <param name="ct">Cancels the lookups on shutdown.</param>
    async Task<TwitchChannel?> ChooseChannelAsync(DropsCampaign campaign, CancellationToken ct)
    {
        // (a manual "Watch" pick is handled up-front in PickWatchableTargetAsync, on the campaign matching
        // the channel's game, not whatever campaign is evaluated first here)

        // restricted (official-channel-only) campaign. The allow-list can be HUGE (Division 2 ~485), so
        // instead of probing it (the old code only checked the first 20, so a live official further down
        // looked offline and the whole campaign got skipped) we ask the game's drops directory which
        // channels are live and take the first on the allow-list; direct probes only fill the gaps, bounded
        // so they can't flood the API
        var siblings = SameGameHarvestableSiblings(campaign);
        if (campaign.AllowedChannels.Count > 0)
        {
            var allowed = campaign.AllowedChannels.ToHashSet(StringComparer.OrdinalIgnoreCase);

            var live = await _channels.FetchLiveChannelsForGameAsync(campaign.Game, 100, ct).ConfigureAwait(false);
            // among the LIVE allow-listed channels, take the one that also credits the most concurrent
            // same-game campaigns (a channel on this restricted list may also sit in another campaign's
            // list, so one watch advances both) - not merely the first one we happen to see
            var best = live
                .Where(ch => allowed.Contains(ch.Login) && !OnCooldown(ch.Login) && ch.Online && !string.IsNullOrEmpty(ch.Id))
                .OrderByDescending(ch => CrossCreditScore(ch.Login, siblings))
                .FirstOrDefault();
            if (best is not null)
                return best;

            var seen = live.Select(c => c.Login).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // probe the not-yet-seen allow-list logins, checking the ones that credit the most concurrent
            // campaigns first so a multi-campaign channel wins the tie when several are live
            foreach (var login in campaign.AllowedChannels
                         .Where(l => !seen.Contains(l) && !OnCooldown(l))
                         .OrderByDescending(l => CrossCreditScore(l, siblings))
                         .Take(40))
            {
                var ch = new TwitchChannel
                {
                    Id = "", Login = login, DisplayName = login, Game = campaign.Game, DropsEnabled = true,
                };
                await _channels.RefreshChannelAsync(ch, ct).ConfigureAwait(false);
                if (ch.Online && !string.IsNullOrEmpty(ch.Id))
                    return ch;
            }
            return null;
        }

        // open campaign: any live drops-enabled channel of the game credits the generic drop. The directory
        // can lag, so try several before concluding nothing's live; prefer/avoid only tune WHICH one we idle on
        var candidates = (await _channels.FetchLiveChannelsForGameAsync(campaign.Game, 30, ct).ConfigureAwait(false))
            .Where(c => !OnCooldown(c.Login)) // skip channels benched for not crediting us recently
            .ToList();
        // when the game has other concurrent campaigns, prefer a channel that also credits them (e.g. one
        // that sits in a restricted sibling campaign's allow-list) so a single watch banks every same-game
        // drop instead of just this open one. OrderByDescending is stable, so equal-scoring channels keep
        // the directory's original order, and the prefer/avoid lists below still win outright.
        if (siblings.Count > 1)
            candidates = candidates.OrderByDescending(c => CrossCreditScore(c.Login, siblings)).ToList();
        var preferred = Settings.PreferredChannels;
        var avoided = Settings.AvoidedChannels;

        // PREFER: idle on a live preferred channel. Only reached for open campaigns (officials handled
        // above), and directory results are already DROPS_ENABLED, so it's guaranteed to credit
        if (preferred.Count > 0)
        {
            var chosen = candidates.FirstOrDefault(c => preferred.Contains(c.Login, StringComparer.OrdinalIgnoreCase));
            if (chosen is not null)
            {
                if (!chosen.Online || string.IsNullOrEmpty(chosen.BroadcastId))
                    await _channels.RefreshChannelAsync(chosen, ct).ConfigureAwait(false);
                if (chosen.Online)
                {
                    Log($"Watching preferred channel {chosen.DisplayName} [{campaign.Game.Name}] - no official channel needed here, so idling on your pick (the drop still credits).");
                    return chosen;
                }
            }
        }

        // AVOID: try non-avoided channels first; fall back to an avoided one only if nothing else is live
        var avoidedSet = avoided.Count > 0 ? avoided.ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        if (avoidedSet is not null)
        {
            foreach (var cand in candidates.Where(c => !avoidedSet.Contains(c.Login)).Take(6))
            {
                if (!cand.Online || string.IsNullOrEmpty(cand.BroadcastId))
                    await _channels.RefreshChannelAsync(cand, ct).ConfigureAwait(false);
                if (cand.Online)
                    return cand;
            }
            foreach (var cand in candidates.Where(c => avoidedSet.Contains(c.Login)).Take(6))
            {
                if (!cand.Online || string.IsNullOrEmpty(cand.BroadcastId))
                    await _channels.RefreshChannelAsync(cand, ct).ConfigureAwait(false);
                if (cand.Online)
                {
                    Log($"Watching avoided channel {cand.DisplayName} [{campaign.Game.Name}] - it's the only drops-enabled stream live for this game right now.");
                    return cand;
                }
            }
            return null;
        }

        foreach (var cand in candidates.Take(6))
        {
            if (!cand.Online || string.IsNullOrEmpty(cand.BroadcastId))
                await _channels.RefreshChannelAsync(cand, ct).ConfigureAwait(false);
            if (cand.Online)
                return cand;
        }
        return null;
    }

    /// <summary>Kick off a Channels-tab refresh in the background so it never delays harvesting. Throttled
    /// to at most once per 5 minutes unless <paramref name="force"/> (e.g. a fresh pick) - so it isn't
    /// spamming Twitch's directory API.</summary>
    /// <param name="active">The currently watched channel to flag, or null.</param>
    /// <param name="ct">Cancels the background refresh on shutdown.</param>
    /// <param name="force">Bypass the throttle (e.g. right after a fresh pick).</param>
    void QueueChannelRefresh(TwitchChannel? active, CancellationToken ct, bool force = false)
    {
        if (!force && DateTimeOffset.UtcNow - _lastChannelRefresh < TimeSpan.FromMinutes(5))
            return;
        _lastChannelRefresh = DateTimeOffset.UtcNow;
        _ = RefreshChannelsSafeAsync(active, ct);
    }

    /// <summary>Run the tracked-channels refresh, swallowing any failure (the list is best-effort).</summary>
    /// <param name="active">The currently watched channel to flag, or null.</param>
    /// <param name="ct">Cancels the refresh on shutdown.</param>
    async Task RefreshChannelsSafeAsync(TwitchChannel? active, CancellationToken ct)
    {
        try { await RefreshTrackedChannelsAsync(active, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch { /* the channel list is best-effort; never let it disrupt harvesting */ }
    }

    /// <summary>Populate the Channels tab with every channel that could currently earn a drop, across
    /// ALL harvestable campaigns (not just the one being watched): open campaigns contribute their game's
    /// live drops-enabled channels; restricted campaigns contribute their allow-listed channels. The
    /// active channel is flagged. Best-effort and bounded so it can't flood the API.</summary>
    /// <param name="active">The currently watched channel to flag, or null.</param>
    /// <param name="ct">Cancels the gather on shutdown.</param>
    async Task RefreshTrackedChannelsAsync(TwitchChannel? active, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _refreshingChannels, 1, 0) == 1)
            return; // one gather at a time (self-heals via the hard timeout below)
        SetRefreshingChannels(true);
        try
        {
            // 1. which games are harvestable, in harvesting order (no network). ONE directory fetch per game
            //    covers open + channel-specific: a live official channel shows up in the game's drops
            //    directory, so we don't probe the allow-list (hundreds of channels per game - probing all
            //    rate-limited the gather so only the first game or two populated). We just remember which
            //    logins are "official" to flag/sort them first.
            var gameRank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var games = new Dictionary<string, Game>(StringComparer.OrdinalIgnoreCase); // every harvestable game
            var officialLogins = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // allow-listed official logins
            var orderedGameNames = new List<string>();
            var seenGameName = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // games with at least one OPEN harvestable campaign: any live drops-enabled channel credits us,
            // so show them all. A channel-specific-only game isn't here - we show just its officials, since
            // a generic directory stream credits a different campaign, not the drop the user can still earn
            var openGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nextRank = 0;

            // gather over ALL harvestable campaigns, IGNORING any manual override (CandidateCampaignsRaw, not
            // the override-filtered CandidateCampaigns) - so the Channels tab always shows every game and
            // the override-yield check can see when a higher-priority game comes online. The override still
            // governs only what's actively HARVESTED, not what we discover.
            foreach (var c in CandidateCampaignsRaw())
            {
                if (HarvestBlockReason(c) is not null)
                    continue; // only campaigns we could actually harvest right now
                if (IsUnlinked(c) && !Settings.ShowUnlinkedInChannels)
                    continue; // opt-out: don't spend directory fetches on unlinked games' channels
                if (!gameRank.ContainsKey(c.Game.Id))
                {
                    if (gameRank.Count >= MaxTrackedGamesShown)
                        continue; // bound the tab so a huge campaign list can't overwhelm the UI
                    gameRank[c.Game.Id] = nextRank++;
                    games[c.Game.Id] = c.Game;
                    if (seenGameName.Add(c.Game.Name)) orderedGameNames.Add(c.Game.Name);
                }
                if (c.AllowedChannels.Count == 0)
                    openGames.Add(c.Game.Id); // an open campaign here -> generic channels credit
                foreach (var login in c.AllowedChannels)
                    officialLogins.Add(login);
            }

            HarvestableGames = orderedGameNames;
            Log($"Getting channels for {orderedGameNames.Count} harvestable game(s)...", HarvesterLogLevel.Debug);

            // 2. show the game groups IMMEDIATELY (just the active channel) so the tab is never blank
            //    while the per-game directory fetches run - even if a fetch is slow or hangs
            var byLogin = new Dictionary<string, (TwitchChannel ch, int rank)>(StringComparer.OrdinalIgnoreCase);
            if (active is not null)
                byLogin[active.Login] = (active, gameRank.GetValueOrDefault(active.Game?.Id ?? "", int.MaxValue));

            // painted progressively after each game's fetch, but THROTTLED (except a forced final paint)
            // so the tab isn't rewritten ~25 times in a few seconds; fewer writes collide less with the
            // user expanding a group, and the last write always carries the complete data
            var lastPaint = DateTimeOffset.MinValue;
            /// <summary>Sort the gathered channels and push them to the Channels tab (throttled).</summary>
            /// <param name="force">Bypass the throttle for the final paint.</param>
            void Paint(bool force = false)
            {
                var now = DateTimeOffset.UtcNow;
                if (!force && now - lastPaint < TimeSpan.FromMilliseconds(1200))
                    return;
                lastPaint = now;
                var ordered = byLogin.Values
                    .Where(x => x.ch.Online)                                    // live only - we don't list offline channels
                    .OrderByDescending(x => ReferenceEquals(x.ch, active))      // watched channel pinned first
                    .ThenBy(x => x.rank)                                        // then harvesting order (game)
                    .ThenByDescending(x => officialLogins.Contains(x.ch.Login)) // officials first within a game
                    .ThenByDescending(x => x.ch.ViewerCount)                    // then viewers
                    .Select(x => x.ch)
                    .ToList();
                UpdateTrackedChannels(ordered, active, officialLogins);
            }

            Paint(force: true);

            // 3. fetch each game's channels SEQUENTIALLY, paced, under a hard timeout. Firing them
            //    concurrently gets the directory API rate-limited so only the first game or two return
            //    ("0 live" for the rest); pacing + the GQL 429 backoff keeps every game populated, and the
            //    hard timeout stops a hung request from stalling the tab
            var processed = 0;   // games actually fetched (vs cut short by the timeout)
            var emptyGames = new List<string>(); // games that came back with 0 live channels
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                var fct = timeout.Token;
                try
                {
                    var first = true;
                    // fetch OPEN games first: they contribute the channels the tab actually shows, and under
                    // rate-limiting the EARLY requests are the ones that succeed - so open games stay
                    // populated instead of being starved behind channel-specific games (which yield ~nothing)
                    foreach (var g in games.Values.OrderByDescending(g => openGames.Contains(g.Id)))
                    {
                        if (!first)
                            await Task.Delay(300, fct).ConfigureAwait(false); // pace to dodge rate limits
                        first = false;
                        IReadOnlyList<TwitchChannel> list;
                        try { list = await _channels.FetchLiveChannelsForGameAsync(g, 12, fct).ConfigureAwait(false); }
                        catch { list = Array.Empty<TwitchChannel>(); }
                        processed++;
                        _liveCountByGame[g.Id] = list.Count; // availability signal for tie-breaking the harvesting order
                        // channel-specific-only game: keep just the official (allow-listed) streams - the
                        // rest of the directory credits other campaigns, not the drop we still need
                        var openGame = openGames.Contains(g.Id);
                        var creditable = openGame ? list : list.Where(ch => officialLogins.Contains(ch.Login)).ToList();
                        if (creditable.Count == 0) emptyGames.Add(g.Name);
                        var rank = gameRank.GetValueOrDefault(g.Id, int.MaxValue);
                        foreach (var ch in creditable)
                            if (!byLogin.ContainsKey(ch.Login))
                                byLogin[ch.Login] = (ch, rank);
                        Paint(); // progressive fill
                    }
                }
                catch { /* timed out or transient - show whatever we gathered */ }
            }

            Paint(); // final state (also covers the loop being cut short by the timeout)

            // subscribe to real-time stream state for the live channels now shown, so the tab reacts the
            // instant one goes offline/online; SetTopicsAsync diffs incrementally, adding/removing only what changed
            lock (_watchedChannels)
            {
                _watchedChannels.Clear();
                foreach (var (chn, _) in byLogin.Values)
                    if (chn.Online && !string.IsNullOrEmpty(chn.Id))
                        _watchedChannels[chn.Id!] = chn;
            }
            _ = ResubscribeAsync(ct);

            Log($"Channels ready: {orderedGameNames.Count} game(s), {byLogin.Values.Count(x => x.ch.Online)} live channel(s).", HarvesterLogLevel.Debug);
            // diagnostics for the "0 live" report: note if the gather was cut short by the timeout and
            // which games returned no live channels
            if (processed < games.Count)
                Log($"Channel gather stopped early: {processed}/{games.Count} games fetched before the 60s timeout - the rest will read 0 live until the next refresh.", HarvesterLogLevel.Warn);
            if (emptyGames.Count > 0)
                Log($"No live channels returned for: {string.Join(", ", emptyGames.Take(25))}.", HarvesterLogLevel.Debug);
        }
        finally
        {
            Interlocked.Exchange(ref _refreshingChannels, 0);
            SetRefreshingChannels(false);
        }
    }

    /// <summary>Rebuild the websocket topic set (user drops/notifications plus the active and tracked
    /// channels' stream-state) and apply it. Best-effort - polling still covers offline detection.</summary>
    /// <param name="ct">Cancels the topic update on shutdown.</param>
    async Task ResubscribeAsync(CancellationToken ct)
    {
        var userId = _auth.State.UserId;
        var topics = new List<WebsocketTopic>();
        if (!string.IsNullOrEmpty(userId))
        {
            topics.Add(new WebsocketTopic($"drops:{userId}", TwitchConstants.Topics.UserDrops(userId)));
            topics.Add(new WebsocketTopic($"notif:{userId}", TwitchConstants.Topics.UserNotifications(userId)));
        }
        // subscribe to the active channel AND every tracked channel's stream-state so we hear up/down in
        // real time; bounded to stay within the websocket pool's topic budget
        var streamIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ActiveChannel is { } ch && !string.IsNullOrEmpty(ch.Id))
            streamIds.Add(ch.Id!);
        lock (_watchedChannels)
            foreach (var wc in _watchedChannels.Values)
            {
                if (streamIds.Count >= 140) break;
                if (!string.IsNullOrEmpty(wc.Id)) streamIds.Add(wc.Id);
            }
        foreach (var id in streamIds)
            topics.Add(new WebsocketTopic($"stream:{id}", TwitchConstants.Topics.ChannelStreamState(id)));

        try { await _ws.SetTopicsAsync(topics, ct).ConfigureAwait(false); }
        catch { /* websocket is best-effort; polling covers offline detection */ }
    }

    /// <summary>Handle a PubSub message: nudge a sync/claim on drop events, and mirror stream up/down
    /// onto the active and tracked channels for the Channels tab.</summary>
    /// <param name="topic">The PubSub topic the message arrived on.</param>
    /// <param name="message">The message payload.</param>
    void OnPubSubMessage(string topic, JsonElement message)
    {
        // Claim the INSTANT Twitch signals a drop is (about to be) claimable, from any of the three
        // signals it sends, instead of drifting up to a full watch tick:
        //  - drop-claim: the direct claimable event.
        //  - drop-progress that has reached the required minutes: the drop just completed.
        if (topic.StartsWith("user-drop-events", StringComparison.Ordinal))
        {
            var type = message.Str("type");
            if (type == "drop-claim" || (type == "drop-progress" && DropProgressComplete(message)))
            {
                _claimNowRequested = true;
                Wake();
            }
        }
        //  - the onsite "you can now claim your drop" reminder (create-notification). In practice this is
        //    often the fastest claimable signal Twitch sends, so claiming on it keeps us instant.
        else if (topic.StartsWith("onsite-notifications", StringComparison.Ordinal))
        {
            if (message.Str("type") == "create-notification" && IsDropNotification(message))
            {
                _claimNowRequested = true;
                Wake();
            }
        }
        else if (topic.StartsWith("video-playback-by-id", StringComparison.Ordinal))
        {
            var type = message.Str("type");
            if (type is not ("stream-up" or "stream-down"))
                return;
            var online = type == "stream-up";
            var id = topic[(topic.LastIndexOf('.') + 1)..];

            if (!online && ActiveChannel is { } ach && string.Equals(ach.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                // the stream we're harvesting just went down - re-pick NOW rather than waiting for the next
                // watch tick to notice; the re-pick scans every game's live streams for the next target
                UiDispatch.Current.Post(() => ach.Online = false);
                _switchRequested = true;
                Wake();
            }

            TwitchChannel? wc;
            lock (_watchedChannels) _watchedChannels.TryGetValue(id, out wc);
            if (wc is not null)
                // websocket thread -> mutate the bound tab on the UI thread; offline channels are pulled
                // from the list (we don't show offline), a subscribed one coming back is re-added
                UiDispatch.Current.Post(() =>
                {
                    wc.Online = online;
                    if (online)
                    {
                        if (!TrackedChannels.Contains(wc)) TrackedChannels.Add(wc);
                    }
                    else
                    {
                        TrackedChannels.Remove(wc);
                    }
                });
        }
    }

    /// <summary>Whether a drop-progress PubSub payload reports the drop has reached (or passed) its required
    /// watch minutes - i.e. it just completed and is about to be claimable.</summary>
    /// <param name="message">The drop-progress message.</param>
    static bool DropProgressComplete(JsonElement message)
    {
        if (!message.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return false;
        var cur = data.TryGetProperty("current_progress_min", out var c) && c.TryGetInt32(out var cv) ? cv : 0;
        var req = data.TryGetProperty("required_progress_min", out var r) && r.TryGetInt32(out var rv) ? rv : 0;
        return req > 0 && cur >= req;
    }

    /// <summary>Whether an onsite create-notification is a drop-reward reminder (so we claim), rather than
    /// an unrelated onsite notification. Unknown shapes are treated as a drop reminder - a needless sync is
    /// cheap, and missing a claim is not.</summary>
    /// <param name="message">The create-notification message.</param>
    static bool IsDropNotification(JsonElement message)
    {
        if (!message.TryGetProperty("data", out var data) || !data.TryGetProperty("notification", out var n))
            return true;
        var nt = n.Str("type");
        return nt is null || nt.Contains("drop", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Validate the token; on a real expiry publish LoginExpired and stop, but treat a transient
    /// validation failure as still-authed (keep harvesting with the cached token).</summary>
    /// <param name="ct">Cancels the validation on shutdown.</param>
    /// <returns>True to keep harvesting; false when login has genuinely expired.</returns>
    async Task<bool> EnsureAuthAsync(CancellationToken ct)
    {
        try
        {
            if (await _auth.ValidateAsync(ct).ConfigureAwait(false))
                return true;
        }
        catch { return true; /* transient/offline: keep going with the cached token */ }

        _bus.Publish(new LoginExpiredEvent());
        Log("Login expired - please log in again.", HarvesterLogLevel.Warn);
        IsRunning = false;
        return false;
    }

    /// <summary>Fetch campaigns and refresh the claim history (throttled to ~20 min), merging Twitch's
    /// history with the local ledger so a claimed drop never re-appears as unclaimed, and publishing the
    /// discovery plus any new drops.</summary>
    /// <param name="ct">Cancels the fetch on shutdown.</param>
    async Task EnsureCampaignsAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - _lastCampaignFetch < TimeSpan.FromMinutes(RediscoverMinutes) && _campaigns.Count > 0)
            return;

        try
        {
            var campaigns = await _inventory.FetchCampaignsAsync(Settings.EnableBadgesEmotes, Settings.HarvestUnlinkedGames, ct).ConfigureAwait(false);
            _lastCampaignFetch = DateTimeOffset.UtcNow;
            _campaigns = campaigns.ToList(); // harvesting loop's private snapshot (atomic swap)
            _benefitWindows = DropsCampaign.BenefitWindows(_campaigns); // to attribute claims to campaigns

            // always refresh the claimed-benefit set - needed to skip already-earned drops (which can
            // never credit), not just for the opt-in de-dupe list
            try
            {
                var claimed = await _inventory.GetClaimedBenefitsAsync(ct).ConfigureAwait(false);
                // only replace the claim history when the fetch returned something: a transient inventory
                // failure returns an EMPTY map, and Clear()+repopulating with that wiped the whole history
                // (made already-claimed Diablo drops look unclaimed). History only grows, so empty = failed read.
                if (claimed.Count > 0)
                {
                    _claimedBenefits.Clear();
                    foreach (var kv in claimed) _claimedBenefits[kv.Key] = kv.Value;
                    _ledger.RecordAll(claimed); // accumulate Twitch's history into our permanent local ledger
                }
                // merge the local ledger back in - our own (and past Twitch-confirmed) claims survive
                // Twitch's lag / a dropped read, so a claimed drop never re-appears as unclaimed
                foreach (var kv in _ledger.All)
                    if (!_claimedBenefits.ContainsKey(kv.Key))
                        _claimedBenefits[kv.Key] = kv.Value;

                // per-tier claim set (drop-definition id): lets a shared-reward campaign (Marbles' 15-coin
                // tiers) finish - each tier tracked individually and stays done even after self lags to 0
                _claimedDropIds.Clear();
                foreach (var id in _ledger.Drops.Keys)
                    _claimedDropIds.Add(id);
                // just-synced campaigns in hand: drop any per-tier claim the live data contradicts (a tier
                // Twitch still shows mid-progress can't be claimed) so a stale mark doesn't strand a campaign
                PurgeStaleLedgerClaims();
                if (!_claimHistoryLogged && _claimedBenefits.Count > 0)
                {
                    _claimHistoryLogged = true;
                    var timed = _claimedBenefits.Values.Count(v => v is not null);
                    Log($"Claim history: {_claimedBenefits.Count} owned reward(s), {timed} with award dates.");
                }
            }
            catch (GqlAuthException) { throw; }
            catch { /* best-effort */ }

            UiDispatch.Current.Post(() =>
            {
                Campaigns.Clear();
                foreach (var c in campaigns)
                    Campaigns.Add(c);
            });
            Log($"Discovered {campaigns.Count} active campaign(s).");
            AnnounceNewDrops(campaigns);
        }
        catch (GqlAuthException)
        {
            _bus.Publish(new LoginExpiredEvent());
            IsRunning = false;
        }
        catch (Exception ex)
        {
            Log($"Campaign discovery failed: {ex.Message}", HarvesterLogLevel.Warn);
        }
    }

    // display ceilings for the Channels tab, kept modest so a huge campaign list can't choke the grouped
    // UI (a nested-repeater CollectionView gets slow with hundreds of rows / dozens of groups)
    const int MaxTrackedGamesShown = 25;
    const int MaxTrackedChannelsShown = 200;

    /// <summary>Replace the Channels tab rows with the candidates (capped), setting each row's active /
    /// official / preferred / avoided flags.</summary>
    /// <param name="candidates">The channels to show, already in display order.</param>
    /// <param name="active">The currently watched channel, or null.</param>
    /// <param name="officialLogins">Logins to flag as official, or null to leave the flag unchanged.</param>
    void UpdateTrackedChannels(IReadOnlyList<TwitchChannel> candidates, TwitchChannel? active,
                               IReadOnlySet<string>? officialLogins = null)
    {
        var pref = Settings.PreferredChannels;
        var avoid = Settings.AvoidedChannels;
        UiDispatch.Current.Post(() =>
        {
            TrackedChannels.Clear();
            foreach (var c in candidates.Take(MaxTrackedChannelsShown))
            {
                c.IsActive = active is not null && ReferenceEquals(c, active);
                if (!c.IsActive) c.PendingSwitch = false; // clear stale "Switching..." on rebuild
                if (officialLogins is not null) c.IsOfficial = officialLogins.Contains(c.Login);
                c.IsPreferred = pref.Contains(c.Login, StringComparer.OrdinalIgnoreCase);
                c.IsAvoided = avoid.Contains(c.Login, StringComparer.OrdinalIgnoreCase);
                TrackedChannels.Add(c);
            }
        });
    }

    /// <summary>Set the active channel/campaign/drop, flag which drops tick their countdown, and publish
    /// the resulting state (channel-switched, active-target, harvesting-state, next-up, queue).</summary>
    /// <param name="channel">The now-active channel, or null when idle.</param>
    /// <param name="campaign">The now-active campaign, or null when idle.</param>
    /// <param name="drop">The now-active drop, or null when idle.</param>
    /// <param name="summary">Human-readable status line for the UI.</param>
    void SetActive(TwitchChannel? channel, DropsCampaign? campaign, TimedDrop? drop, string summary)
    {
        ActiveChannel = channel;
        ActiveCampaign = campaign;
        ActiveDrop = drop;
        _lastSummary = summary;

        // only the campaign being harvested right now ticks its per-second "time remaining" countdown
        foreach (var c in _campaigns)
        {
            var watched = campaign is not null && ReferenceEquals(c, campaign);
            foreach (var d in c.Drops)
                d.IsActivelyWatched = watched && !d.IsClaimed && !d.IsComplete;
        }

        _bus.Publish(new ChannelSwitchedEvent(channel, summary));
        _bus.Publish(new ActiveTargetEvent(channel, campaign, drop));
        _bus.Publish(new HarvestingStateEvent(IsRunning, summary));
        PublishNextUp();
        PublishQueue();
    }

    /// <summary>Publish the ordered list of harvestable campaigns (the queue the user can pick an override
    /// from), each tagged with whether it's the active target and whether it's the current override. The
    /// campaign being harvested right now is pinned to the top so the queue reads now -> next -> later; the
    /// rest keep priority order (a manual override is harvested out of priority order, so without the pin the
    /// active row would sit wherever its game happens to rank instead of first).</summary>
    void PublishQueue()
    {
        var items = new List<HarvestingQueueItem>();
        foreach (var c in CandidateCampaignsRaw())
        {
            if (HarvestBlockReason(c) is not null)
                continue; // only actually-harvestable campaigns in the queue
            var d = FirstHarvestableDrop(c);
            items.Add(new HarvestingQueueItem(
                c.Id, c.Game.Name, c.Name, d?.RewardName, d?.RewardImageUrl,
                ReferenceEquals(c, ActiveCampaign),
                string.Equals(c.Id, _forcedCampaignId, StringComparison.OrdinalIgnoreCase)));
            if (items.Count >= 25)
                break;
        }
        var ordered = items.OrderByDescending(i => i.IsActive).ToList();
        _bus.Publish(new HarvestingQueueEvent(ordered, _forcedCampaignId is not null));
    }

    string? _lastNextUpCampaignId;
    string? _lastNextUpDropId;

    /// <summary>Publish what the harvester will move to next (the first harvestable campaign that isn't the
    /// active one, plus its first harvestable drop), deduped so it only fires when the queue changes.
    /// Called on every target change and after each campaign refresh, since it's subject to change.</summary>
    void PublishNextUp()
    {
        var next = OrderedCampaigns().FirstOrDefault(c => !ReferenceEquals(c, ActiveCampaign));
        var nextDrop = next is null ? null : FirstHarvestableDrop(next);
        if (next?.Id == _lastNextUpCampaignId && nextDrop?.Id == _lastNextUpDropId)
            return;
        _lastNextUpCampaignId = next?.Id;
        _lastNextUpDropId = nextDrop?.Id;
        _bus.Publish(new NextUpEvent(next, nextDrop));
    }

    /// <summary>Publish a log line on the event bus.</summary>
    /// <param name="message">The text to log.</param>
    /// <param name="level">The severity level.</param>
    void Log(string message, HarvesterLogLevel level = HarvesterLogLevel.Info)
        => _bus.Publish(new LogEvent(message, level));

    /// <summary>Build a serializable snapshot of the harvester's live state and per-campaign/drop decisions
    /// (why each is harvested/skipped/finished, claim attribution) for the debug server.</summary>
    public object GetDebugSnapshot()
    {
        var now = DateTimeOffset.UtcNow;
        return new
        {
            GeneratedUtc = now,
            IsRunning,
            Summary = _lastSummary,
            WatchBeaconUrl = _watch.BeaconUrl,
            WatchTransport = _watch.CurrentTransport,
            WatchSelfHeal = new { Rotating = _rotating, Exhausted = _selfHealExhausted, OutageActive = _outageActive },
            Active = new
            {
                Channel = ActiveChannel?.DisplayName,
                Game = ActiveCampaign?.Game.Name,
                Campaign = ActiveCampaign?.Name,
                Drop = ActiveDrop?.Name,
            },
            Override = new
            {
                Active = _forcedCampaignId is not null,
                CampaignId = _forcedCampaignId,
                DropOnly = _forcedDropOnly,
                Campaign = _campaigns.FirstOrDefault(c => string.Equals(c.Id, _forcedCampaignId, StringComparison.OrdinalIgnoreCase))?.Name,
            },
            LastClaimSweepUtc = _lastClaimSweep,
            Settings = new
            {
                Settings.EndingSoonest, Settings.AvailabilityPriority, Settings.PriorityOnly,
                Settings.HarvestImpossibleDrops, Settings.EnableBadgesEmotes,
                Priority = Settings.PriorityGames, Excluded = Settings.ExcludedGames,
                Dedupe = Settings.DedupeGames, HarvestUnlinked = Settings.HarvestUnlinkedGames,
            },
            // live drops-enabled streamer count per game from the last gather, driving availability ordering
            LiveStreamersByGame = _liveCountByGame.OrderBy(kv => kv.Value)
                .Select(kv => new { GameId = kv.Key, LiveStreamers = kv.Value }).ToList(),
            ClaimHistoryCount = _claimedBenefits.Count,
            ClaimHistory = _claimedBenefits.OrderBy(kv => kv.Key)
                .Select(kv => new { RewardId = kv.Key, AwardedAt = kv.Value }).ToList(),
            SkippedDrops = _skipDrops.Count,
            BenchedChannels = _channelCooldownUntil
                .Where(kv => kv.Value > now).Select(kv => new { Login = kv.Key, Until = kv.Value }).ToList(),
            HarvestableGames,
            // ALL candidate campaigns in harvesting order (ignoring any override, so the snapshot shows the
            // full picture even while one campaign is force-harvested), each with the harvester's decisions
            Campaigns = CandidateCampaignsRaw().Select(c => new
            {
                c.Id, c.Name, Game = c.Game.Name, c.StartsAt, c.EndsAt, c.Linked,
                c.AllowedChannels,
                LiveStreamers = _liveCountByGame.TryGetValue(c.Game.Id, out var lc) ? lc : (int?)null,
                Eligible = IsEligible(c),
                BlockReason = HarvestBlockReason(c),
                FinishedForHarvesting = IsCampaignFinishedForHarvesting(c),
                Drops = c.Drops.OrderBy(d => d.RequiredMinutes).Select(d => new
                {
                    d.Name, d.RequiredMinutes, d.CurrentMinutes, d.IsClaimed, d.IsComplete,
                    d.RequiredSubs, d.CurrentSubs, d.RequiresSubscription, d.SubRequirementMet,
                    ClaimedThisCampaign = IsClaimedThisCampaign(d),
                    LedgerClaimed = WeClaimedDrop(d), // per-tier ledger (survives self lag)
                    GivenUp = IsSkipped(d.Id),
                    Benefits = d.Benefits.Select(b => new
                    {
                        b.Id, b.MatchKey, b.Name,
                        ClaimedAt = _claimedBenefits.TryGetValue(b.MatchKey, out var t) ? t : null,
                        GrantedByCampaigns = _benefitWindows.TryGetValue(b.MatchKey, out var w) ? w.Count : 0,
                        AttributedHere = ClaimBelongsTo(b.MatchKey, c),
                    }).ToList(),
                }).ToList(),
            }).ToList(),
        };
    }

    /// <summary>Dumps EVERY discovered campaign (not just the harvestable candidates in the main snapshot),
    /// each with why it is / isn't a harvesting candidate, so a campaign the harvester is skipping or
    /// treating as finished can be inspected even when it's filtered out of /snapshot.</summary>
    /// <returns>A serializable object listing all campaigns and their drops.</returns>
    public object DebugAllCampaigns()
    {
        return new
        {
            GeneratedUtc = DateTimeOffset.UtcNow,
            TotalCampaigns = _campaigns.Count,
            Campaigns = _campaigns
                .OrderBy(c => c.Game.Name).ThenBy(c => c.Name)
                .Select(c => new
                {
                    c.Id, c.Name, Game = c.Game.Name,
                    Status = c.Status.ToString(),
                    c.IsActive,
                    IsFinished = c.IsFinished,                       // model: all drops claimed/complete
                    FinishedForHarvesting = IsCampaignFinishedForHarvesting(c),
                    // why it is / isn't in the harvestable candidate set (/snapshot)
                    InCandidateSet = c.IsActive && !c.IsFinished,
                    BlockReason = HarvestBlockReason(c),
                    Drops = c.Drops.OrderBy(d => d.RequiredMinutes).Select(d => new
                    {
                        d.Name, d.RequiredMinutes, d.CurrentMinutes, d.IsClaimed, d.IsComplete,
                        d.RequiredSubs, d.CurrentSubs, d.SubRequirementMet,
                        LedgerClaimed = WeClaimedDrop(d),
                        ClaimedThisCampaign = IsClaimedThisCampaign(d),
                    }).ToList(),
                }).ToList(),
        };
    }
}
