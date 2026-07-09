using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DropHarvester.Localization;
using DropHarvester.Models;
using DropHarvester.Models.Twitch;

namespace DropHarvester.ViewModels;

/// <summary>
/// One expandable/collapsible game row in the Channels tab: a parent header (game name + a
/// disclosure caret + a live count) with its channels as children, shown only while expanded.
/// Games are listed in harvesting order; channels within a game keep their viewer order.
/// </summary>
public partial class ChannelGroup : ObservableModel
{
    public string GameName { get; }

    /// <summary>Creates a collapsed group header for the given game.</summary>
    /// <param name="gameName">The game name shown in the header.</param>
    public ChannelGroup(string gameName) => GameName = gameName;

    public ObservableCollection<TwitchChannel> Channels { get; } = new UiObservableCollection<TwitchChannel>();

    [ObservableProperty] private bool _isExpanded;

    /// <summary>Disclosure caret: pointing down when open, right when closed.</summary>
    public string Caret => IsExpanded ? "▾" : "▸"; // down triangle / right triangle
    public int LiveCount => Channels.Count(c => c.Online);
    public string Summary => LiveCount == 1 ? Loc.T("Channels_OneLive") : Loc.T("Channels_NLive", LiveCount);

    /// <summary>Refreshes the disclosure caret when the expanded state changes.</summary>
    /// <param name="value">The new expanded state.</param>
    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(Caret));

    /// <summary>Recompute the header's derived labels (called after the child list is rebuilt).</summary>
    public void RefreshHeader()
    {
        OnPropertyChanged(nameof(Caret));
        OnPropertyChanged(nameof(LiveCount));
        OnPropertyChanged(nameof(Summary));
    }
}
