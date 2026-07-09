using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DropHarvester.Models.Twitch;

namespace DropHarvester.Services.Twitch;

/// <summary>
/// The stream-less "watch": instead of pulling video, it sends a single minute-watched analytics
/// event to Twitch every ~59s via the <c>sendSpadeEvents</c> GraphQL mutation, which is what
/// advances drop progress. The payload goes through GQL rather than the (blocked) Spade endpoint.
/// No video, no browser - a few hundred bytes every minute.
/// </summary>
public interface IWatchService
{
    /// <summary>Send one minute-watched heartbeat for the channel. Returns true on HTTP 204 ack.</summary>
    /// <param name="channel">Channel to credit the watch minute to.</param>
    /// <param name="ct">Token to cancel the send.</param>
    Task<bool> SendWatchAsync(TwitchChannel channel, CancellationToken ct = default);
}

/// <summary>Default <see cref="IWatchService"/> that sends minute-watched events via GraphQL.</summary>
public sealed class WatchService : IWatchService
{
    static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    readonly IGqlClient _gql;
    readonly ITwitchAuth _auth;

    /// <summary>Creates the watch service with its GraphQL client and auth.</summary>
    /// <param name="gql">GraphQL client used to send the spade-event mutation.</param>
    /// <param name="auth">Twitch auth supplying the logged-in user id.</param>
    public WatchService(IGqlClient gql, ITwitchAuth auth)
    {
        _gql = gql;
        _auth = auth;
    }

    /// <summary>Sends a minute-watched spade event for an online channel and reports the 204 ack.</summary>
    /// <param name="channel">Channel to credit the watch minute to.</param>
    /// <param name="ct">Token to cancel the send.</param>
    /// <returns>True when Twitch acks with status 204; false when offline or on failure.</returns>
    public async Task<bool> SendWatchAsync(TwitchChannel channel, CancellationToken ct = default)
    {
        if (!channel.Online || string.IsNullOrEmpty(channel.BroadcastId))
            return false;

        var userId = _auth.State.UserId ?? "";
        var evt = new[]
        {
            new Dictionary<string, object?>
            {
                ["event"] = "minute-watched",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["broadcast_id"] = channel.BroadcastId,
                    ["channel_id"] = channel.Id,
                    ["channel"] = channel.Login,
                    ["client_time"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    ["game"] = channel.Game?.Name ?? "",
                    ["game_id"] = channel.Game?.Id ?? "",
                    ["hidden"] = false,
                    ["is_live"] = true,
                    ["live"] = true,
                    ["logged_in"] = true,
                    ["minutes_logged"] = 1,
                    ["muted"] = false,
                    ["user_id"] = userId,
                },
            },
        };

        var encoded = EncodePayload(evt);

        try
        {
            // SendSpadeEventsInput: the base64(gzip(json)) blob plus the repository/encoding fields
            // Twitch needs to actually decode it. Omitting encoding=GZIP_B64 makes Twitch ack with
            // 204 but silently drop the event (no drop credit) - that was the long-standing bug.
            var root = await _gql.RawAsync(
                TwitchConstants.SendSpadeEventsMutation,
                new { input = new { data = encoded, repository = "twilight", encoding = "GZIP_B64" } },
                ct).ConfigureAwait(false);

            if (root.TryGetProperty("data", out var data)
                && data.TryGetProperty("sendSpadeEvents", out var send)
                && send.TryGetProperty("statusCode", out var status)
                && status.TryGetInt32(out var code))
            {
                return code == 204;
            }
        }
        catch (GqlAuthException)
        {
            throw; // let the orchestrator handle re-login
        }
        catch
        {
            return false; // transient network/API hiccup; caller retries next tick
        }

        return false;
    }

    /// <summary>json_minify -> utf8 -> gzip -> base64 encoding pipeline.</summary>
    /// <param name="payload">Object serialized as the spade-event payload.</param>
    /// <returns>Base64 of the gzipped compact json.</returns>
    static string EncodePayload(object payload)
    {
        var json = JsonSerializer.Serialize(payload, Compact);
        var raw = Encoding.UTF8.GetBytes(json);

        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(raw, 0, raw.Length);
        return Convert.ToBase64String(ms.ToArray());
    }
}
