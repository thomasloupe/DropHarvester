using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DropHarvester.Models.Twitch;

namespace DropHarvester.Services.Twitch;

/// <summary>
/// The default watch transport: watch the stream the way a player does, minus the video. Since Oct 2026
/// Twitch credits drop minutes only to accounts that actually pull the stream (the bare minute-watched
/// heartbeat is still accepted but no longer counted), so this fetches a playback access token for the
/// channel, resolves the lowest-bandwidth video variant from usher, then every
/// <see cref="TwitchConstants.HlsPollInterval"/> re-reads that media playlist and sends a HEAD request for
/// each segment it hasn't touched yet. HEAD means no audio/video is downloaded - a few KB of playlist text
/// and a handful of empty responses per poll. The token is issued to the logged-in user, which is what ties
/// the segment requests to the account.
///
/// It runs as a background pump because the orchestrator ticks once a minute while a live playlist only
/// holds ~30s of segments. The orchestrator keeps it alive by calling <see cref="WatchAsync"/> each tick;
/// <see cref="Stop"/> ends it, and if the calls simply stop coming (idle, rotated to a fallback transport)
/// the lease lapses and it ends on its own, so it can never keep watching a channel nobody asked for.
/// </summary>
internal sealed class HlsWatcher
{
    // Covers a slow orchestrator tick (watch interval + inventory sync + claims) with room to spare, yet a
    // pump nobody renews still stops within a few minutes.
    static readonly TimeSpan Lease = TimeSpan.FromMinutes(3);
    // A good poll vouches for the stream this long, so one slow or failed poll doesn't flip the tick's ack.
    static readonly TimeSpan HealthyWindow = TimeSpan.FromSeconds(35);
    // How long a fresh pump's caller waits for the first poll before acking on what it has.
    static readonly TimeSpan FirstPollWait = TimeSpan.FromSeconds(20);
    // Back-off between playback-session attempts while the channel can't be resolved (e.g. just went offline).
    static readonly TimeSpan ResolveRetry = TimeSpan.FromSeconds(30);
    static readonly TimeSpan PlaylistTimeout = TimeSpan.FromSeconds(8);
    static readonly TimeSpan SegmentTimeout = TimeSpan.FromSeconds(5);
    // Ceiling on one whole poll (token + playlists + every HEAD), so a run of slow segments can't stall it.
    static readonly TimeSpan PollBudget = TimeSpan.FromSeconds(25);
    // Segments already HEAD'd, so each is touched once; bounded, and far more than one playlist window.
    const int SeenCap = 256;

    readonly IGqlClient _gql;
    readonly HttpClient _http;
    readonly object _lock = new();
    volatile Session? _session; // the current (or last ended) watch, kept for the debug snapshot

    /// <summary>Creates the watcher on the watch service's GQL client (for the user-bound playback token) and
    /// HttpClient (which carries the proxy setting).</summary>
    /// <param name="gql">GraphQL client used to fetch the playback access token as the logged-in user.</param>
    /// <param name="http">HttpClient used for usher, the media playlist and the segment HEADs.</param>
    public HlsWatcher(IGqlClient gql, HttpClient http)
    {
        _gql = gql;
        _http = http;
    }

    /// <summary>Make sure the channel's current broadcast is being watched and renew the lease. A different
    /// channel or broadcast replaces the running pump; a fresh pump's first poll is awaited so the result
    /// reflects whether the stream is really being pulled.</summary>
    /// <param name="channel">The online channel to watch.</param>
    /// <param name="ct">Token to cancel the wait for a fresh pump's first poll.</param>
    /// <returns>True when the stream was pulled successfully within the last poll or so.</returns>
    public async Task<bool> WatchAsync(TwitchChannel channel, CancellationToken ct)
    {
        Session s;
        var fresh = false;
        lock (_lock)
        {
            var cur = _session;
            if (cur is null || cur.Ended
                || !string.Equals(cur.Login, channel.Login, StringComparison.OrdinalIgnoreCase)
                || cur.BroadcastId != channel.BroadcastId)
            {
                cur?.Cts.Cancel();
                s = new Session(channel.Login, channel.BroadcastId ?? "");
                s.Renew();
                s.Pump = Task.Run(() => PumpAsync(s));
                _session = s;
                fresh = true;
            }
            else
            {
                s = cur;
                s.Renew();
            }
        }

        if (!fresh)
            return s.Healthy;
        try { return await s.FirstPoll.Task.WaitAsync(FirstPollWait, ct).ConfigureAwait(false); }
        catch (TimeoutException) { return false; }
    }

