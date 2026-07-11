using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DropHarvester.Localization;
using DropHarvester.Models;
using DropHarvester.Models.Events;
using DropHarvester.Models.Twitch;
using DropHarvester.Services;
using DropHarvester.Services.Twitch;

namespace DropHarvester.ViewModels;

/// <summary>
/// The Inventory tab: fetches every drop campaign from Twitch and shows them with their drops
/// and progress, filterable by status/link/finished.
/// </summary>
public partial class CampaignsViewModel : ObservableViewModel
{
    readonly IInventoryService _inventory;
    readonly ITwitchAuth _auth;
    readonly ISettingsStore _settings;
    readonly IHarvesterEventBus _bus;
    readonly IHarvesterOrchestrator _harvester;
    readonly IClaimLedger _ledger;

    readonly List<DropsCampaign> _all = new();
    Dictionary<string, DateTimeOffset?> _claimedBenefits = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, List<(DateTimeOffset start, DateTimeOffset end)>> _benefitWindows = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<DropsCampaign> Campaigns { get; } = new UiObservableCollection<DropsCampaign>();

    /// <summary>Wires up services, subscribes to harvester events, restores the saved drop layout, and
    /// starts the per-second countdown ticker.</summary>
    /// <param name="inventory">fetches drop campaigns and claim history from Twitch.</param>
    /// <param name="auth">current Twitch login state.</param>
    /// <param name="settings">persisted app settings store.</param>
    /// <param name="bus">harvester event bus, subscribed for live progress and claim events.</param>
    /// <param name="harvester">orchestrator queried for harvestable/finished campaign state.</param>
    /// <param name="ledger">local claim ledger that survives Twitch per-drop lag.</param>
    public CampaignsViewModel(IInventoryService inventory, ITwitchAuth auth, ISettingsStore settings,
        IHarvesterEventBus bus, IHarvesterOrchestrator harvester, IClaimLedger ledger)
    {
        _inventory = inventory;
        _auth = auth;
        _settings = settings;
        _bus = bus;
        _harvester = harvester;
        _ledger = ledger;
        _bus.Event += OnHarvesterEvent;
        _verticalView = settings.Settings.InventoryVerticalDrops; // restore the last-chosen drop layout
        _ = RunCountdownAsync();
    }

