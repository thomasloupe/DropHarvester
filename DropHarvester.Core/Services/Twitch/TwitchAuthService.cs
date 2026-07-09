using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using DropHarvester.Models.Auth;
using DropHarvester.Models.Twitch;

namespace DropHarvester.Services.Twitch;

/// <summary>Owns login state and drives Twitch's OAuth device-code flow.</summary>
public interface ITwitchAuth
{
    AuthState State { get; }
    bool IsLoggedIn { get; }

    /// <summary>Raised (on any thread) whenever login state changes.</summary>
    event Action? AuthChanged;

    /// <summary>Kick off a device-code login; returns the code + URL to show the user.</summary>
    /// <param name="ct">Token to cancel the request.</param>
    /// <returns>Device-code response with the user code and verification URL.</returns>
    Task<DeviceCodeResponse> BeginDeviceLoginAsync(CancellationToken ct = default);

    /// <summary>Poll until the user authorizes (or the code expires / is cancelled).</summary>
    /// <param name="device">Device-code response to poll against.</param>
    /// <param name="ct">Token to cancel polling.</param>
    /// <returns>True once authorized and validated; false on expiry, denial, or cancellation.</returns>
    Task<bool> AwaitAuthorizationAsync(DeviceCodeResponse device, CancellationToken ct = default);

    /// <summary>Validate the stored token against Twitch; clears it on rejection. Returns true if valid.</summary>
    /// <param name="ct">Token to cancel the request.</param>
    /// <returns>True when the token validates; false otherwise.</returns>
    Task<bool> ValidateAsync(CancellationToken ct = default);

    /// <summary>Forget the token and stored session.</summary>
    void LogOut();
}

/// <summary>
/// Device-code OAuth against id.twitch.tv. Logs in as the
/// Android TV app client (which has the device grant enabled), then validates the token to learn
/// the user id / login. The token is persisted via <see cref="IAuthStore"/> and reused on restart.
/// </summary>
public sealed class TwitchAuthService : ITwitchAuth, IDisposable
{
    readonly IAuthStore _store;
    readonly HttpClient _http;

    public AuthState State { get; private set; }
    public bool IsLoggedIn => State.IsLoggedIn;
    public event Action? AuthChanged;

    /// <summary>Loads persisted auth state, ensures a stable device id, and builds the HttpClient.</summary>
    /// <param name="store">Store the auth state is loaded from and saved to.</param>
    /// <param name="settings">Settings store used to build the HTTP handler.</param>
    public TwitchAuthService(IAuthStore store, ISettingsStore settings)
    {
        _store = store;
        State = store.Load();
        if (string.IsNullOrEmpty(State.DeviceId))
        {
            State.DeviceId = NewDeviceId();
            _store.Save(State);
        }

        _http = new HttpClient(HttpClientBuilder.CreateHandler(settings));
    }

