using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DropHarvester.Models;
using DropHarvester.Models.Twitch;
using DropHarvester.Services.Twitch;

namespace DropHarvester.ViewModels;

/// <summary>
/// Channels tab: the drops-enabled channels for every game currently being harvested, grouped under a
/// collapsible per-game header (in harvesting order), each channel switchable via "Watch". The channel
/// list itself is the harvester's <see cref="IHarvesterOrchestrator.TrackedChannels"/> (display-only - it
/// reflects the harvesting order, it does not decide what's harvested).
/// </summary>
public partial class ChannelsViewModel : ObservableViewModel
{
    readonly IHarvesterOrchestrator _harvester;
    // Remember each game's expand/collapse state across list rebuilds (keyed by game name).
    readonly Dictionary<string, bool> _expandedByGame = new(StringComparer.OrdinalIgnoreCase);
    bool _rebuildQueued;

    public ObservableCollection<ChannelGroup> Groups { get; } = new UiObservableCollection<ChannelGroup>();

    /// <summary>True while the channel list is being (re)gathered - shows a spinner.</summary>
    [ObservableProperty] private bool _isRefreshing;

    /// <summary>Live channel/game totals, shown in the header and updated as the list fills in.</summary>
    [ObservableProperty] private string _countText = "";

    /// <summary>Subscribes to the harvester's tracked-channel and refresh-state changes and builds the initial groups.</summary>
    /// <param name="harvester">Harvester orchestrator supplying the tracked channels and harvesting order.</param>
    public ChannelsViewModel(IHarvesterOrchestrator harvester)
    {
        _harvester = harvester;
        _harvester.TrackedChannels.CollectionChanged += OnTrackedChannelsChanged;
        _harvester.ChannelRefreshStateChanged += () =>
            MainThread.BeginInvokeOnMainThread(() => IsRefreshing = _harvester.IsRefreshingChannels);
        RebuildGroups();
    }