    /// <summary>Stop watching (harvesting stopped, went idle, or a fallback transport took over).</summary>
    public void Stop()
    {
        lock (_lock)
            _session?.Cts.Cancel();
    }

    /// <summary>Why the current watch last failed, or null while it's healthy or not running.</summary>
    public string? LastError => _session is { Ended: false } s ? s.LastError : null;

    /// <summary>Debug-snapshot view of the current (or last) watch, or null if none has run. Never includes
    /// the token or any signed URL, since snapshots get pasted around.</summary>
    public object? Status => _session is not { } s ? null : new
    {
        Channel = s.Login,
        s.BroadcastId,
        Running = !s.Ended,
        s.Healthy,
        s.Variant,
        s.TokenHasUser,
        s.StartedUtc,
        s.LastPollUtc,
        s.LastOkUtc,
        s.SegmentsWatched,
        s.SegmentFailures,
        PlaybackSessions = s.Resolves,
        s.LastError,
    };

    /// <summary>Debug: run one complete, independent watch pass for the channel - a new playback session,
    /// one playlist read and a HEAD for every listed segment - and report each step. Leaves the running
    /// pump alone. Signed URLs and the token are never included.</summary>
    /// <param name="channel">Channel to probe.</param>
    /// <param name="ct">Token to cancel the probe.</param>
    /// <returns>A diagnostic object describing each step and whether the pass succeeded.</returns>
    public async Task<object> ProbeAsync(TwitchChannel channel, CancellationToken ct)
    {
        Resolved? r = null;
        int? playlistStatus = null;
        var segmentStatuses = new List<int>();
        string? error = null;
        try
        {
            r = await ResolveAsync(channel.Login, ct).ConfigureAwait(false);
            if (r.MediaUrl is null)
                error = r.Error;
            else
            {
                var (status, body) = await GetTextAsync(r.MediaUrl, closeConnection: true, ct).ConfigureAwait(false);
                playlistStatus = (int)status;
                if (status != HttpStatusCode.OK)
                    error = $"media playlist returned HTTP {(int)status}";
                else
                {
                    foreach (var url in HlsPlaylist.SegmentUrls(body, new Uri(r.MediaUrl)))
                        segmentStatuses.Add(await HeadAsync(url, ct).ConfigureAwait(false) is { } st ? (int)st : -1);
                    if (segmentStatuses.Count == 0)
                        error = "media playlist listed no segments";
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { error = Describe(ex); }

        return new
        {
            Accepted = error is null && segmentStatuses.Count > 0 && segmentStatuses.All(c => c == 200),
            TokenHasUser = r?.TokenHasUser,
            TokenExpiresUtc = r?.TokenExpiresUtc,
            Variants = r?.Variants.Select(v => new { v.Name, v.Bandwidth, v.HasVideo }),
            ChosenVariant = r?.Variant,
            PlaylistHost = r?.MediaUrl is { } m ? new Uri(m).Host : null,
            MediaPlaylistHttpStatus = playlistStatus,
            SegmentHeadStatuses = segmentStatuses,
            Error = error,
            RunningWatch = Status,
        };
    }

    /// <summary>The pump: poll on the interval until cancelled or the lease lapses. The first result is
    /// handed to whoever started the pump.</summary>
    /// <param name="s">The session this pump drives.</param>
    async Task PumpAsync(Session s)
    {
        var ct = s.Cts.Token;
        try
        {
            while (!ct.IsCancellationRequested && !s.LeaseExpired)
            {
                var started = DateTimeOffset.UtcNow;
                s.FirstPoll.TrySetResult(await PollAsync(s, ct).ConfigureAwait(false));
                var wait = TwitchConstants.HlsPollInterval - (DateTimeOffset.UtcNow - started);
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { s.LastError = Describe(ex); } // PollAsync contains its own failures; backstop only
        finally { s.FirstPoll.TrySetResult(false); }
    }

    /// <summary>One poll: (re)start the playback session if needed, read the media playlist, and HEAD every
    /// segment not touched yet. Failures are recorded on the session, never thrown.</summary>
    /// <param name="s">The session being polled.</param>
    /// <param name="ct">The session's cancellation token.</param>
    /// <returns>True when the playlist was read and any new segments could be fetched.</returns>
    async Task<bool> PollAsync(Session s, CancellationToken ct)
    {
        s.LastPollUtc = DateTimeOffset.UtcNow;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(PollBudget);
        try
        {
            if (s.MediaUrl is null)
            {
                if (DateTimeOffset.UtcNow < s.ResolveNotBefore)
                    return false;
                var r = await ResolveAsync(s.Login, budget.Token).ConfigureAwait(false);
                if (r.MediaUrl is null)
                {
                    s.ResolveNotBefore = DateTimeOffset.UtcNow + ResolveRetry;
                    return Fail(s, r.Error);
                }
                s.Resolves++;
                s.MediaUrl = r.MediaUrl;
                s.Variant = r.Variant;
                s.TokenHasUser = r.TokenHasUser;
            }

            var (status, body) = await GetTextAsync(s.MediaUrl, closeConnection: true, budget.Token).ConfigureAwait(false);
            if (status != HttpStatusCode.OK)
            {
                // The signed playlist expired or the broadcast ended: start a new playback session next poll.
                if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone)
                    s.MediaUrl = null;
                return Fail(s, $"media playlist returned HTTP {(int)status}");
            }
            var segments = HlsPlaylist.SegmentUrls(body, new Uri(s.MediaUrl));
            if (segments.Count == 0)
                return Fail(s, "media playlist listed no segments");

            int fetched = 0, failed = 0;
            foreach (var url in segments)
            {
                if (s.HasSeen(url))
                    continue;
                var st = await HeadAsync(url, budget.Token).ConfigureAwait(false);
                if (st == HttpStatusCode.OK)
                {
                    s.MarkSeen(url);
                    s.SegmentsWatched++;
                    fetched++;
                    continue;
                }
                failed++;
                s.SegmentFailures++;
                // A segment can 404 just by rolling off the edge before its HEAD; only a rejected signature
                // means the playback session itself has expired.
                if (st is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    s.MediaUrl = null;
            }
            // A stray miss doesn't mean we stopped watching; a poll where nothing new could be fetched does.
            if (failed > 0 && fetched == 0)
                return Fail(s, $"all {failed} new segment request(s) failed");

            s.LastOkUtc = DateTimeOffset.UtcNow;
            s.LastError = null;
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return Fail(s, Describe(ex)); }
    }

    /// <summary>Start a playback session for the channel: get a playback access token as the logged-in
    /// user, ask usher for the stream's variants, and pick the lowest-bandwidth video one. (Usher's variant
    /// order isn't by quality, so taking the last line - as some miners do - can land on 720p60.)</summary>
    /// <param name="login">Channel login.</param>
    /// <param name="ct">Token to cancel the requests.</param>
    /// <returns>The resolved media playlist and token facts, or the reason none could be resolved.</returns>
    async Task<Resolved> ResolveAsync(string login, CancellationToken ct)
    {
        var root = await _gql.PersistedAsync("PlaybackAccessToken", TwitchConstants.Gql.PlaybackAccessToken, new
        {
            isLive = true,
            isVod = false,
            login,
            platform = "web",
            playerType = "site",
            vodID = "",
        }, ct).ConfigureAwait(false);

        if (root.Path("data", "streamPlaybackAccessToken") is not { } tok
            || tok.Str("value") is not { Length: > 0 } value
            || tok.Str("signature") is not { Length: > 0 } signature)
            return Resolved.Failed("Twitch returned no playback token for the channel");

        var (hasUser, expires) = ReadToken(value);
        var masterUrl = string.Format(TwitchConstants.UsherChannelHlsUrl, Uri.EscapeDataString(login))
            + $"?sig={Uri.EscapeDataString(signature)}&token={Uri.EscapeDataString(value)}";
        var (status, body) = await GetTextAsync(masterUrl, closeConnection: false, ct).ConfigureAwait(false);
        if (status != HttpStatusCode.OK)
            return Resolved.Failed($"usher returned HTTP {(int)status}{UsherError(body)}") with { TokenHasUser = hasUser, TokenExpiresUtc = expires };

        var variants = HlsPlaylist.Variants(body, new Uri(masterUrl));
        var pick = HlsPlaylist.PickLowest(variants);
        if (pick is null)
            return Resolved.Failed("usher listed no playable variants") with { TokenHasUser = hasUser, TokenExpiresUtc = expires };
        return new Resolved(pick.Url, pick.Name, hasUser, expires, variants, null);
    }

    /// <summary>GETs a playlist as text with the Android UA and a per-request timeout.</summary>
    /// <param name="url">Playlist URL.</param>
    /// <param name="closeConnection">Close the connection after the response (see the media-playlist note).</param>
    /// <param name="ct">Token to cancel the request.</param>
    /// <returns>The status code and body.</returns>
    async Task<(HttpStatusCode status, string body)> GetTextAsync(string url, bool closeConnection, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PlaylistTimeout);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", TwitchConstants.AndroidUserAgent);
        // The media-playlist CDN drops the connection shortly after serving a list; closing it ourselves
        // keeps the next request from landing on a socket that is about to be cut.
        if (closeConnection)
            req.Headers.ConnectionClose = true;
        using var resp = await _http.SendAsync(req, timeout.Token).ConfigureAwait(false);
        return (resp.StatusCode, await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
    }

    /// <summary>HEADs one segment: enough for Twitch to count it as watched, without the video body.</summary>
    /// <param name="url">Segment URL.</param>
    /// <param name="ct">Token to cancel the request.</param>
    /// <returns>The status code, or null if the request failed or timed out.</returns>
    async Task<HttpStatusCode?> HeadAsync(string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SegmentTimeout);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            req.Headers.TryAddWithoutValidation("User-Agent", TwitchConstants.AndroidUserAgent);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            return resp.StatusCode;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; } // this HEAD timed out
        catch (HttpRequestException) { return null; }
    }

    /// <summary>Whether the playback token was issued to a user (it must be, or nothing gets credited) and
    /// when it expires, read from the token's JSON value.</summary>
    /// <param name="value">The token's <c>value</c> string.</param>
    static (bool? hasUser, DateTimeOffset? expires) ReadToken(string value)
    {
        try
        {
            using var doc = JsonDocument.Parse(value);
            var r = doc.RootElement;
            bool? hasUser = r.TryGetProperty("user_id", out var uid) ? uid.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) : null;
            DateTimeOffset? expires = r.TryGetProperty("expires", out var ex) && ex.TryGetInt64(out var secs)
                ? DateTimeOffset.FromUnixTimeSeconds(secs) : null;
            return (hasUser, expires);
        }
        catch (JsonException) { return (null, null); }
    }

    /// <summary>Usher's error text from a JSON error body (e.g. ": Can not find channel" when offline).</summary>
    /// <param name="body">The usher response body.</param>
    static string UsherError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0
                ? doc.RootElement[0] : doc.RootElement;
            return r.ValueKind == JsonValueKind.Object && r.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                ? $": {e.GetString()}" : "";
        }
        catch (JsonException) { return ""; }
    }

