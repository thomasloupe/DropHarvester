using CommunityToolkit.Mvvm.ComponentModel;
using DropHarvester.Localization;

namespace DropHarvester.Models.Twitch;

/// <summary>
/// A trackable Twitch channel. Online/viewers/game/drops-enabled/active all change while harvesting,
/// so this is observable for direct binding in the Channels table.
/// </summary>
public partial class TwitchChannel : ObservableModel
{
    // Settable (not init-only): channels resolved from a campaign's allow-list start with only a
    // login, and their id is filled in once RefreshChannelAsync looks the channel up.
    public required string Id { get; set; }
    public required string Login { get; init; }

    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private bool _online;
    [ObservableProperty] private int _viewerCount;
    [ObservableProperty] private Game? _game;
    [ObservableProperty] private bool _dropsEnabled;
    [ObservableProperty] private string? _broadcastId;

    /// <summary>An "Official Campaign Channel": on a campaign's allow-list. Some campaigns only
    /// credit drops on these specific channels; others accept any participating channel.</summary>
    [ObservableProperty] private bool _isOfficial;

    /// <summary>The channel currently being watched (there is at most one).</summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>User right-click "Prefer": idle here when live and no official channel is required.</summary>
    [ObservableProperty] private bool _isPreferred;

    /// <summary>User right-click "Avoid": never idled on unless it's the only stream live.</summary>
    [ObservableProperty] private bool _isAvoided;

    // Context-menu labels flip between set/unset. Star emoji for prefer, no-entry for avoid.
    public string PreferMenuText => IsPreferred ? Loc.T("Model_RemovePreferred") : Loc.T("Model_PreferChannel");
    public string AvoidMenuText => IsAvoided ? Loc.T("Model_RemoveAvoided") : Loc.T("Model_AvoidChannel");

    /// <summary>The badge shown on the row: star if preferred, no-entry if avoided, else nothing.</summary>
    public string PrefAvoidGlyph => IsPreferred ? "⭐" : IsAvoided ? "\U0001F6AB" : "";
    public bool HasPrefAvoid => IsPreferred || IsAvoided;

    /// <summary>Set the instant the user clicks "Watch"; cleared once the switch takes effect (this
    /// channel becomes active) or the list is rebuilt. Gives immediate click feedback and prevents a
    /// double-click while the switch is in flight.</summary>
    [ObservableProperty] private bool _pendingSwitch;

    public string StatusText => Online ? Loc.T("Model_ChannelLive", ViewerCount.ToString("N0")) : Loc.T("Model_ChannelOffline");

    /// <summary>Show the "Watch" button only when this isn't the active channel and no switch is pending.</summary>
    public bool CanWatch => !IsActive && !PendingSwitch;

    /// <summary>Re-raise StatusText when the channel goes online or offline.</summary>
    /// <param name="value">True when the channel is now live.</param>
    partial void OnOnlineChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusText));
    }

    /// <summary>Re-raise StatusText when the viewer count changes.</summary>
    /// <param name="value">The new viewer count.</param>
    partial void OnViewerCountChanged(int value) => OnPropertyChanged(nameof(StatusText));

    /// <summary>Clear any pending switch and re-raise CanWatch when this becomes the active channel.</summary>
    /// <param name="value">True when this channel is now the one being watched.</param>
    partial void OnIsActiveChanged(bool value)
    {
        if (value) PendingSwitch = false; // became active -> the switch completed
        OnPropertyChanged(nameof(CanWatch));
    }

    /// <summary>Re-raise CanWatch when a switch to this channel starts or clears.</summary>
    /// <param name="value">True while a switch to this channel is in flight.</param>
    partial void OnPendingSwitchChanged(bool value) => OnPropertyChanged(nameof(CanWatch));

    // Prefer and Avoid are mutually exclusive: setting one clears the other.
    /// <summary>Clear the avoided flag and refresh the prefer/avoid labels when this channel is preferred.</summary>
    /// <param name="value">True when the channel is now preferred.</param>
    partial void OnIsPreferredChanged(bool value)
    {
        if (value) IsAvoided = false;
        OnPropertyChanged(nameof(PreferMenuText));
        OnPropertyChanged(nameof(PrefAvoidGlyph));
        OnPropertyChanged(nameof(HasPrefAvoid));
    }

    /// <summary>Clear the preferred flag and refresh the prefer/avoid labels when this channel is avoided.</summary>
    /// <param name="value">True when the channel is now avoided.</param>
    partial void OnIsAvoidedChanged(bool value)
    {
        if (value) IsPreferred = false;
        OnPropertyChanged(nameof(AvoidMenuText));
        OnPropertyChanged(nameof(PrefAvoidGlyph));
        OnPropertyChanged(nameof(HasPrefAvoid));
    }
}
