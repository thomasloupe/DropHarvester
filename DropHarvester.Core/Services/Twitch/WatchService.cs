using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DropHarvester.Services;
using DropHarvester.Models.Twitch;

namespace DropHarvester.Services.Twitch;

/// <summary>
/// The "watch" that advances drop progress. Every watch tick pulls the channel's live stream
/// (<see cref="HlsWatcher"/>: read the live playlist and HEAD each new segment, no video downloaded) and
/// sends a minute-watched heartbeat alongside it, as a browser player does. Twitch only credits accounts
/// that pull the stream, so the stream watch always runs while a channel is watched. What varies is the
/// heartbeat: an ordered list of heartbeat transports the orchestrator's watchdog can rotate through when
/// progress stalls - the form-POST to the "track" endpoint on several interchangeable hosts
/// (beacon/spade/trowel), then the GraphQL <c>sendSpadeEvents</c> mutation. The POST sends plain base64
/// JSON; the GQL mutation sends the same event gzipped+base64 as the GZIP_B64 "twilight" input. An ack
/// means "accepted", not "credited": real crediting is judged by observed drop progress.
/// </summary>
public interface IWatchService
{
    /// <summary>The resolved beacon/spade endpoint the POST heartbeat prefers, or null until resolved.</summary>
    string? BeaconUrl { get; }

    /// <summary>Human-readable name of the watch in use - the stream watch plus the active heartbeat
    /// transport (for logging and the debug snapshot), or a placeholder before the list has been built.</summary>
    string CurrentTransport { get; }

    /// <summary>Whether there is at least one alternate heartbeat transport to rotate to when progress stalls.</summary>
    bool HasBackupTransports { get; }

    /// <summary>Why the stream watch last failed, or null when it has no error to report.</summary>
    string? LastError { get; }

    /// <summary>Debug-snapshot view of the HLS stream watch (running, variant, segments, last error), or
    /// null if it hasn't run this session.</summary>
    object? StreamStatus { get; }

    /// <summary>Watch the channel, called once per watch tick: keep the background stream watch on this
    /// channel and send one minute-watched heartbeat on the active heartbeat transport. Returns true while
    /// the stream is being pulled; the heartbeat's ack does not decide it.</summary>
    /// <param name="channel">Channel to credit the watch minute to.</param>
    /// <param name="ct">Token to cancel the send.</param>
    Task<bool> SendWatchAsync(TwitchChannel channel, CancellationToken ct = default);

    /// <summary>Stop the background stream watch, if one is running (harvesting stopped or went idle).
    /// The next <see cref="SendWatchAsync"/> starts it again.</summary>
    void StopWatching();

    /// <summary>Advance the active heartbeat transport to the next not-yet-tried candidate in a single
    /// self-heal pass; the stream watch keeps running throughout. The first call after a good run starts
    /// the pass from the known-good primary. Returns true if it moved to a fresh transport this pass;
    /// false once every alternate has been tried.</summary>
    /// <returns>True if the active heartbeat transport advanced; false when the pass is exhausted.</returns>
    bool RotateToNextTransport();

    /// <summary>Adopt the active heartbeat transport as the known-good primary (called when a real credit
    /// lands), so a rotation that restored progress sticks and later stalls rotate outward from it.</summary>
    void MarkCurrentGood();

    /// <summary>Return the active heartbeat transport to the last known-good primary and end the pass
    /// (called when a full rotation restored nothing - a Twitch-side outage rather than a transport problem).</summary>
    void SettleToPrimary();

    /// <summary>Reset the active and primary heartbeat transport back to the first (most-preferred)
    /// candidate. Called on a fresh harvest start so a prior run's rotation doesn't carry over.</summary>
    void ResetTransport();

    /// <summary>Debug-only: run one full playback pass of the stream watch, then one send on EVERY heartbeat
    /// transport, and return the full round-trip for each (for the stream each step of a fresh playback
    /// session; for the heartbeats the decoded payload, endpoint, ack status and raw response) so it's
    /// obvious at a glance what Twitch is accepting right now.</summary>
    /// <param name="channel">Channel to probe the watch for.</param>
    /// <param name="ct">Token to cancel the sends.</param>
    /// <returns>A diagnostic object describing what was sent and what each transport returned.</returns>
    Task<object> ProbeAsync(TwitchChannel channel, CancellationToken ct = default);
}

/// <summary>Default <see cref="IWatchService"/>: the HLS stream watch on every tick, with the minute-watched
/// heartbeat sent alongside on one of the POST "track" hosts or the GraphQL mutation.</summary>
public sealed class WatchService : IWatchService
{
    const string StreamWatchName = "HLS stream";

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

    /// <summary>How a heartbeat transport sends the minute-watched event: as a form-POST or via the
    /// GraphQL mutation.</summary>
    enum TransportKind { Post, Gql }

