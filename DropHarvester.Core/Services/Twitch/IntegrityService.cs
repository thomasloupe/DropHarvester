using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DropHarvester.Models.Events;
using DropHarvester.Models.Twitch;

namespace DropHarvester.Services.Twitch;

/// <summary>
/// Mints and caches a Twitch <c>Client-Integrity</c> token. Twitch requires this token on the
/// <c>sendSpadeEvents</c> watch mutation for drop progress to be credited; without it the request is
/// still acked (HTTP 204) but silently not credited. The token is fetched from the integrity endpoint
/// with the same client/device/auth identity the watch uses, cached until shortly before its stated
/// expiration, and re-minted on demand.
/// </summary>
/// <summary>An integrity token together with the client-id and device-id it was minted for; the watch must
/// send all three as a matched set (an integrity token is bound to the client-id and device-id that
/// requested it). A null <see cref="DeviceId"/> means "use the app's own device id".</summary>
/// <param name="Token">The Client-Integrity token value.</param>
/// <param name="ClientId">The Client-Id the token is bound to (and the watch must use).</param>
/// <param name="DeviceId">The X-Device-Id the token is bound to, or null to use the app's device id.</param>
public sealed record IntegrityInfo(string Token, string ClientId, string? DeviceId);

public interface IIntegrityService
{
    /// <summary>Return a cached (or freshly minted) integrity token plus the client-id to pair it with,
    /// or null when none is available.</summary>
    /// <param name="ct">Token to cancel the mint request.</param>
    /// <returns>The integrity token + client-id, or null.</returns>
    Task<IntegrityInfo?> GetAsync(CancellationToken ct = default);

    /// <summary>Drop the cached token so the next request re-mints (e.g. after an integrity rejection).</summary>
    void Invalidate();

    /// <summary>Testing hook: inject an externally-obtained integrity token (e.g. a real Kasada-backed one)
    /// bound to the given client-id and device-id, overriding the headless mint until it expires.</summary>
    /// <param name="token">The integrity token to use.</param>
    /// <param name="clientId">The client-id the token is bound to (the watch will use it too).</param>
    /// <param name="deviceId">The device-id the token is bound to (the watch will send it too), or null.</param>
    /// <param name="ttlMinutes">How long to trust the injected token.</param>
    void SetOverride(string token, string clientId, string? deviceId, double ttlMinutes);

    /// <summary>Diagnostic snapshot of the current cached token state (redacted), for the debug server.</summary>
    /// <returns>An object describing whether a token is cached and when it expires.</returns>
    object DebugState();
}

/// <summary>Default <see cref="IIntegrityService"/> that mints tokens from Twitch's integrity endpoint.</summary>
public sealed class IntegrityService : IIntegrityService, IDisposable
{
    // Re-mint this long before the stated expiry so a request never rides an about-to-expire token.
    static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    readonly ITwitchAuth _auth;
    readonly IHarvesterEventBus _bus;
    readonly HttpClient _http;
    readonly SemaphoreSlim _gate = new(1, 1);

    string? _token;
    string _clientId = TwitchConstants.AndroidAppClientId;
    string? _deviceId; // null = use the app's own device id
    DateTimeOffset _expiresUtc = DateTimeOffset.MinValue;
    bool _isOverride;

    /// <summary>Creates the service with auth (for the identity headers) and an HttpClient from settings.</summary>
    /// <param name="auth">Twitch auth supplying the token, device id, and client id.</param>
    /// <param name="settings">Settings store used to build the HTTP handler (proxy etc.).</param>
    /// <param name="bus">Event bus used to publish mint success/failure logs.</param>
    public IntegrityService(ITwitchAuth auth, ISettingsStore settings, IHarvesterEventBus bus)
    {
        _auth = auth;
        _bus = bus;
        _http = new HttpClient(HttpClientBuilder.CreateHandler(settings));
    }

