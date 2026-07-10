using System.Net;
using System.Text;
using System.Text.Json;
using DropHarvester.Models.Events;
using DropHarvester.Models.Twitch;
using DropHarvester.Services;

namespace DropHarvester.Services.Twitch;

/// <summary>Raised when Twitch rejects the GQL request due to an invalid/expired token (HTTP 401).</summary>
public sealed class GqlAuthException : Exception
{
    /// <summary>Creates the exception with the given message.</summary>
    /// <param name="message">Description of the auth failure.</param>
    public GqlAuthException(string message) : base(message) { }
}

/// <summary>
/// Thin client for Twitch's private GraphQL endpoint. Supports persisted-query operations (the
/// normal case) and raw-query mutations (the stream-less watch). Uses the web client id + the
/// logged-in user's OAuth token, mirroring a browser.
/// </summary>
public interface IGqlClient
{
    /// <summary>Run a persisted-query operation; returns the cloned response root element.</summary>
    /// <param name="operationName">Twitch GraphQL operation name.</param>
    /// <param name="sha256Hash">Persisted-query hash for the operation.</param>
    /// <param name="variables">Operation variables, or null for none.</param>
    /// <param name="ct">Token to cancel the request.</param>
    /// <returns>Cloned root JSON element of the response.</returns>
    Task<JsonElement> PersistedAsync(string operationName, string sha256Hash, object? variables, CancellationToken ct = default);

    /// <summary>Run a raw-query mutation (used only by the watch payload).</summary>
    /// <param name="query">Raw GraphQL query text.</param>
    /// <param name="variables">Query variables.</param>
    /// <param name="ct">Token to cancel the request.</param>
    /// <returns>Cloned root JSON element of the response.</returns>
    Task<JsonElement> RawAsync(string query, object variables, CancellationToken ct = default);

    /// <summary>Debug-only: POST a raw query ONCE (no retry masking) and return the full HTTP round-trip
    /// (status, body, and the headers we actually sent, token redacted) so a 204-acked-but-not-credited
    /// watch can be inspected end to end.</summary>
    /// <param name="query">Raw GraphQL query text.</param>
    /// <param name="variables">Query variables.</param>
    /// <param name="ct">Token to cancel the request.</param>
    /// <returns>The status code, response body, and redacted sent headers.</returns>
    Task<GqlProbeResult> ProbeRawAsync(string query, object variables, CancellationToken ct = default);
}

/// <summary>Full HTTP detail of a one-shot debug probe: the status Twitch returned, the raw response body,
/// the headers we actually sent (Authorization redacted), and the request JSON.</summary>
/// <param name="HttpStatus">HTTP status code Twitch returned.</param>
/// <param name="ResponseBody">Raw response body text.</param>
/// <param name="SentHeaders">Headers sent on the request, with the token redacted.</param>
/// <param name="RequestJson">The serialized GraphQL request body.</param>
public sealed record GqlProbeResult(int HttpStatus, string ResponseBody, Dictionary<string, string> SentHeaders, string RequestJson);

/// <summary>Default <see cref="IGqlClient"/> posting to Twitch's private GraphQL endpoint.</summary>
public sealed class GqlClient : IGqlClient, IDisposable
{
    static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    readonly ITwitchAuth _auth;
    readonly IHarvesterEventBus _bus;
    readonly IIntegrityService _integrity;
    readonly HttpClient _http;
    // Stable per-run session id; Twitch uses it to tie minute-watched events to one watch
    // session. Without it the watch is accepted (204) but not credited toward drops.
    readonly string _sessionId = System.Security.Cryptography.RandomNumberGenerator.GetHexString(16).ToLowerInvariant();

    // Bounded retry-with-backoff for transient failures (network errors, timeouts, 5xx, 429). Auth
    // (401) and user cancellation are never retried.
    const int MaxAttempts = 3;

    /// <summary>Creates the client with auth, integrity, event bus, and an HttpClient built from settings.</summary>
    /// <param name="auth">Twitch auth supplying tokens and client/device ids.</param>
    /// <param name="integrity">Supplies the Client-Integrity token the watch mutation needs to be credited.</param>
    /// <param name="settings">Settings store used to build the HTTP handler.</param>
    /// <param name="bus">Event bus used to publish warning logs.</param>
    public GqlClient(ITwitchAuth auth, IIntegrityService integrity, ISettingsStore settings, IHarvesterEventBus bus)
    {
        _auth = auth;
        _integrity = integrity;
        _bus = bus;
        _http = new HttpClient(HttpClientBuilder.CreateHandler(settings));
    }

