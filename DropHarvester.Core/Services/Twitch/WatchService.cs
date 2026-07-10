using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DropHarvester.Services;
using DropHarvester.Models.Twitch;

namespace DropHarvester.Services.Twitch;

/// <summary>
/// The stream-less "watch": every ~59s it sends one minute-watched analytics event to Twitch's
/// beacon/spade endpoint, which is what advances drop progress. As of 2026-07-10 Twitch stopped
/// crediting drop minutes sent via the <c>sendSpadeEvents</c> GraphQL mutation (it still acks 204 but
/// discards the credit); crediting now flows through the classic form-POST to the beacon URL, and only
/// when the payload carries game attribution (<c>game</c>/<c>game_id</c>) and an integer <c>user_id</c>.
/// No video, no browser - a few hundred bytes every minute.
/// </summary>
public interface IWatchService
{
    /// <summary>The resolved beacon/spade endpoint the watch posts to, or null until first resolved.</summary>
    string? BeaconUrl { get; }

    /// <summary>Send one minute-watched heartbeat for the channel. Returns true on HTTP 204 ack.</summary>
    /// <param name="channel">Channel to credit the watch minute to.</param>
    /// <param name="ct">Token to cancel the send.</param>
    Task<bool> SendWatchAsync(TwitchChannel channel, CancellationToken ct = default);

    /// <summary>Debug-only: send one minute-watched heartbeat and return the full round-trip (the exact
    /// decoded payload, the resolved beacon URL, the HTTP status, and the raw response body) so a
    /// 204-acked-but-not-credited watch can be inspected end to end.</summary>
    /// <param name="channel">Channel to probe the watch for.</param>
    /// <param name="ct">Token to cancel the send.</param>
    /// <returns>A diagnostic object describing exactly what was sent and what Twitch returned.</returns>
    Task<object> ProbeAsync(TwitchChannel channel, CancellationToken ct = default);
}

/// <summary>Default <see cref="IWatchService"/> that posts minute-watched events to the beacon endpoint.</summary>
public sealed class WatchService : IWatchService
{
    static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    // beacon_url is preferred over spade_url: on stricter drop campaigns only the beacon pipeline credits.
    static readonly Regex[] BeaconPatterns =
    {
        new("\"beacon_url\"\\s*:\\s*\"(https:[^\"]+)\"", RegexOptions.Compiled),
        new("\"beaconUrl\"\\s*:\\s*\"(https:[^\"]+)\"", RegexOptions.Compiled),
        new("\"spade_url\"\\s*:\\s*\"(https:[^\"]+)\"", RegexOptions.Compiled),
        new("\"spadeUrl\"\\s*:\\s*\"(https:[^\"]+)\"", RegexOptions.Compiled),
    };
    static readonly Regex SettingsJsPattern =
        new("(https://(?:static\\.twitchcdn\\.net|assets\\.twitch\\.tv)/config/settings\\.[^\"'\\s]+\\.js)", RegexOptions.Compiled);

    readonly ITwitchAuth _auth;
    readonly HttpClient _http;
    readonly SemaphoreSlim _urlGate = new(1, 1);
    string? _beaconUrl; // resolved once, then reused for the session

    /// <summary>The resolved beacon/spade endpoint the watch posts to, or null until first resolved.</summary>
    public string? BeaconUrl => _beaconUrl;

    /// <summary>Creates the watch service with auth and an HttpClient built from settings.</summary>
    /// <param name="auth">Twitch auth supplying the logged-in user id.</param>
    /// <param name="settings">Settings store used to build the HTTP handler (proxy etc.).</param>
    public WatchService(ITwitchAuth auth, ISettingsStore settings)
    {
        _auth = auth;
        _http = new HttpClient(HttpClientBuilder.CreateHandler(settings));
    }

    /// <summary>Posts a minute-watched event to the beacon endpoint for an online channel and reports the
    /// 204 ack (the only status Twitch uses for "credited").</summary>
    /// <param name="channel">Channel to credit the watch minute to.</param>
    /// <param name="ct">Token to cancel the send.</param>
    /// <returns>True when Twitch acks with status 204; false when offline, no beacon URL, or on failure.</returns>
    public async Task<bool> SendWatchAsync(TwitchChannel channel, CancellationToken ct = default)
    {
        if (!channel.Online || string.IsNullOrEmpty(channel.BroadcastId))
            return false;

        var url = await ResolveBeaconUrlAsync(ct).ConfigureAwait(false);
        if (url is null)
            return false;

        var body = EncodeForm(BuildMinuteWatched(channel, _auth.State.UserId ?? ""));
        try
        {
            using var resp = await PostBeaconAsync(url, body, ct).ConfigureAwait(false);
            return resp.StatusCode == HttpStatusCode.NoContent;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false; // transient network/API hiccup; caller retries next tick
        }
    }

