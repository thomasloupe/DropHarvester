using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DropHarvester.Models.Auth;
using DropHarvester.Models.Twitch;

namespace DropHarvester.Services.Twitch;

/// <summary>Owns login state: the token from Twitch's browser sign-in, its validation and persistence.</summary>
public interface ITwitchAuth
{
    AuthState State { get; }
    bool IsLoggedIn { get; }

    /// <summary>Raised (on any thread) whenever login state changes.</summary>
    event Action? AuthChanged;

    /// <summary>Log in with the access token from the browser sign-in (<see cref="TwitchConstants.BrowserLoginUrl"/>):
    /// validate it, require it to belong to the Android app client, and persist the session.</summary>
    /// <param name="tokenOrUrl">The bare access token, or the whole twitch.tv address it arrived in.</param>
    /// <param name="ct">Token to cancel the validation.</param>
    /// <returns>Success when the session is saved; otherwise why the token was not used.</returns>
    Task<TokenLoginResult> LogInWithTokenAsync(string tokenOrUrl, CancellationToken ct = default);

    /// <summary>Validate the stored token against Twitch; clears it on rejection. Returns true if valid.</summary>
    /// <param name="ct">Token to cancel the request.</param>
    /// <returns>True when the token validates; false otherwise.</returns>
    Task<bool> ValidateAsync(CancellationToken ct = default);

    /// <summary>Forget the token and stored session.</summary>
    void LogOut();
}

/// <summary>
/// Takes the access token produced by Twitch's browser sign-in (an implicit grant for the Android app
/// client), validates it to learn the user id / login, and persists it via <see cref="IAuthStore"/> so it
/// is reused on restart.
/// </summary>
public sealed class TwitchAuthService : ITwitchAuth, IDisposable
{
    // "access_token=<token>" in a redirect URL's fragment or query
    static readonly Regex TokenInUrl = new(@"[#&?]access_token=([A-Za-z0-9]+)", RegexOptions.Compiled);
    static readonly Regex BareToken = new(@"^[A-Za-z0-9]{20,64}$", RegexOptions.Compiled);

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

    /// <summary>Pull the access token out of a browser sign-in redirect URL
    /// (<c>https://www.twitch.tv/#access_token=...</c>).</summary>
    /// <param name="url">The URL the sign-in page navigated to.</param>
    /// <returns>The access token, or null when the URL is not the sign-in redirect.</returns>
    public static string? TokenFromRedirectUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)
            || !url.StartsWith(TwitchConstants.BrowserLoginRedirectPrefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var m = TokenInUrl.Match(url);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Validates a token from the browser sign-in and saves the session when it belongs to the
    /// Android app client; anything else is discarded and the user stays logged out.</summary>
    /// <param name="tokenOrUrl">The bare access token, or the whole twitch.tv address it arrived in.</param>
    /// <param name="ct">Token to cancel the validation.</param>
    /// <returns>Success when the session is saved; otherwise why the token was not used.</returns>
    public async Task<TokenLoginResult> LogInWithTokenAsync(string tokenOrUrl, CancellationToken ct = default)
    {
        var text = tokenOrUrl?.Trim() ?? "";
        var token = TokenFromRedirectUrl(text) ?? (BareToken.IsMatch(text) ? text : null);
        if (token is null)
            return TokenLoginResult.NoToken;

        State.AccessToken = token;
        if (!await ValidateAsync(ct).ConfigureAwait(false))
        {
            LogOut();
            return TokenLoginResult.Rejected;
        }
        // GraphQL is sent as the Android app client, and Twitch only honors a token with its own client id.
        if (!string.Equals(State.ClientId, TwitchConstants.AndroidAppClientId, StringComparison.Ordinal))
        {
            LogOut();
            return TokenLoginResult.WrongClient;
        }
        return TokenLoginResult.Success;
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