    /// <summary>Record a failed poll on the session.</summary>
    /// <param name="s">The session.</param>
    /// <param name="error">What went wrong.</param>
    /// <returns>Always false, for <c>return Fail(...)</c>.</returns>
    static bool Fail(Session s, string? error)
    {
        s.LastError = error ?? "unknown error";
        return false;
    }

    /// <summary>Short description of an exception for the snapshot (a timeout reads as one).</summary>
    /// <param name="ex">The exception.</param>
    static string Describe(Exception ex)
        => ex is OperationCanceledException ? "timed out" : $"{ex.GetType().Name}: {ex.Message}";

    /// <summary>A resolved playback session, or why there isn't one.</summary>
    /// <param name="MediaUrl">The chosen variant's media playlist URL (signed), or null on failure.</param>
    /// <param name="Variant">The chosen variant's name, e.g. "160p30".</param>
    /// <param name="TokenHasUser">Whether the playback token names the logged-in user.</param>
    /// <param name="TokenExpiresUtc">When the playback token expires.</param>
    /// <param name="Variants">Every variant usher offered.</param>
    /// <param name="Error">Why no playlist was resolved, or null on success.</param>
    sealed record Resolved(string? MediaUrl, string? Variant, bool? TokenHasUser, DateTimeOffset? TokenExpiresUtc,
        IReadOnlyList<HlsVariant> Variants, string? Error)
    {
        public static Resolved Failed(string error) => new(null, null, null, null, Array.Empty<HlsVariant>(), error);
    }