    /// <summary>Debug-only: send one minute-watched heartbeat and report the full round-trip so a
    /// 204-acked-but-not-credited watch can be inspected end to end.</summary>
    /// <param name="channel">Channel to probe the watch for.</param>
    /// <param name="ct">Token to cancel the send.</param>
    /// <returns>A diagnostic object: the decoded payload, resolved beacon URL, HTTP status, and body.</returns>
    public async Task<object> ProbeAsync(TwitchChannel channel, CancellationToken ct = default)
    {
        var userId = _auth.State.UserId ?? "";
        var eligible = channel.Online && !string.IsNullOrEmpty(channel.BroadcastId);
        var evt = BuildMinuteWatched(channel, userId);
        var payloadJson = JsonSerializer.Serialize(evt, new JsonSerializerOptions { WriteIndented = true });
        var url = await ResolveBeaconUrlAsync(ct).ConfigureAwait(false);

        object? beacon = null;
        string? error = null;
        if (url is null)
        {
            error = "Could not resolve a beacon/spade URL from twitch.tv.";
        }
        else
        {
            try
            {
                using var resp = await PostBeaconAsync(url, EncodeForm(evt), ct).ConfigureAwait(false);
                var respBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                beacon = new
                {
                    BeaconUrl = url,
                    HttpStatus = (int)resp.StatusCode,
                    Credited = resp.StatusCode == HttpStatusCode.NoContent,
                    ResponseBody = respBody.Length > 500 ? respBody[..500] : respBody,
                };
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
            }
        }

        return new
        {
            Channel = channel.Login,
            ChannelId = channel.Id,
            BroadcastId = channel.BroadcastId,
            Game = channel.Game?.Name,
            GameId = channel.Game?.Id,
            channel.Online,
            WouldSendInNormalLoop = eligible,
            UserId = userId,
            DecodedPayload = evt,
            DecodedPayloadJson = payloadJson,
            Beacon = beacon,
            Error = error,
        };
    }

    /// <summary>POSTs the form-encoded <c>data=&lt;base64&gt;</c> body to the beacon URL with the Android UA.</summary>
    /// <param name="url">Resolved beacon/spade endpoint.</param>
    /// <param name="base64Data">Base64 of the minified minute-watched JSON.</param>
    /// <param name="ct">Token to cancel the request.</param>
    /// <returns>The HTTP response (caller inspects the status).</returns>
    Task<HttpResponseMessage> PostBeaconAsync(string url, string base64Data, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("data", base64Data) }),
        };
        req.Headers.TryAddWithoutValidation("User-Agent", TwitchConstants.AndroidUserAgent);
        return _http.SendAsync(req, ct);
    }

    /// <summary>Resolves (and caches) Twitch's beacon/spade endpoint by scraping twitch.tv and, if needed,
    /// its settings JS bundle. Returns null if none can be found.</summary>
    /// <param name="ct">Token to cancel the lookups.</param>
    /// <returns>The beacon URL, or null when it can't be resolved.</returns>
    async Task<string?> ResolveBeaconUrlAsync(CancellationToken ct)
    {
        if (_beaconUrl is not null)
            return _beaconUrl;

        await _urlGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_beaconUrl is not null)
                return _beaconUrl;

            var page = await GetStringAsync("https://www.twitch.tv", ct).ConfigureAwait(false);
            var url = page is null ? null : MatchBeacon(page);

            if (url is null && page is not null)
            {
                var settings = SettingsJsPattern.Match(page);
                if (settings.Success)
                {
                    var js = await GetStringAsync(settings.Groups[1].Value, ct).ConfigureAwait(false);
                    if (js is not null)
                        url = MatchBeacon(js);
                }
            }

            _beaconUrl = url;
            return _beaconUrl;
        }
        finally
        {
            _urlGate.Release();
        }
    }

    /// <summary>Runs the beacon/spade URL patterns over some text and unescapes any JSON slashes.</summary>
    /// <param name="text">Page or JS text to scan.</param>
    /// <returns>The first matched URL, or null.</returns>
    static string? MatchBeacon(string text)
    {
        foreach (var re in BeaconPatterns)
        {
            var m = re.Match(text);
            if (m.Success)
                return m.Groups[1].Value.Replace("\\/", "/");
        }
        return null;
    }

    /// <summary>GETs a URL as text with the Android UA, returning null on any failure.</summary>
    /// <param name="url">URL to fetch.</param>
    /// <param name="ct">Token to cancel the request.</param>
    /// <returns>The response body, or null.</returns>
    async Task<string?> GetStringAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", TwitchConstants.AndroidUserAgent);
            req.Headers.TryAddWithoutValidation("Accept", "text/html,application/javascript,*/*");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    /// <summary>Builds the single minute-watched event shared by the live send and the probe. Carries the
    /// game attribution fields and an integer user_id that the beacon drop-credit pipeline requires (a
    /// string user_id, or missing game/game_id, is 204-acked but silently not credited).</summary>
    /// <param name="channel">Channel the minute is credited to.</param>
    /// <param name="userId">Logged-in user id (sent as an integer when numeric).</param>
    /// <returns>The one-element event array ready to encode.</returns>
    static Dictionary<string, object?>[] BuildMinuteWatched(TwitchChannel channel, string userId) =>
        new[]
        {
            new Dictionary<string, object?>
            {
                ["event"] = "minute-watched",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["channel_id"] = channel.Id,
                    ["broadcast_id"] = channel.BroadcastId,
                    ["player"] = "site",
                    ["user_id"] = long.TryParse(userId, out var uid) ? uid : userId,
                    ["channel"] = channel.Login,
                    ["client_time"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    ["game"] = channel.Game?.Name ?? "",
                    ["game_id"] = channel.Game?.Id ?? "",
                    ["hidden"] = false,
                    ["is_live"] = true,
                    ["live"] = true,
                    ["location"] = "channel",
                    ["logged_in"] = true,
                    ["minutes_logged"] = 1,
                    ["muted"] = false,
                },
            },
        };

    /// <summary>json_minify -> utf8 -> base64 (no gzip: the beacon POST expects plain base64 in `data`).</summary>
    /// <param name="payload">Object serialized as the minute-watched payload.</param>
    /// <returns>Base64 of the compact json.</returns>
    static string EncodeForm(object payload)
    {
        var json = JsonSerializer.Serialize(payload, Compact);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }
}