    /// <summary>One heartbeat transport: a friendly name, how it sends, and (for POST) which host to send to.</summary>
    /// <param name="Name">Human-readable label shown in logs and the debug snapshot.</param>
    /// <param name="Kind">Whether this transport is a form-POST or the GraphQL mutation.</param>
    /// <param name="Url">The POST "track" endpoint, or null for the GQL transport.</param>
    sealed record Transport(string Name, TransportKind Kind, string? Url);

    readonly ITwitchAuth _auth;
    readonly IGqlClient _gql;
    readonly HttpClient _http;
    readonly HlsWatcher _hls;
    readonly SemaphoreSlim _urlGate = new(1, 1);
    readonly SemaphoreSlim _buildGate = new(1, 1);
    string? _beaconUrl; // resolved once, then reused for the session

    readonly List<Transport> _transports = new();
    bool _built;
    int _primaryIndex;                 // last known-good heartbeat transport
    int _currentIndex;                 // heartbeat transport being sent on right now
    bool _passActive;                  // walking a single self-heal pass
    readonly HashSet<int> _triedThisPass = new();

    /// <summary>The resolved beacon/spade endpoint the POST heartbeat prefers, or null until resolved.</summary>
    public string? BeaconUrl => _beaconUrl;

    /// <summary>"HLS stream + &lt;heartbeat transport&gt;", or a placeholder before the list is built.</summary>
    public string CurrentTransport =>
        _built && _currentIndex >= 0 && _currentIndex < _transports.Count
            ? $"{StreamWatchName} + {_transports[_currentIndex].Name}"
            : "(resolving)";

    /// <summary>Whether at least one alternate heartbeat transport exists to rotate to.</summary>
    public bool HasBackupTransports => _built && _transports.Count > 1;

    /// <summary>Why the stream watch last failed, or null.</summary>
    public string? LastError => _hls.LastError;

    /// <summary>Debug-snapshot view of the HLS stream watch, or null if it hasn't run this session.</summary>
    public object? StreamStatus => _hls.Status;

    /// <summary>Creates the watch service with auth, the GQL client (for the playback token and the mutation
    /// transport), and an HttpClient built from settings.</summary>
    /// <param name="auth">Twitch auth supplying the logged-in user id.</param>
    /// <param name="gql">GraphQL client used for the HLS playback token and the <c>sendSpadeEvents</c> transport.</param>
    /// <param name="settings">Settings store used to build the HTTP handler (proxy etc.).</param>
    public WatchService(ITwitchAuth auth, IGqlClient gql, ISettingsStore settings)
    {
        _auth = auth;
        _gql = gql;
        _http = new HttpClient(HttpClientBuilder.CreateHandler(settings));
        _hls = new HlsWatcher(gql, _http);
    }

    /// <summary>Watches an online channel: keeps the background stream watch on it and sends the
    /// minute-watched heartbeat on the active heartbeat transport alongside, as a browser player does.</summary>
    /// <param name="channel">Channel to credit the watch minute to.</param>
    /// <param name="ct">Token to cancel the send.</param>
    /// <returns>True while the stream is being pulled; false when offline, unbuilt, or the stream watch fails.</returns>
    public async Task<bool> SendWatchAsync(TwitchChannel channel, CancellationToken ct = default)
    {
        if (!channel.Online || string.IsNullOrEmpty(channel.BroadcastId))
            return false;

        await EnsureTransportsAsync(ct).ConfigureAwait(false);

        var watching = await _hls.WatchAsync(channel, ct).ConfigureAwait(false);
        // The heartbeat doesn't credit by itself, but a real player sends it; its ack decides nothing.
        if (_transports.Count > 0)
            await SendViaAsync(_transports[Math.Clamp(_currentIndex, 0, _transports.Count - 1)], channel, ct).ConfigureAwait(false);
        return watching;
    }

    /// <summary>Stop the background stream watch, if one is running.</summary>
    public void StopWatching() => _hls.Stop();

    /// <summary>Advance the active heartbeat transport to the next not-yet-tried candidate in the current pass.</summary>
    /// <returns>True if the active heartbeat transport advanced to a fresh candidate; false when the pass is exhausted.</returns>
    public bool RotateToNextTransport()
    {
        if (!_built || _transports.Count <= 1)
            return false;

        if (!_passActive)
        {
            _passActive = true;
            _triedThisPass.Clear();
            _triedThisPass.Add(_primaryIndex);
            _currentIndex = _primaryIndex;
        }

        for (var step = 1; step <= _transports.Count; step++)
        {
            var idx = (_primaryIndex + step) % _transports.Count;
            if (_triedThisPass.Add(idx))
            {
                _currentIndex = idx;
                return true;
            }
        }
        return false;
    }