    /// <summary>One watch of one broadcast: its playback session, the segments already touched, and the
    /// readings the ack and the debug snapshot are built from. A new channel or broadcast gets a new one.</summary>
    sealed class Session
    {
        long _leaseUntilTicks;
        readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        readonly Queue<string> _seenOrder = new();

        public Session(string login, string broadcastId)
        {
            Login = login;
            BroadcastId = broadcastId;
        }

        public string Login { get; }
        public string BroadcastId { get; }
        public CancellationTokenSource Cts { get; } = new();
        public TaskCompletionSource<bool> FirstPoll { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Pump { get; set; } = Task.CompletedTask;

        // Pump-owned playback state.
        public string? MediaUrl;
        public DateTimeOffset ResolveNotBefore;

        // Readings (also read by the snapshot thread; a stale read only skews a debug field).
        public string? Variant;
        public bool? TokenHasUser;
        public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? LastPollUtc;
        public DateTimeOffset? LastOkUtc;
        public string? LastError;
        public long SegmentsWatched;
        public long SegmentFailures;
        public int Resolves;

        public bool Ended => Cts.IsCancellationRequested || Pump.IsCompleted;
        public bool Healthy => !Ended && LastOkUtc is { } ok && DateTimeOffset.UtcNow - ok <= HealthyWindow;
        public bool LeaseExpired => DateTimeOffset.UtcNow.UtcTicks > Interlocked.Read(ref _leaseUntilTicks);
        public void Renew() => Interlocked.Exchange(ref _leaseUntilTicks, (DateTimeOffset.UtcNow + Lease).UtcTicks);

        public bool HasSeen(string url) => _seen.Contains(url);

        public void MarkSeen(string url)
        {
            if (!_seen.Add(url))
                return;
            _seenOrder.Enqueue(url);
            if (_seenOrder.Count > SeenCap)
                _seen.Remove(_seenOrder.Dequeue());
        }
    }
}

/// <summary>One variant from a master playlist.</summary>
/// <param name="Url">The variant's media playlist URL.</param>
/// <param name="Bandwidth">Its declared BANDWIDTH (long.MaxValue when missing, so it sorts last).</param>
/// <param name="Name">Its VIDEO group name, e.g. "160p30" or "audio_only".</param>
/// <param name="HasVideo">Whether it declares a RESOLUTION (audio-only variants don't).</param>
internal sealed record HlsVariant(string Url, long Bandwidth, string? Name, bool HasVideo);

/// <summary>Just enough HLS (RFC 8216) parsing for the watch: variants from a master playlist and segment
/// URIs from a media playlist. Twitch's own tags (prefetch, dateranges, ad markers) are skipped.</summary>
internal static class HlsPlaylist
{
    /// <summary>The variants a master playlist offers: each URI line that follows an
    /// <c>#EXT-X-STREAM-INF</c> tag, with that tag's bandwidth/name/resolution.</summary>
    /// <param name="master">Master playlist text.</param>
    /// <param name="baseUri">The playlist's own URL, for resolving relative URIs.</param>
    /// <returns>The variants in playlist order; empty if this isn't a playlist.</returns>
    public static List<HlsVariant> Variants(string master, Uri baseUri)
    {
        var list = new List<HlsVariant>();
        if (!IsPlaylist(master))
            return list;
        string? inf = null;
        foreach (var raw in master.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            if (line.StartsWith('#'))
            {
                if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
                    inf = line;
                continue;
            }
            if (inf is not null && Resolve(baseUri, line) is { } url)
            {
                var bandwidth = long.TryParse(Attribute(inf, "BANDWIDTH"), out var bw) ? bw : long.MaxValue;
                list.Add(new HlsVariant(url, bandwidth, Attribute(inf, "VIDEO"), Attribute(inf, "RESOLUTION") is not null));
            }
            inf = null;
        }
        return list;
    }

