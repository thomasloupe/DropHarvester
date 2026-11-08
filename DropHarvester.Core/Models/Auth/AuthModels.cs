using System.Text.Json.Serialization;

namespace DropHarvester.Models.Auth;

/// <summary>Outcome of logging in with a token from the browser sign-in.</summary>
public enum TokenLoginResult
{
    /// <summary>The token validated and the session is saved.</summary>
    Success,
    /// <summary>No access token could be found in the text that was handed in.</summary>
    NoToken,
    /// <summary>Twitch did not accept the token (revoked, expired, or unreachable).</summary>
    Rejected,
    /// <summary>The token belongs to a different Twitch client than the one the app runs as.</summary>
    WrongClient,
}

/// <summary>Response from GET https://id.twitch.tv/oauth2/validate.</summary>
public sealed class ValidateResponse
{
    [JsonPropertyName("client_id")] public string ClientId { get; set; } = "";
    [JsonPropertyName("login")] public string Login { get; set; } = "";
    [JsonPropertyName("user_id")] public string UserId { get; set; } = "";
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    [JsonPropertyName("scopes")] public string[]? Scopes { get; set; }
}

/// <summary>
/// Persisted authentication state. Stored as JSON in the app data folder; reloaded on start,
/// cleared on logout or when the token is rejected.
/// </summary>
public sealed class AuthState
{
    public string? AccessToken { get; set; }
    public string? UserId { get; set; }
    public string? Username { get; set; }
    /// <summary>Twitch display name (e.g. "aSpyda" vs the login "aspyda"); may differ in casing.</summary>
    public string? DisplayName { get; set; }
    public string? ClientId { get; set; }

    /// <summary>Stable per-install device id sent as X-Device-Id; generated once, kept forever.</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>UTC time the token was last validated against Twitch.</summary>
    public DateTimeOffset? ValidatedAtUtc { get; set; }

    [JsonIgnore]
    public bool IsLoggedIn => !string.IsNullOrEmpty(AccessToken) && !string.IsNullOrEmpty(UserId);
}