    /// <summary>Adopt the active heartbeat transport as the known-good primary and end the pass.</summary>
    public void MarkCurrentGood()
    {
        _primaryIndex = _currentIndex;
        _passActive = false;
        _triedThisPass.Clear();
    }

    /// <summary>Return the active heartbeat transport to the last known-good primary and end the pass.</summary>
    public void SettleToPrimary()
    {
        _currentIndex = _primaryIndex;
        _passActive = false;
        _triedThisPass.Clear();
    }

    /// <summary>Reset the active and primary heartbeat transport back to the first (most-preferred) candidate.</summary>
    public void ResetTransport()
    {
        _primaryIndex = 0;
        _currentIndex = 0;
        _passActive = false;
        _triedThisPass.Clear();
    }

    /// <summary>Debug-only: run one playback pass of the stream watch and one send on every heartbeat
    /// transport, and report each round-trip so it's clear what Twitch is accepting right now.</summary>
    /// <param name="channel">Channel to probe the watch for.</param>
    /// <param name="ct">Token to cancel the sends.</param>
    /// <returns>A diagnostic object: decoded payload plus a per-transport result list, stream watch first.</returns>
    public async Task<object> ProbeAsync(TwitchChannel channel, CancellationToken ct = default)
    {
        var userId = _auth.State.UserId ?? "";
        var eligible = channel.Online && !string.IsNullOrEmpty(channel.BroadcastId);
        var evt = BuildMinuteWatched(channel, userId);
        var payloadJson = JsonSerializer.Serialize(evt, new JsonSerializerOptions { WriteIndented = true });

        await EnsureTransportsAsync(ct).ConfigureAwait(false);

        var results = new List<object>();
        try
        {
            results.Add(new { Transport = StreamWatchName, Probe = await _hls.ProbeAsync(channel, ct).ConfigureAwait(false) });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            results.Add(new { Transport = StreamWatchName, Error = $"{ex.GetType().Name}: {ex.Message}" });
        }

        foreach (var t in _transports)
        {
            try
            {
                if (t.Kind == TransportKind.Post)
                {
                    using var resp = await PostTrackAsync(t.Url!, EncodeBase64(evt), ct).ConfigureAwait(false);
                    var respBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    results.Add(new
                    {
                        Transport = t.Name,
                        Endpoint = t.Url,
                        HttpStatus = (int)resp.StatusCode,
                        Accepted = resp.StatusCode == HttpStatusCode.NoContent,
                        ResponseBody = respBody.Length > 300 ? respBody[..300] : respBody,
                    });
                }
                else
                {
                    var (accepted, code, raw) = await SendViaGqlProbeAsync(evt, ct).ConfigureAwait(false);
                    results.Add(new
                    {
                        Transport = t.Name,
                        Endpoint = TwitchConstants.GqlUrl,
                        GqlStatusCode = code,
                        Accepted = accepted,
                        ResponseBody = raw.Length > 300 ? raw[..300] : raw,
                    });
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                results.Add(new { Transport = t.Name, Endpoint = t.Url ?? TwitchConstants.GqlUrl, Error = $"{ex.GetType().Name}: {ex.Message}" });
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
            ActiveTransport = CurrentTransport,
            ResolvedBeaconUrl = _beaconUrl,
            DecodedPayload = evt,
            DecodedPayloadJson = payloadJson,
            Transports = results,
        };
    }

    /// <summary>Send the minute-watched event on one heartbeat transport, containing every failure (a
    /// transient error becomes a false so a bad transport can't kill the watch loop).</summary>
    /// <param name="t">The transport to send on.</param>
    /// <param name="channel">Channel the minute is credited to.</param>
    /// <param name="ct">Token to cancel the send.</param>
    /// <returns>True on the transport's accept ack.</returns>
    async Task<bool> SendViaAsync(Transport t, TwitchChannel channel, CancellationToken ct)
    {
        var evt = BuildMinuteWatched(channel, _auth.State.UserId ?? "");
        try
        {
            if (t.Kind == TransportKind.Post)
            {
                using var resp = await PostTrackAsync(t.Url!, EncodeBase64(evt), ct).ConfigureAwait(false);
                return resp.StatusCode == HttpStatusCode.NoContent;
            }
            var (accepted, _, _) = await SendViaGqlProbeAsync(evt, ct).ConfigureAwait(false);
            return accepted;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        // A failure here (including a 401 from a disabled GQL mutation) just means "this transport didn't
        // work" - return false so the watchdog rotates on. Real token expiry is surfaced by the inventory
        // sync that runs every tick, so it isn't missed by swallowing it here.
        catch { return false; }
    }

    /// <summary>Send the minute-watched event via the <c>sendSpadeEvents</c> GraphQL mutation and read back
    /// the ack.</summary>
    /// <param name="evt">The minute-watched event array.</param>
    /// <param name="ct">Token to cancel the request.</param>
    /// <returns>Whether the mutation acked 204, the reported status code (or -1), and the raw response.</returns>
    async Task<(bool accepted, int code, string raw)> SendViaGqlProbeAsync(object evt, CancellationToken ct)
    {
        var vars = new
        {
            input = new
            {
                data = EncodeGzipBase64(evt),
                repository = "twilight",
                encoding = "GZIP_B64",
            },
        };
        var root = await _gql.RawAsync(TwitchConstants.SendSpadeEventsMutation, vars, ct).ConfigureAwait(false);
        var raw = root.GetRawText();
        if (root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("sendSpadeEvents", out var sse)
            && sse.ValueKind == JsonValueKind.Object
            && sse.TryGetProperty("statusCode", out var sc)
            && sc.TryGetInt32(out var code))
            return (code == 204, code, raw);
        return (false, -1, raw);
    }

    /// <summary>POSTs the form-encoded <c>data=&lt;base64&gt;</c> body to a track host with the Android UA.</summary>
    /// <param name="url">The "track" endpoint to send to.</param>
    /// <param name="base64Data">Base64 of the minified minute-watched JSON.</param>
    /// <param name="ct">Token to cancel the request.</param>
    /// <returns>The HTTP response (caller inspects the status).</returns>
    Task<HttpResponseMessage> PostTrackAsync(string url, string base64Data, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("data", base64Data) }),
        };
        req.Headers.TryAddWithoutValidation("User-Agent", TwitchConstants.AndroidUserAgent);
        return _http.SendAsync(req, ct);
    }