    /// <summary>The lowest-bandwidth video variant, falling back to the lowest of any kind (audio-only)
    /// when no video variant is offered.</summary>
    /// <param name="variants">The master playlist's variants.</param>
    /// <returns>The variant to watch, or null when there are none.</returns>
    public static HlsVariant? PickLowest(IReadOnlyList<HlsVariant> variants)
        => variants.Where(v => v.HasVideo).MinBy(v => v.Bandwidth) ?? variants.MinBy(v => v.Bandwidth);

    /// <summary>The segment URIs a media playlist lists: each URI line that follows an <c>#EXTINF</c> tag.</summary>
    /// <param name="media">Media playlist text.</param>
    /// <param name="baseUri">The playlist's own URL, for resolving relative URIs.</param>
    /// <returns>Absolute segment URLs in playlist order; empty if this isn't a playlist.</returns>
    public static List<string> SegmentUrls(string media, Uri baseUri)
    {
        var list = new List<string>();
        if (!IsPlaylist(media))
            return list;
        var pending = false;
        foreach (var raw in media.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            if (line.StartsWith('#'))
            {
                if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
                    pending = true;
                continue;
            }
            if (pending && Resolve(baseUri, line) is { } url)
                list.Add(url);
            pending = false;
        }
        return list;
    }