    /// <summary>Tick the actively-harvested campaign + its watched drops each second so their "time
    /// remaining" countdowns advance smoothly in the grid / list views.</summary>
    async Task RunCountdownAsync()
    {
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync())
        {
            // Snapshot + per-tick guard so a reload mutating _all mid-iterate can't kill the ticker.
            try
            {
                foreach (var c in _all.ToArray())
                    if (c.IsHarvesting)
                    {
                        c.Tick();
                        foreach (var d in c.Drops)
                            if (d.IsActivelyWatched) d.Tick();
                    }
            }
            catch { /* transient (e.g. inventory reload); resume next tick */ }
        }
    }

    // ----- Drop layout (persisted so the chosen view survives a restart) -----
    /// <summary>false = compact horizontal rows (default), true = large vertical cards (wrap, max 5/row).</summary>
    [ObservableProperty] private bool _verticalView;

    public bool HorizontalView => !VerticalView;

    /// <summary>Label for the toggle button - names the view it switches TO.</summary>
    public string ViewToggleText => VerticalView ? Loc.T("Campaigns_ListView") : Loc.T("Campaigns_GridView");

    /// <summary>Persists the chosen drop layout and refreshes its dependent view properties.</summary>
    /// <param name="value">the new VerticalView value.</param>
    partial void OnVerticalViewChanged(bool value)
    {
        OnPropertyChanged(nameof(HorizontalView));
        OnPropertyChanged(nameof(ViewToggleText));
        _settings.Settings.InventoryVerticalDrops = value;
        _settings.Save();
    }

    /// <summary>Toggles between the grid and list drop layouts.</summary>
    [RelayCommand]
    void ToggleView() => VerticalView = !VerticalView;

    /// <summary>Opens the subscribe page for a sub-only drop (its campaign's subscribe URL), since these
    /// can't be harvested by watching - the user earns them by subscribing.</summary>
    /// <param name="url">The subscribe URL to open.</param>
    [RelayCommand]
    async Task BuyDrop(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return;
        try { await Launcher.Default.OpenAsync(url); } catch { }
    }

    /// <summary>Publishes an info-level log line to the harvester event bus.</summary>
    /// <param name="message">the text to log.</param>
    void Log(string message) => _bus.Publish(new LogEvent(message, HarvesterLogLevel.Info));

    // The campaign the harvester is currently watching (remembered so the "Harvesting" badge survives a
    // reload - a fresh fetch builds new objects with IsHarvesting=false otherwise).
    string? _harvestingCampaignId;

    /// <summary>Tracks which campaign is harvested for the badge, and mirrors live progress/claim events
    /// onto the Inventory's own drop copies.</summary>
    /// <param name="e">the harvester event to handle.</param>
    void OnHarvesterEvent(HarvesterEvent e)
    {
        switch (e)
        {
            // Which campaign is being harvested (for the "Harvesting" badge).
            case ActiveTargetEvent t:
                _harvestingCampaignId = t.Campaign?.Id;
                MainThread.BeginInvokeOnMainThread(ApplyHarvestingFlag);
                break;
            // Live watch-progress / claim: mirror it onto the Inventory's copy so the bar advances
            // without a manual refresh (the harvester updates its own objects, not these).
            case DropProgressEvent p:
                MainThread.BeginInvokeOnMainThread(() => MirrorProgress(p.Drop, claimed: false));
                break;
            case DropClaimedEvent dc:
                MainThread.BeginInvokeOnMainThread(() => MirrorProgress(dc.Drop, claimed: true));
                QueueInventoryReload(); // a claim can unlock the next drop / update claim history
                break;
        }
    }

    bool _reloadQueued;
    /// <summary>Coalesces a burst of claims into one full inventory reload a few seconds later, so
    /// newly-unlocked drops and refreshed claim history appear without a manual refresh.</summary>
    void QueueInventoryReload()
    {
        if (_reloadQueued) return;
        _reloadQueued = true;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(3)); } catch { }
            _reloadQueued = false;
            if (!IsLoading && _auth.IsLoggedIn)
                await LoadAsync();
        });
    }

    /// <summary>Flag the currently-harvested campaign (by id) so the Inventory shows a "Harvesting" badge.
    /// Re-applied after every load so a refresh doesn't drop the badge.</summary>
    void ApplyHarvestingFlag()
    {
        foreach (var c in _all)
        {
            var harvesting = _harvestingCampaignId is not null
                && string.Equals(c.Id, _harvestingCampaignId, StringComparison.OrdinalIgnoreCase);
            c.IsHarvesting = harvesting;
            // Only the harvested campaign's unclaimed drops tick their per-second countdown.
            foreach (var d in c.Drops)
                d.IsActivelyWatched = harvesting && !d.IsClaimed && !d.IsComplete;
        }
    }

    /// <summary>Copy live watch-progress from the harvester's drop instance onto the matching Inventory
    /// drop (they're separate objects from different queries), so the progress bar advances without a
    /// manual refresh. Only re-filters when the drop crosses into done, so the list stays stable while
    /// a bar merely ticks up.</summary>
    /// <param name="source">the harvester's drop instance carrying the fresh progress/claim state.</param>
    /// <param name="claimed">true when this update is a claim, forcing the drop to show as claimed.</param>
    void MirrorProgress(TimedDrop source, bool claimed)
    {
        foreach (var c in _all)
        {
            var d = c.Drops.FirstOrDefault(x => string.Equals(x.Id, source.Id, StringComparison.OrdinalIgnoreCase));
            if (d is null)
                continue;

            var wasFinished = BucketOf(c) == CampaignBucket.Finished;
            d.RealCurrentMinutes = source.CurrentMinutes;
            d.ExtraCurrentMinutes = 0;
            if (source.ClaimId is not null) d.ClaimId = source.ClaimId;
            if (claimed || source.IsClaimed) d.IsClaimed = true;
            if (!wasFinished && BucketOf(c) == CampaignBucket.Finished)
                ApplyFilters(); // last reward just claimed -> let it move to the Finished filter
            return;
        }
    }

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _loadStatus = "";

    // Filters (default view: current, linked, unfinished).
    [ObservableProperty] private bool _showUpcoming = true;
    [ObservableProperty] private bool _showExpired;
    [ObservableProperty] private bool _showNotLinked;
    [ObservableProperty] private bool _showFinished;
    [ObservableProperty] private bool _showExcluded;
    [ObservableProperty] private bool _showDeduped;
    // Opt-in: campaigns whose every reward requires a paid sub are hidden until this is checked.
    [ObservableProperty] private bool _showSubOnly;

    /// <summary>Re-applies the filters when the ShowUpcoming toggle changes.</summary>
    /// <param name="value">the new toggle value.</param>
    partial void OnShowUpcomingChanged(bool value) => ApplyFilters();
    /// <summary>Re-applies the filters when the ShowExpired toggle changes.</summary>
    /// <param name="value">the new toggle value.</param>
    partial void OnShowExpiredChanged(bool value) => ApplyFilters();
    /// <summary>Re-applies the filters when the ShowNotLinked toggle changes.</summary>
    /// <param name="value">the new toggle value.</param>
    partial void OnShowNotLinkedChanged(bool value) => ApplyFilters();
    /// <summary>Re-applies the filters when the ShowFinished toggle changes.</summary>
    /// <param name="value">the new toggle value.</param>
    partial void OnShowFinishedChanged(bool value) => ApplyFilters();
    /// <summary>Re-applies the filters when the ShowExcluded toggle changes.</summary>
    /// <param name="value">the new toggle value.</param>
    partial void OnShowExcludedChanged(bool value) => ApplyFilters();
    /// <summary>Re-applies the filters when the ShowDeduped toggle changes.</summary>
    /// <param name="value">the new toggle value.</param>
    partial void OnShowDedupedChanged(bool value) => ApplyFilters();
    /// <summary>Re-applies the filters when the Sub-Only toggle changes.</summary>
    /// <param name="value">the new toggle value.</param>
    partial void OnShowSubOnlyChanged(bool value) => ApplyFilters();

    /// <summary>Called on first appearance; loads once if empty.</summary>
    public async Task EnsureLoadedAsync()
    {
        if (_all.Count == 0 && !IsLoading && _auth.IsLoggedIn)
            await LoadAsync();
    }

    /// <summary>Reloads the inventory from Twitch.</summary>
    [RelayCommand]
    async Task RefreshAsync() => await LoadAsync();

    /// <summary>Fetches all drop campaigns and claim history from Twitch, reconciles claimed drops,
    /// then re-applies the filters and the Harvesting badge.</summary>
    async Task LoadAsync()
    {
        if (!_auth.IsLoggedIn)
        {
            LoadStatus = Loc.T("Inventory_LogInFirst");
            return;
        }
        if (IsLoading) return;

        IsLoading = true;
        LoadStatus = Loc.T("Inventory_LoadingCampaigns");
        Log("Loading inventory...");
        try
        {
            var progress = new Progress<(int done, int total)>(p =>
                LoadStatus = Loc.T("Inventory_LoadingDrops", p.done, p.total));
            var campaigns = await _inventory.FetchInventoryAsync(progress);
            // Load the claim history (how we mark a drop "done" for the Finished filter even when
            // Twitch's per-drop self flag lags). Keep the previous history if this read comes back empty
            // (a transient/rate-limited failure returns an empty map; the history only ever grows).
            var claimed = await _inventory.GetClaimedBenefitsAsync();
            if (claimed.Count > 0)
            {
                _claimedBenefits = claimed;
                _ledger.RecordAll(claimed); // grow the permanent local ledger from Twitch's history
            }
            // Fold in our local ledger so claimed drops survive Twitch lag / an empty read.
            foreach (var kv in _ledger.All)
                if (!_claimedBenefits.ContainsKey(kv.Key))
                    _claimedBenefits[kv.Key] = kv.Value;

            _all.Clear();
            _all.AddRange(campaigns
                .DistinctBy(c => c.Id, StringComparer.OrdinalIgnoreCase) // no duplicate rows in any filter
                .OrderByDescending(c => c.Status == CampaignStatus.Active)
                .ThenBy(c => c.EndsAt));
            _benefitWindows = DropsCampaign.BenefitWindows(_all); // to attribute claims to campaigns
            ReconcileClaimedDisplay(); // show claimed-but-self-lagging drops as claimed, not 0/xxx in the default view
            ApplyFilters();
            ApplyHarvestingFlag(); // re-apply the "Harvesting" badge to the freshly-loaded objects
            LoadStatus = _all.Count == 0 ? Loc.T("Inventory_None") : Loc.T("Inventory_Loaded", _all.Count);
            Log($"Inventory loaded: {_all.Count} campaign(s).");
        }
        catch (Exception ex)
        {
            LoadStatus = Loc.T("Inventory_LoadFailed", ex.Message);
            Log($"Inventory load failed: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Rebuilds the visible Campaigns collection from the full list per the current filter toggles.</summary>
    void ApplyFilters()
    {
        var excluded = _settings.Settings.ExcludedGames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var view = _all.Where(c =>
        {
            // Sub-only campaigns (nothing earnable by watching) live ONLY under the opt-in Sub-Only filter,
            // so they don't clutter the normal view with rewards that cost money.
            if (c.IsSubscriptionOnly)
                return ShowSubOnly;

            // Each campaign is in exactly one bucket (Expired / Finished / Upcoming) and its checkbox
            // governs whether it shows. Finished means every reward is actually CLAIMED - a 100%-watched-
            // but-unclaimed campaign is still actionable and stays under Upcoming.
            switch (BucketOf(c))
            {
                case CampaignBucket.Expired: if (!ShowExpired) return false; break;
                case CampaignBucket.Finished: if (!ShowFinished) return false; break;
                default: if (!ShowUpcoming) return false; break; // Upcoming: not started, or still unclaimed
            }
            if (!c.Linked && !c.HasBadgeOrEmote && !ShowNotLinked) return false;
            if (excluded.Contains(c.Game.Name) && !ShowExcluded) return false;
            if (IsDeduped(c) && !ShowDeduped) return false;
            return true;
        });

        // Collapse repeated same game+name rows: Twitch lists every recurring (weekly/seasonal) drop and
        // region variant as its own campaign id, so a same-named campaign piles up. Keep one row per
        // (bucket, game, name) - the one being harvested if any, otherwise the most recent.
        var collapsed = view
            .GroupBy(CollapseKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(IsHarvestingCampaign).ThenByDescending(c => c.EndsAt).First());

        Campaigns.Clear();
        foreach (var c in collapsed.OrderByDescending(c => c.Status == CampaignStatus.Active).ThenBy(c => c.EndsAt))
            Campaigns.Add(c);
    }

    /// <summary>Row-collapsing key: same bucket (Expired / Finished / Upcoming) + game + name folds into
    /// one row, so a recurring or region-variant campaign shows once instead of many times.</summary>
    /// <param name="c">The campaign to key.</param>
    string CollapseKey(DropsCampaign c) => $"{BucketOf(c)} | {c.Game.Name} | {c.Name}";

    /// <summary>The display bucket a campaign belongs to (exactly one): Expired = past its end date,
    /// claimed or not; Finished = every reward actually CLAIMED; Upcoming = anything else (not started
    /// yet, or still has an unclaimed reward - i.e. still actionable).</summary>
    enum CampaignBucket { Upcoming, Finished, Expired }

    /// <summary>Classifies a campaign into its display bucket. Expiry wins first; then Finished only when
    /// the harvester wouldn't harvest it AND every reward is claimed; otherwise Upcoming. A drop that's 100%
    /// watched but unclaimed keeps the campaign in Upcoming, never Finished.</summary>
    /// <param name="c">the campaign to classify.</param>
    CampaignBucket BucketOf(DropsCampaign c)
    {
        if (c.Status == CampaignStatus.Expired)
            return CampaignBucket.Expired;
        // never file the campaign being harvested, or one the harvester would still harvest, under Finished - a
        // reused reward id owned from a past run (SMITE2 "Market Coins") must not mark a still-earnable
        // campaign done. "All claimed" comes from the harvester's ledger, not just Twitch's self.isClaimed,
        // which can lag at 0/unclaimed for minutes after we've actually claimed (the Albion case).
        if (!IsHarvestingCampaign(c) && !_harvester.IsCampaignHarvestable(c.Id) && _harvester.AreAllRewardsClaimed(c.Id))
            return CampaignBucket.Finished;
        return CampaignBucket.Upcoming;
    }

    /// <summary>Whether this is the campaign the harvester is actively watching right now.</summary>
    /// <param name="c">the campaign to test.</param>
    bool IsHarvestingCampaign(DropsCampaign c)
        => _harvestingCampaignId is not null && string.Equals(c.Id, _harvestingCampaignId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Mark drops claimed THIS campaign but whose Twitch per-drop self.isClaimed still lags
    /// (shows 0/xxx) as claimed on the card - so a claimed drop shows "Claimed" instead of sitting in the
    /// default view at 0 minutes, and a fully-claimed campaign correctly reads as Finished. Only
    /// unique-reward drops reconcile (ClaimedThisRun exempts rewards shared by multiple drops), so a
    /// partly-claimed tiered campaign (R6S "Esports Pack" 1h/3h/6h/9h) is NOT prematurely marked done -
    /// its unclaimed tiers keep showing progress and it stays OUT of Finished.</summary>
    void ReconcileClaimedDisplay()
    {
        foreach (var c in _all)
        {
            // Show a drop as claimed when our claim history attributes its reward to THIS campaign, OR the
            // harvester's per-tier ledger recorded it claimed (the truth for recurring campaigns whose
            // reused reward id makes the history-time attribution ambiguous - the Albion case), even if
            // Twitch's per-drop self still lags at 0/xxx. A drop merely watched to 100% but not yet claimed
            // stays unclaimed (actionable), never shown as claimed.
            foreach (var d in c.Drops)
                if (!d.IsClaimed && (ClaimedThisRun(c, d) || _harvester.WasDropClaimed(d.Id)))
                    d.IsClaimed = true;
        }
    }

    /// <summary>The drop was claimed in THIS campaign: its reward was awarded within this campaign's
    /// window, and this is the only granting campaign whose window contains the claim time (so a reward
    /// shared with a concurrent campaign stays ambiguous, and one owned only from a past run - awarded
    /// before this started - is not counted).</summary>
    /// <param name="c">the campaign the drop belongs to.</param>
    /// <param name="d">the drop whose claim attribution is being tested.</param>
    bool ClaimedThisRun(DropsCampaign c, TimedDrop d)
        => d.Benefits.Any(b =>
        {
            // A reward listed on several tier-drops in the SAME campaign (e.g. R6S "Esports Pack" at
            // 1h/3h/6h/9h - separate claimable drops sharing a name + id) normally can't be attributed to
            // one tier from the claim history (one award time per reward id), so we fall back to per-drop
            // self. EXCEPTION: when self shows NOTHING on any of those tiers (all 0/unclaimed) yet the
            // reward was awarded in-window, self has fully lagged - the tiers are one bundle claimed once
            // (Marbles' "15 Community Coins" repeated across tiers), so the history is the truth.
            if (RewardSharedInCampaign(c, b.MatchKey) && !RewardSelfUntracked(c, b.MatchKey))
                return false;
            if (!_claimedBenefits.TryGetValue(b.MatchKey, out var awarded) || awarded is not { } t)
                return false;
            if (t < c.StartsAt || t > c.EndsAt.AddHours(24))
                return false;
            return !_benefitWindows.TryGetValue(b.MatchKey, out var windows)
                || windows.Count(w => t >= w.start && t <= w.end.AddHours(24)) <= 1;
        });

    /// <summary>More than one of the campaign's drops grants this reward id (time-tiered drops sharing
    /// a reward - the claim history, one time per id, can't say which tier was claimed).</summary>
    /// <param name="c">the campaign whose drops are checked.</param>
    /// <param name="matchKey">the reward match key to count across drops.</param>
    static bool RewardSharedInCampaign(DropsCampaign c, string matchKey)
        => c.Drops.Count(x => x.Benefits.Any(b => string.Equals(b.MatchKey, matchKey, StringComparison.OrdinalIgnoreCase))) > 1;

    /// <summary>Every tier granting this reward shows no per-drop self signal (unclaimed, not complete,
    /// 0 minutes). Distinguishes a fully-lagged claimed-once bundle (Marbles) from R6S Esports tiers that
    /// self correctly tracks - see the harvester's RewardSelfUntracked.</summary>
    /// <param name="c">the campaign whose drops are checked.</param>
    /// <param name="matchKey">the reward match key whose granting tiers are inspected.</param>
    static bool RewardSelfUntracked(DropsCampaign c, string matchKey)
        => c.Drops.Where(d => d.Benefits.Any(b => string.Equals(b.MatchKey, matchKey, StringComparison.OrdinalIgnoreCase)))
                  .All(d => !d.IsClaimed && !d.IsComplete && d.CurrentMinutes == 0);

    /// <summary>A campaign is "de-duped" when its game is on the de-dupe list and every one of its
    /// drops' rewards is already owned (in Twitch's claimed-drop history).</summary>
    /// <param name="c">the campaign to test.</param>
    bool IsDeduped(DropsCampaign c)
    {
        if (_claimedBenefits.Count == 0
            || !_settings.Settings.DedupeGames.Contains(c.Game.Name, StringComparer.OrdinalIgnoreCase))
            return false;
        // A reward shared by 2+ drops in the campaign is repeatable (Marbles-style / tiered) and stays
        // claimable, so it never counts as "owned" for de-dupe - matches the harvester's IsAlreadyOwned.
        return c.Drops.Count > 0
            && c.Drops.All(d => d.Benefits.Any(b => !RewardSharedInCampaign(c, b.MatchKey)
                                                    && _claimedBenefits.ContainsKey(b.MatchKey)));
    }
}