    /// <summary>Builds the ordered heartbeat transport list once: the resolved beacon URL (if any) followed
    /// by the hardcoded track hosts (deduped), then the GraphQL mutation last. The POST hosts lead as the
    /// least detectable.</summary>
    /// <param name="ct">Token to cancel the URL lookups.</param>
    async Task EnsureTransportsAsync(CancellationToken ct)
    {
        if (_built)
            return;

        await _buildGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_built)
                return;

            var resolved = await ResolveBeaconUrlAsync(ct).ConfigureAwait(false);

            var hosts = new List<string>();
            void AddHost(string h)
            {
                if (!hosts.Any(x => string.Equals(x, h, StringComparison.OrdinalIgnoreCase)))
                    hosts.Add(h);
            }
            if (resolved is not null)
                AddHost(resolved);
            foreach (var h in TwitchConstants.SpadeTrackHosts)
                AddHost(h);

            _transports.Clear();
            foreach (var h in hosts)
                _transports.Add(new Transport(PostName(h), TransportKind.Post, h));
            _transports.Add(new Transport("GQL sendSpadeEvents", TransportKind.Gql, null));

            _primaryIndex = 0;
            _currentIndex = 0;
            _passActive = false;
            _triedThisPass.Clear();
            _built = true;
        }
        finally
        {
            _buildGate.Release();
        }
    }

    /// <summary>Friendly "host (POST)" label for a track endpoint.</summary>
    /// <param name="url">The track endpoint URL.</param>
    /// <returns>A short label, e.g. "beacon.twitch.tv (POST)".</returns>
    static string PostName(string url)
    {
        try { return $"{new Uri(url).Host} (POST)"; }
        catch { return $"{url} (POST)"; }
    }

    /// <summary>Resolves (and caches) Twitch's beacon/spade endpoint by scraping twitch.tv and, if needed,
    /// its settings JS bundle. Returns null if none can be found (the hardcoded hosts then stand in).</summary>
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

    /// <summary>Builds the single minute-watched event shared by every transport and the probe. Carries the
    /// game attribution fields and an integer user_id that the drop-credit pipeline requires (a string
    /// user_id, or missing game/game_id, is ack'd but silently not credited).</summary>
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

    /// <summary>json_minify -> utf8 -> base64 (the POST transport expects plain base64 in <c>data</c>).</summary>
    /// <param name="payload">Object serialized as the minute-watched payload.</param>
    /// <returns>Base64 of the compact json.</returns>
    static string EncodeBase64(object payload)
    {
        var json = JsonSerializer.Serialize(payload, Compact);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>json_minify -> utf8 -> gzip -> base64 (the GQL mutation expects the GZIP_B64 encoding).</summary>
    /// <param name="payload">Object serialized as the minute-watched payload.</param>
    /// <returns>Base64 of the gzipped compact json.</returns>
    static string EncodeGzipBase64(object payload)
    {
        var json = JsonSerializer.Serialize(payload, Compact);
        var bytes = Encoding.UTF8.GetBytes(json);
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(bytes, 0, bytes.Length);
        return Convert.ToBase64String(ms.ToArray());
    }
}