    /// <summary>Whether the text is an HLS playlist at all (usher answers errors as JSON).</summary>
    /// <param name="text">Response body.</param>
    static bool IsPlaylist(string text) => text.TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal);

    /// <summary>An attribute's value from an HLS tag's attribute list (quoted values may contain commas).</summary>
    /// <param name="tag">The full tag line.</param>
    /// <param name="name">Attribute name, e.g. "BANDWIDTH".</param>
    /// <returns>The unquoted value, or null when the attribute is absent.</returns>
    static string? Attribute(string tag, string name)
    {
        var m = Regex.Match(tag, $"[:,]{name}=(?:\"([^\"]*)\"|([^,]*))");
        return !m.Success ? null : m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
    }

    /// <summary>Resolve a playlist URI line to an absolute http(s) URL. An absolute line is returned verbatim
    /// (its signature must not be re-encoded); a relative one is resolved against the playlist URL.</summary>
    /// <param name="baseUri">The playlist's own URL.</param>
    /// <param name="reference">The URI line.</param>
    /// <returns>The absolute URL, or null when it isn't a usable http(s) URL.</returns>
    static string? Resolve(Uri baseUri, string reference)
    {
        if (Uri.TryCreate(reference, UriKind.Absolute, out var abs))
            return abs.Scheme == Uri.UriSchemeHttps || abs.Scheme == Uri.UriSchemeHttp ? reference : null;
        return Uri.TryCreate(baseUri, reference, out var rel) ? rel.AbsoluteUri : null;
    }
}