    /// <summary>Builds the persisted-query request payload and sends it.</summary>
    /// <param name="operationName">Twitch GraphQL operation name.</param>
    /// <param name="sha256Hash">Persisted-query hash for the operation.</param>
    /// <param name="variables">Operation variables, or null for none.</param>
    /// <param name="ct">Token to cancel the request.</param>
    /// <returns>Cloned root JSON element of the response.</returns>
    public Task<JsonElement> PersistedAsync(string operationName, string sha256Hash, object? variables, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>
        {
            ["operationName"] = operationName,
            ["variables"] = variables ?? new { },
            ["extensions"] = new
            {
                persistedQuery = new { version = TwitchConstants.Gql.Version, sha256Hash },
            },
        };
        return SendAsync(payload, operationName, withIntegrity: false, ct);
    }

    /// <summary>Builds the raw-query request payload and sends it.</summary>
    /// <param name="query">Raw GraphQL query text.</param>
    /// <param name="variables">Query variables.</param>
    /// <param name="ct">Token to cancel the request.</param>
    /// <returns>Cloned root JSON element of the response.</returns>
    public Task<JsonElement> RawAsync(string query, object variables, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>
        {
            ["query"] = query,
            ["variables"] = variables,
        };
        return SendAsync(payload, "watch", withIntegrity: true, ct);
    }

    /// <summary>Debug-only: POST a raw query once and capture the full HTTP round-trip for inspection.</summary>
    /// <param name="query">Raw GraphQL query text.</param>
    /// <param name="variables">Query variables.</param>
    /// <param name="ct">Token to cancel the request.</param>
    /// <returns>The status code, raw body, and redacted sent headers.</returns>
    public async Task<GqlProbeResult> ProbeRawAsync(string query, object variables, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?> { ["query"] = query, ["variables"] = variables };
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        var integrity = await _integrity.GetAsync(ct).ConfigureAwait(false);
        using var req = new HttpRequestMessage(HttpMethod.Post, TwitchConstants.GqlUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        ApplyHeaders(req, integrity?.ClientId, integrity?.DeviceId);
        if (integrity is not null)
            req.Headers.TryAddWithoutValidation("Client-Integrity", integrity.Token);

        var sent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in req.Headers)
            sent[h.Key] = h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                ? $"OAuth <redacted, {string.Concat(h.Value).Length - 6} chars>"
                : h.Key.Equals("Client-Integrity", StringComparison.OrdinalIgnoreCase)
                    ? $"<redacted, {string.Concat(h.Value).Length} chars>"
                    : string.Join(", ", h.Value);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return new GqlProbeResult((int)resp.StatusCode, body, sent, json);
    }

    /// <summary>Posts the payload with bounded retry/backoff, mapping HTTP 401 to GqlAuthException.</summary>
    /// <param name="payload">Serializable GraphQL request body.</param>
    /// <param name="op">Operation label used in log messages.</param>
    /// <param name="withIntegrity">Attach a Client-Integrity token (required for the watch to be credited).</param>
    /// <param name="ct">Token to cancel the request.</param>
    /// <returns>Cloned root JSON element of the successful response.</returns>
    async Task<JsonElement> SendAsync(object payload, string op, bool withIntegrity, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        // Fetched once per send (cached across sends); the watch mutation is not credited without it.
        // The integrity token dictates the Client-Id to send (a token is bound to its client-id).
        var integrity = withIntegrity ? await _integrity.GetAsync(ct).ConfigureAwait(false) : null;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, TwitchConstants.GqlUrl)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
                ApplyHeaders(req, integrity?.ClientId, integrity?.DeviceId);
                if (integrity is not null)
                    req.Headers.TryAddWithoutValidation("Client-Integrity", integrity.Token);

                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                    throw new GqlAuthException("Twitch rejected the token (401).");

                if (IsRetryableStatus(resp.StatusCode) && attempt < MaxAttempts)
                {
                    var wait = RetryAfter(resp) ?? Backoff(attempt);
                    Log($"Twitch '{op}' returned {(int)resp.StatusCode}; retrying in {wait.TotalSeconds:0.#}s (attempt {attempt}/{MaxAttempts - 1}).", HarvesterLogLevel.Warn);
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                    continue;
                }