    // The tracked list is rebuilt as Clear()+many Add()s; coalesce those into a single regroup that
    // runs after the batch, instead of regrouping once per Add.
    /// <summary>Coalesces a burst of tracked-channel changes into a single deferred regroup on the UI thread.</summary>
    /// <param name="sender">The tracked-channels collection raising the change.</param>
    /// <param name="e">The collection-changed arguments.</param>
    void OnTrackedChannelsChanged(object? sender, EventArgs e)
    {
        if (_rebuildQueued)
            return;
        _rebuildQueued = true;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _rebuildQueued = false;
            RebuildGroups();
        });
    }

    /// <summary>Rebuilds the per-game channel groups in place (order, membership, expand state, and header count) from the harvester's tracked channels.</summary>
    void RebuildGroups()
    {
        var byGame = new Dictionary<string, List<TwitchChannel>>(StringComparer.OrdinalIgnoreCase);
        foreach (var ch in _harvester.TrackedChannels)
        {
            var name = ch.Game?.Name ?? DropHarvester.Localization.Loc.T("Channels_UnknownGame");
            if (!byGame.TryGetValue(name, out var list))
            {
                list = new List<TwitchChannel>();
                byGame[name] = list;
            }
            list.Add(ch);
        }

        // Group ORDER = the harvester's harvestable-game list (harvesting order), so EVERY harvestable game shows as
        // a group even when it has no live channel right now. Then any leftover games that have
        // channels but somehow aren't in that list.
        var order = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in _harvester.HarvestableGames)
            if (seen.Add(g)) order.Add(g);
        foreach (var g in byGame.Keys)
            if (seen.Add(g)) order.Add(g);

        // Reconcile the bound collections IN PLACE instead of Clear()+recreate. The progressive channel
        // refresh calls this many times in quick succession; rebuilding every group object each time
        // meant a group the user was expanding could be destroyed mid-layout -> WinUI crash. Preserving
        // the existing ChannelGroup objects (and only nudging channels/positions that changed) keeps the
        // item the user is interacting with alive.
        var wanted = new HashSet<string>(order, StringComparer.OrdinalIgnoreCase);
        for (var i = Groups.Count - 1; i >= 0; i--)
            if (!wanted.Contains(Groups[i].GameName))
                Groups.RemoveAt(i);

        for (var idx = 0; idx < order.Count; idx++)
        {
            var name = order[idx];
            var chans = byGame.TryGetValue(name, out var l) ? l : new List<TwitchChannel>();
            var group = FindGroup(name);
            if (group is null)
            {
                // Default: the game being watched starts open, the rest closed; user toggles remembered.
                var expanded = _expandedByGame.TryGetValue(name, out var e) ? e : chans.Any(c => c.IsActive);
                _expandedByGame[name] = expanded;
                group = new ChannelGroup(name) { IsExpanded = expanded };
                foreach (var c in chans)
                    group.Channels.Add(c);
                group.RefreshHeader();
                Groups.Insert(Math.Min(idx, Groups.Count), group);
            }
            else
            {
                ReconcileChannels(group.Channels, chans);
                group.RefreshHeader();
                var cur = Groups.IndexOf(group);
                if (cur != idx)
                    Groups.Move(cur, idx);
            }
        }

        // Header count, recomputed on every (progressive) rebuild so it climbs as channels arrive.
        var live = Groups.Sum(g => g.Channels.Count);
        var gamesWithChannels = Groups.Count(g => g.Channels.Count > 0);
        CountText = live == 0
            ? ""
            : DropHarvester.Localization.Loc.T("Channels_CountText", live, gamesWithChannels);
    }

    /// <summary>Finds the existing group for a game by name, case-insensitively.</summary>
    /// <param name="name">The game name to look up.</param>
    /// <returns>The matching group, or null when none exists.</returns>
    ChannelGroup? FindGroup(string name)
    {
        foreach (var g in Groups)
            if (string.Equals(g.GameName, name, StringComparison.OrdinalIgnoreCase))
                return g;
        return null;
    }

    // Minimal mutations, so the CollectionView/BindableLayout isn't torn down and rebuilt wholesale.
    /// <summary>Diffs a group's channel list in place - removing gone channels, inserting new ones, and reordering to match the desired list.</summary>
    /// <param name="target">The bound channel collection to mutate.</param>
    /// <param name="desired">The channels the collection should end up containing, in order.</param>
    static void ReconcileChannels(ObservableCollection<TwitchChannel> target, List<TwitchChannel> desired)
    {
        for (var i = target.Count - 1; i >= 0; i--)
            if (!desired.Contains(target[i]))
                target.RemoveAt(i);
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], desired[i]))
                continue;
            var existing = target.IndexOf(desired[i]);
            if (existing >= 0)
                target.Move(existing, i);
            else
                target.Insert(Math.Min(i, target.Count), desired[i]);
        }
        while (target.Count > desired.Count)
            target.RemoveAt(target.Count - 1);
    }

    /// <summary>Force an immediate refresh of the channel list (the tab's Refresh button).</summary>
    [RelayCommand]
    void Refresh() => _harvester.RequestChannelRefresh();

    /// <summary>Toggles a game group's expand/collapse state and remembers it for future rebuilds.</summary>
    /// <param name="group">The group to toggle; ignored when null.</param>
    [RelayCommand]
    void ToggleGroup(ChannelGroup? group)
    {
        if (group is null)
            return;
        group.IsExpanded = !group.IsExpanded;
        _expandedByGame[group.GameName] = group.IsExpanded;
    }

    /// <summary>Requests the harvester switch to the given channel, marking its row as pending for immediate feedback.</summary>
    /// <param name="channel">The channel to switch to; ignored when null.</param>
    [RelayCommand]
    void SwitchTo(TwitchChannel? channel)
    {
        if (channel is null)
            return;
        // Immediate feedback: hide this row's Watch button (and show "Switching...") right away, and
        // clear any other row's pending state, so it's obvious the click registered and can't be
        // double-fired. The actual switch happens on the harvester's next tick.
        foreach (var ch in _harvester.TrackedChannels)
            ch.PendingSwitch = ReferenceEquals(ch, channel);
        _harvester.RequestSwitchTo(channel);
    }

    // Right-click context menu: prefer (star) or avoid (no-entry) a channel. Toggles the persisted
    // list via the harvester, which flips the row's badge and logs the change.
    /// <summary>Toggles the "prefer" (star) flag on the given channel via the harvester's persisted list.</summary>
    /// <param name="channel">The channel to prefer/unprefer; ignored when null.</param>
    [RelayCommand]
    void PreferChannel(TwitchChannel? channel)
    {
        if (channel is not null)
            _harvester.TogglePreferChannel(channel);
    }

    /// <summary>Toggles the "avoid" (no-entry) flag on the given channel via the harvester's persisted list.</summary>
    /// <param name="channel">The channel to avoid/unavoid; ignored when null.</param>
    [RelayCommand]
    void AvoidChannel(TwitchChannel? channel)
    {
        if (channel is not null)
            _harvester.ToggleAvoidChannel(channel);
    }
}
