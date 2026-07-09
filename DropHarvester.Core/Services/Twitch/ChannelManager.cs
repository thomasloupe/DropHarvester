using System.Text;
using System.Text.Json;
using DropHarvester.Models.Twitch;

namespace DropHarvester.Services.Twitch;

/// <summary>
/// Finds live, drops-enabled channels for a game and refreshes a channel's live state (online,
/// viewers, broadcast id).
/// </summary>
public interface IChannelManager
{
    /// <summary>Live channels streaming the game with drops enabled, best (most viewers) first.</summary>
    /// <param name="game">Game to search the directory for.</param>
    /// <param name="limit">Max channels to return.</param>
    /// <param name="ct">Token to cancel the query.</param>
    Task<IReadOnlyList<TwitchChannel>> FetchLiveChannelsForGameAsync(Game game, int limit = 30, CancellationToken ct = default);

    /// <summary>Refresh a channel's online/viewers/broadcast-id from its current stream.</summary>
    /// <param name="channel">Channel whose live state is refreshed in place.</param>
    /// <param name="ct">Token to cancel the query.</param>
    Task RefreshChannelAsync(TwitchChannel channel, CancellationToken ct = default);
}

/// <summary>Default <see cref="IChannelManager"/> backed by Twitch's private GraphQL API.</summary>
public sealed class ChannelManager : IChannelManager
{
    readonly IGqlClient _gql;

    /// <summary>Creates the manager with the GraphQL client it queries through.</summary>
    /// <param name="gql">GraphQL client used for directory and stream-info queries.</param>
    public ChannelManager(IGqlClient gql) => _gql = gql;

    /// <summary>Queries the game directory for live drops-enabled channels, sorted by viewer count.</summary>
    /// <param name="game">Game to search the directory for.</param>
    /// <param name="limit">Max channels to return.</param>
    /// <param name="ct">Token to cancel the query.</param>
    /// <returns>Live drops-enabled channels; empty on error or when no slug can be resolved.</returns>
    public async Task<IReadOnlyList<TwitchChannel>> FetchLiveChannelsForGameAsync(Game game, int limit = 30, CancellationToken ct = default)
    {
        // Some campaign responses omit the game slug; derive it from the name so the directory query
        // still works (Twitch slugs are the lowercased name with spaces -> hyphens, punctuation dropped).
        var slug = string.IsNullOrEmpty(game.Slug) ? SlugFromName(game.Name) : game.Slug;
        if (string.IsNullOrEmpty(slug))
            return Array.Empty<TwitchChannel>();

        var variables = new
        {
            limit,
            slug,
            sortTypeIsRecency = false,
            includeCostreaming = false,
            options = new
            {
                includeRestricted = new[] { "SUB_ONLY_LIVE" },
                systemFilters = new[] { "DROPS_ENABLED" },
                sort = "VIEWER_COUNT",
            },
        };

        JsonElement root;
        try
        {
            root = await _gql.PersistedAsync(
                "DirectoryPage_Game", TwitchConstants.Gql.DirectoryPageGame, variables, ct).ConfigureAwait(false);
        }
        catch (GqlAuthException)
        {
            throw;
        }
        catch
        {
            return Array.Empty<TwitchChannel>();
        }

        var edges = root.Path("data", "game", "streams")?.Prop("edges")?.AsArray()
            ?? Enumerable.Empty<JsonElement>();

        var channels = new List<TwitchChannel>();
        foreach (var edge in edges)
        {
            var node = edge.Prop("node");
            if (node is null)
                continue;
            var n = node.Value;

            var broadcaster = n.Prop("broadcaster");
            var login = broadcaster?.Str("login");
            var id = broadcaster?.Str("id");
            if (string.IsNullOrEmpty(login) || string.IsNullOrEmpty(id))
                continue;

            channels.Add(new TwitchChannel
            {
                Id = id!,
                Login = login!,
                DisplayName = broadcaster?.Str("displayName") ?? login!,
                Online = true,
                ViewerCount = n.IntOr("viewersCount"),
                BroadcastId = n.Str("id"),
                Game = ParseGame(n.Prop("game")) ?? game,
                DropsEnabled = true, // filtered by DROPS_ENABLED
            });
        }

        return channels;
    }

    /// <summary>Refreshes the channel's online state, viewers, broadcast id, and game from its stream.</summary>
    /// <param name="channel">Channel updated in place on the UI thread.</param>
    /// <param name="ct">Token to cancel the query.</param>
    public async Task RefreshChannelAsync(TwitchChannel channel, CancellationToken ct = default)
    {
        JsonElement root;
        try
        {
            root = await _gql.PersistedAsync(
                "VideoPlayerStreamInfoOverlayChannel", TwitchConstants.Gql.VideoPlayerStreamInfoOverlayChannel,
                new { channel = channel.Login }, ct).ConfigureAwait(false);
        }
        catch (GqlAuthException)
        {
            throw;
        }
        catch
        {
            return;
        }

        var user = root.Path("data", "user");
        var stream = user?.Prop("stream");
        // Channels resolved from an allow-list arrive with only a login; capture the id here so the
        // watch heartbeat (which needs channel_id) and websocket topics work for them too.
        var userId = user?.Str("id");

        // The channel is bound to the Channels tab, so mutate its observable properties on the UI
        // thread (off-thread mutation triggers WinUI's cross-thread failfast).
        if (stream is null)
        {
            await UiDispatch.Current.InvokeAsync(() =>
            {
                if (string.IsNullOrEmpty(channel.Id) && !string.IsNullOrEmpty(userId))
                    channel.Id = userId!;
                channel.Online = false;
                channel.BroadcastId = null;
                channel.ViewerCount = 0;
            }).ConfigureAwait(false);
            return;
        }

        var broadcastId = stream.Value.Str("id");
        var viewers = stream.Value.IntOr("viewersCount");
        var game = ParseGame(stream.Value.Prop("game"));
        await UiDispatch.Current.InvokeAsync(() =>
        {
            if (string.IsNullOrEmpty(channel.Id) && !string.IsNullOrEmpty(userId))
                channel.Id = userId!;
            channel.Online = true;
            channel.BroadcastId = broadcastId;
            channel.ViewerCount = viewers;
            if (game is not null)
                channel.Game = game;
        }).ConfigureAwait(false);
    }

    /// <summary>Best-effort Twitch category slug from a display name (fallback when the API omits it).</summary>
    /// <param name="name">Game display name to slugify.</param>
    /// <returns>Lowercased, hyphenated slug with punctuation dropped.</returns>
    static string SlugFromName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (ch is ' ' or '-' or '_' or '.') sb.Append('-');
            // apostrophes, colons, etc. are dropped, matching Twitch's slug rules
        }
        var s = sb.ToString();
        while (s.Contains("--")) s = s.Replace("--", "-");
        return s.Trim('-');
    }

    /// <summary>Parses a Game from a GraphQL game element, or null when id/name are missing.</summary>
    /// <param name="gel">GraphQL game element, may be null.</param>
    /// <returns>The parsed game, or null when the element is null or lacks id/name.</returns>
    static Game? ParseGame(JsonElement? gel)
    {
        if (gel is not { } g)
            return null;
        var id = g.Str("id");
        var name = g.Str("displayName") ?? g.Str("name");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
            return null;
        return new Game { Id = id, Name = name, Slug = g.Str("slug") };
    }
}