                resp.EnsureSuccessStatusCode();
                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                return doc.RootElement.Clone();
            }
            catch (GqlAuthException) { throw; }                                    // never retry auth
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } // real shutdown
            catch (Exception ex) when (attempt < MaxAttempts && IsTransient(ex))
            {
                var wait = Backoff(attempt);
                Log($"Twitch '{op}' failed ({ex.GetType().Name}: {ex.Message}); retrying in {wait.TotalSeconds:0.#}s (attempt {attempt}/{MaxAttempts - 1}).", HarvesterLogLevel.Warn);
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>True for HTTP 429 and 5xx status codes.</summary>
    /// <param name="s">HTTP status to test.</param>
    static bool IsRetryableStatus(HttpStatusCode s) => (int)s == 429 || (int)s >= 500;

    /// <summary>True for network/timeout/IO exceptions that are safe to retry.</summary>
    /// <param name="ex">Exception to classify.</param>
    static bool IsTransient(Exception ex)
        => ex is HttpRequestException or TaskCanceledException or TimeoutException or IOException;

    /// <summary>Exponential backoff delay (0.5s, 1s, 2s) for the given attempt.</summary>
    /// <param name="attempt">1-based attempt number.</param>
    /// <returns>Delay to wait before the next attempt.</returns>
    static TimeSpan Backoff(int attempt) => TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1));

    /// <summary>Parsed Retry-After delay, capped at 30s so a bad header can't stall harvesting; null when absent or invalid.</summary>
    /// <param name="resp">Response whose Retry-After header is read.</param>
    /// <returns>The capped delay, or null when there is no usable Retry-After.</returns>
    static TimeSpan? RetryAfter(HttpResponseMessage resp)
    {
        var ra = resp.Headers.RetryAfter;
        var delta = ra?.Delta ?? (ra?.Date is { } when ? when - DateTimeOffset.UtcNow : (TimeSpan?)null);
        return delta is { } d && d > TimeSpan.Zero && d <= TimeSpan.FromSeconds(30) ? d : null;
    }

    /// <summary>Publishes a log event on the harvester event bus.</summary>
    /// <param name="message">Log message text.</param>
    /// <param name="level">Severity of the log entry.</param>
    void Log(string message, HarvesterLogLevel level) => _bus.Publish(new LogEvent(message, level));

    /// <summary>Adds the Twitch client, session, origin, and auth headers to the request.</summary>
    /// <param name="req">Request the headers are added to.</param>
    /// <param name="clientIdOverride">Client-Id to send instead of the default Android one (the watch uses
    /// the client-id its integrity token is bound to); null keeps the default.</param>
    /// <param name="deviceIdOverride">X-Device-Id to send instead of the app's own (the watch uses the
    /// device-id its integrity token is bound to); null keeps the app's device id.</param>
    void ApplyHeaders(HttpRequestMessage req, string? clientIdOverride = null, string? deviceIdOverride = null)
    {
        var state = _auth.State;
        // Default to the Android TV client id (the one the device token was issued for). The web client
        // id triggers Twitch's Kasada integrity gate ("failed integrity check") which returns empty drop
        // campaigns; the mobile/TV client bypasses it. The watch overrides this to the client-id its
        // integrity token was minted for, so the token and client-id stay a matched pair.
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        req.Headers.TryAddWithoutValidation("Accept-Language", "en-US");
        req.Headers.TryAddWithoutValidation("Pragma", "no-cache");
        req.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
        req.Headers.TryAddWithoutValidation("Client-Id", clientIdOverride ?? TwitchConstants.AndroidAppClientId);
        req.Headers.TryAddWithoutValidation("User-Agent", TwitchConstants.AndroidUserAgent);
        // Client-Session-Id ties minute-watched events to one watch session so drops are credited.
        req.Headers.TryAddWithoutValidation("Client-Session-Id", _sessionId);
        req.Headers.TryAddWithoutValidation("Origin", "https://www.twitch.tv");
        req.Headers.TryAddWithoutValidation("Referer", "https://www.twitch.tv");
        var deviceId = string.IsNullOrEmpty(deviceIdOverride) ? state.DeviceId : deviceIdOverride;
        if (!string.IsNullOrEmpty(deviceId))
            req.Headers.TryAddWithoutValidation("X-Device-Id", deviceId);
        if (!string.IsNullOrEmpty(state.AccessToken))
            req.Headers.TryAddWithoutValidation("Authorization", $"OAuth {state.AccessToken}");
    }

    /// <summary>Disposes the underlying HttpClient.</summary>
    public void Dispose() => _http.Dispose();
}