    /// <summary>Requests a device code from Twitch's OAuth device endpoint.</summary>
    /// <param name="ct">Token to cancel the request.</param>
    /// <returns>The device-code response from Twitch.</returns>
    public async Task<DeviceCodeResponse> BeginDeviceLoginAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, TwitchConstants.OAuthDeviceUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = TwitchConstants.AndroidAppClientId,
                ["scopes"] = TwitchConstants.OAuthScopes,
            }),
        };
        ApplyOAuthHeaders(req);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var device = await resp.Content.ReadFromJsonAsync<DeviceCodeResponse>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Empty device-code response from Twitch.");
        return device;
    }

    /// <summary>Polls the token endpoint at the server interval until the user authorizes or time runs out.</summary>
    /// <param name="device">Device-code response being polled.</param>
    /// <param name="ct">Token to cancel polling.</param>
    /// <returns>True once authorized and validated; false on expiry, denial, or cancellation.</returns>
    public async Task<bool> AwaitAuthorizationAsync(DeviceCodeResponse device, CancellationToken ct = default)
    {
        // Poll no faster than the server-provided interval (default 5s), backing off on slow_down.
        var interval = TimeSpan.FromSeconds(Math.Max(1, device.Interval == 0 ? 5 : device.Interval));
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(device.ExpiresIn > 0 ? device.ExpiresIn : 1800);

        while (!ct.IsCancellationRequested && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(interval, ct).ConfigureAwait(false);

            using var req = new HttpRequestMessage(HttpMethod.Post, TwitchConstants.OAuthTokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = TwitchConstants.AndroidAppClientId,
                    ["device_code"] = device.DeviceCode,
                    ["grant_type"] = TwitchConstants.DeviceCodeGrantType,
                }),
            };
            ApplyOAuthHeaders(req);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.OK)
            {
                var token = await resp.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct).ConfigureAwait(false);
                if (token is not null && !string.IsNullOrEmpty(token.AccessToken))
                {
                    State.AccessToken = token.AccessToken;
                    _store.Save(State);
                    // Learn user id / login and confirm the token is good.
                    return await ValidateAsync(ct).ConfigureAwait(false);
                }
            }
            else
            {
                // 400 while pending; body carries the OAuth error message.
                var message = await SafeReadOAuthMessageAsync(resp, ct).ConfigureAwait(false);
                switch (message)
                {
                    case "authorization_pending":
                        break; // keep waiting
                    case "slow_down":
                        interval += TimeSpan.FromSeconds(5);
                        break;
                    case "expired_token":
                    case "access_denied":
                        return false; // user declined or code expired
                    default:
                        break; // unknown -> keep trying until the deadline
                }
            }
        }

        return false;
    }

    /// <summary>Validates the token, populates user id/login/display name and persists; logs out on 401.</summary>
    /// <param name="ct">Token to cancel the request.</param>
    /// <returns>True when the token validates and yields a user id; false otherwise.</returns>
    public async Task<bool> ValidateAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(State.AccessToken))
            return false;

        using var req = new HttpRequestMessage(HttpMethod.Get, TwitchConstants.OAuthValidateUrl);
        req.Headers.TryAddWithoutValidation("Authorization", $"OAuth {State.AccessToken}");
        req.Headers.TryAddWithoutValidation("User-Agent", TwitchConstants.AndroidUserAgent);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            LogOut();
            return false;
        }
        if (!resp.IsSuccessStatusCode)
            return false;

        var v = await resp.Content.ReadFromJsonAsync<ValidateResponse>(cancellationToken: ct).ConfigureAwait(false);
        if (v is null || string.IsNullOrEmpty(v.UserId))
            return false;

        State.UserId = v.UserId;
        State.Username = v.Login;
        State.ClientId = v.ClientId;
        State.ValidatedAtUtc = DateTimeOffset.UtcNow;
        await FetchDisplayNameAsync(ct).ConfigureAwait(false); // best-effort; keeps proper casing (aSpyda)
        _store.Save(State);
        AuthChanged?.Invoke();
        return true;
    }

    /// <summary>Fetch the properly-cased Twitch display name (differs from the login, e.g. aSpyda vs
    /// aspyda). Best-effort - leaves DisplayName null on failure and the UI falls back to the login.</summary>
    /// <param name="ct">Token to cancel the request.</param>
    async Task FetchDisplayNameAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, TwitchConstants.GqlUrl)
            {
                Content = new StringContent(
                    "{\"query\":\"query { currentUser { displayName } }\"}",
                    System.Text.Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("Client-Id", TwitchConstants.AndroidAppClientId);
            req.Headers.TryAddWithoutValidation("Authorization", $"OAuth {State.AccessToken}");
            req.Headers.TryAddWithoutValidation("User-Agent", TwitchConstants.AndroidUserAgent);
            req.Headers.TryAddWithoutValidation("X-Device-Id", State.DeviceId);
            req.Headers.TryAddWithoutValidation("Origin", "https://www.twitch.tv");
            req.Headers.TryAddWithoutValidation("Referer", "https://www.twitch.tv");

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("currentUser", out var user)
                && user.ValueKind == JsonValueKind.Object
                && user.TryGetProperty("displayName", out var dn)
                && dn.GetString() is { Length: > 0 } name)
            {
                State.DisplayName = name;
            }
        }
        catch
        {
            // ignore; DisplayName stays null
        }
    }

    /// <summary>Clears all auth state except the stable device id, persists, and raises AuthChanged.</summary>
    public void LogOut()
    {
        var deviceId = State.DeviceId; // keep the stable device id across logins
        State = new AuthState { DeviceId = deviceId };
        _store.Save(State);
        AuthChanged?.Invoke();
    }

    /// <summary>Adds the Twitch client, device, and user-agent headers for OAuth requests.</summary>
    /// <param name="req">Request the headers are added to.</param>
    void ApplyOAuthHeaders(HttpRequestMessage req)
    {
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("Client-Id", TwitchConstants.AndroidAppClientId);
        req.Headers.TryAddWithoutValidation("User-Agent", TwitchConstants.AndroidUserAgent);
        req.Headers.TryAddWithoutValidation("X-Device-Id", State.DeviceId);
    }

    /// <summary>Reads the OAuth error message from a response body, or null when unreadable.</summary>
    /// <param name="resp">Response whose JSON body is parsed.</param>
    /// <param name="ct">Token to cancel the read.</param>
    /// <returns>The error message string, or null when the body is missing or malformed.</returns>
    static async Task<string?> SafeReadOAuthMessageAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("message", out var m))
                return m.GetString();
        }
        catch
        {
            // ignore malformed error bodies
        }
        return null;
    }

    /// <summary>Generates a random lowercase 32-char hex device id.</summary>
    /// <returns>A 32-character lowercase hex string.</returns>
    static string NewDeviceId()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Disposes the underlying HttpClient.</summary>
    public void Dispose() => _http.Dispose();
}