    /// <summary>Returns the cached token+client-id when still fresh, otherwise mints a new one under a
    /// lock. An injected override token is never re-minted over while it is fresh.</summary>
    /// <param name="ct">Token to cancel the mint request.</param>
    /// <returns>The integrity token + client-id, or null when unavailable.</returns>
    public async Task<IntegrityInfo?> GetAsync(CancellationToken ct = default)
    {
        if (IsFresh())
            return new IntegrityInfo(_token!, _clientId, _deviceId);

        // Never silently re-mint a headless token over an injected (real) one; if the override expired,
        // fall through and mint, but that headless token won't credit - the override must be refreshed.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsFresh())
                return new IntegrityInfo(_token!, _clientId, _deviceId);
            var minted = await MintAsync(ct).ConfigureAwait(false);
            return minted is null ? null : new IntegrityInfo(minted, _clientId, _deviceId);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Injects an externally-obtained integrity token bound to a client-id + device-id (testing hook).</summary>
    /// <param name="token">The integrity token to use.</param>
    /// <param name="clientId">The client-id the token is bound to.</param>
    /// <param name="deviceId">The device-id the token is bound to, or null to keep the app's device id.</param>
    /// <param name="ttlMinutes">Minutes to trust the injected token.</param>
    public void SetOverride(string token, string clientId, string? deviceId, double ttlMinutes)
    {
        _token = token;
        _clientId = clientId;
        _deviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
        _expiresUtc = DateTimeOffset.UtcNow.AddMinutes(ttlMinutes);
        _isOverride = true;
        _bus.Publish(new LogEvent($"Integrity token injected (client {clientId[..Math.Min(8, clientId.Length)]}..., device {(_deviceId is null ? "app" : "override")}, ~{ttlMinutes:0}m)."));
    }

    /// <summary>True while a token is cached and outside the refresh margin before its expiry.</summary>
    bool IsFresh() => _token is not null && DateTimeOffset.UtcNow < _expiresUtc - RefreshMargin;

    /// <summary>Posts to the integrity endpoint with the watch identity and caches the returned token.</summary>
    /// <param name="ct">Token to cancel the mint request.</param>
    /// <returns>The freshly minted token, or null on failure.</returns>
    async Task<string?> MintAsync(CancellationToken ct)
    {
        var state = _auth.State;
        if (string.IsNullOrEmpty(state.AccessToken))
            return null;

        using var req = new HttpRequestMessage(HttpMethod.Post, TwitchConstants.GqlIntegrityUrl)
        {
            Content = new StringContent("", Encoding.UTF8, "application/json"),
        };
        // Mint with the SAME identity the watch sends, so the token is bound to that client/device/user.
        req.Headers.TryAddWithoutValidation("Client-Id", TwitchConstants.AndroidAppClientId);
        req.Headers.TryAddWithoutValidation("User-Agent", TwitchConstants.AndroidUserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        if (!string.IsNullOrEmpty(state.DeviceId))
            req.Headers.TryAddWithoutValidation("X-Device-Id", state.DeviceId);
        req.Headers.TryAddWithoutValidation("Authorization", $"OAuth {state.AccessToken}");

        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("token", out var tok) && tok.ValueKind == JsonValueKind.String)
            {
                _token = tok.GetString();
                _clientId = TwitchConstants.AndroidAppClientId; // a headless mint is bound to the Android client
                _deviceId = null; // ... and to the app's own device id
                _isOverride = false;
                _expiresUtc = root.TryGetProperty("expiration", out var exp) && exp.TryGetInt64(out var ms) && ms > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                    : DateTimeOffset.UtcNow.AddMinutes(30);
                _bus.Publish(new LogEvent($"Integrity token minted (valid ~{(_expiresUtc - DateTimeOffset.UtcNow).TotalMinutes:0}m)."));
                return _token;
            }

            _bus.Publish(new LogEvent($"Integrity mint returned no token (HTTP {(int)resp.StatusCode}).", HarvesterLogLevel.Warn));
            return null;
        }
        catch (Exception ex)
        {
            _bus.Publish(new LogEvent($"Integrity mint failed: {ex.Message}", HarvesterLogLevel.Warn));
            return null;
        }
    }

    /// <summary>Clears the cached token so the next call re-mints.</summary>
    public void Invalidate()
    {
        _token = null;
        _clientId = TwitchConstants.AndroidAppClientId;
        _deviceId = null;
        _isOverride = false;
        _expiresUtc = DateTimeOffset.MinValue;
    }

    /// <summary>Redacted snapshot of the cache state for the debug server.</summary>
    /// <returns>An object with token presence, length, client-id, and expiry.</returns>
    public object DebugState() => new
    {
        HasToken = _token is not null,
        TokenLength = _token?.Length ?? 0,
        ClientId = _clientId,
        DeviceIdOverride = _deviceId is not null,
        IsInjectedOverride = _isOverride,
        ExpiresUtc = _expiresUtc == DateTimeOffset.MinValue ? null : (DateTimeOffset?)_expiresUtc,
        MinutesRemaining = _token is null ? 0 : Math.Round((_expiresUtc - DateTimeOffset.UtcNow).TotalMinutes, 1),
        Fresh = IsFresh(),
    };

    /// <summary>Disposes the underlying HttpClient.</summary>
    public void Dispose() => _http.Dispose();
}
