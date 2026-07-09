namespace DropHarvester.Models.Twitch;

/// <summary>
/// Central Twitch endpoints, client identities, GraphQL persisted-query hashes and tuning
/// constants. Kept in one place so that when Twitch rotates a persisted-query hash or client
/// id it is a one-line change here.
/// </summary>
public static class TwitchConstants
{
    // ----- Client identities -----
    // The device-code login flow runs as the Android TV app client (it has the device grant
    // enabled). GraphQL requests use the web client id, same as a browser.
    public const string AndroidAppClientId = "kd1unb4b3q4t58fwlpcbzcbnm76a8fp";
    public const string WebClientId = "kimne78kx3ncx6brgo4mv6wki5h1ko";
    public const string SmartboxClientId = "ue6666qo983tsx6so1t0vnawi233wa";

    public const string AndroidUserAgent =
        "Dalvik/2.1.0 (Linux; U; Android 16; SM-S911B Build/TP1A.220624.014) tv.twitch.android.app/25.3.0/2503006";
    public const string WebUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36";

    // ----- Endpoints -----
    public const string OAuthDeviceUrl = "https://id.twitch.tv/oauth2/device";
    public const string OAuthTokenUrl = "https://id.twitch.tv/oauth2/token";
    public const string OAuthValidateUrl = "https://id.twitch.tv/oauth2/validate";
    public const string DeviceCodeGrantType = "urn:ietf:params:oauth:grant-type:device_code";
    public const string ActivateUrl = "https://www.twitch.tv/activate";

    public const string GqlUrl = "https://gql.twitch.tv/gql";
    public const string PubSubUrl = "wss://pubsub-edge.twitch.tv/v1";

    /// <summary>Login-code activation needs no scopes (empty string).</summary>
    public const string OAuthScopes = "";

    // ----- GraphQL persisted-query hashes (operationName -> sha256Hash) -----
    // Update here if Twitch rotates them.
    /// <summary>Persisted-query sha256 hashes for each GraphQL operation the app calls.</summary>
    public static class Gql
    {
        public const int Version = 1;

        public const string Inventory = "d86775d0ef16a63a33ad52e80eaff963b2d5b72fada7c991504a57496e1d8e4b";
        public const string ViewerDropsDashboard = "5a4da2ab3d5b47c9f9ce864e727b2cb346af1e3ea8b897fe8f704a97ff017619";
        public const string DropCampaignDetails = "039277bf98f3130929262cc7c6efd9c141ca3749cb6dca442fc8ead9a53f77c1";
        public const string DropsHighlightServiceAvailableDrops = "782dad0f032942260171d2d80a654f88bdd0c5a9dddc392e9bc92218a0f42d20";
        public const string DropCurrentSessionContext = "4d06b702d25d652afb9ef835d2a550031f1cf762b193523a92166f40ea3d142b";
        public const string DropsPageClaimDropRewards = "a455deea71bdc9015b78eb49f4acfbce8baa7ccbedd28e549bb025bd0f751930";
        public const string DirectoryPageGame = "cb5dc816e139dcb8a118f14b4b677d59abc224a4b016c4bc2bb00a47fe0ddec4";
        public const string VideoPlayerStreamInfoOverlayChannel = "198492e0857f6aedead9665c81c5a06d67b25b58034649687124083ff288597d";
        public const string ChannelPointsContext = "374314de591e69925fce3ddc2bcf085796f56ebb8cad67a0daa3165c03adc345";
        public const string ClaimCommunityPoints = "46aaeebe02c99afdf4fc97c7c0cba964124bf6b0af229395f1f6d1feed05b3d0";
        public const string PlaybackAccessToken = "ed230aa1e33e07eebb8928504583da78a5173989fadfb1ac94be06a04f3cdbe9";
    }

    /// <summary>
    /// The stream-less "watch" is a raw GraphQL mutation (not a persisted query) whose single
    /// input is a base64(gzip(minified-json)) minute-watched event. Success == statusCode 204.
    /// </summary>
    public const string SendSpadeEventsMutation =
        "\n mutation SendEvents($input: SendSpadeEventsInput!) "
        + "{\n sendSpadeEvents(input: $input) {\n statusCode\n}\n}\n";

    // ----- Websocket sharding limits -----
    public const int MaxWebsockets = 8;
    public const int TopicsPerShard = 50;
    public const int TopicsPerChannel = 2;
    public const int BaseTopics = 2;

    /// <summary>PubSub topic string templates ({0} = user or channel id).</summary>
    public static class Topics
    {
        /// <summary>Build the PubSub topic carrying a user's drop-progress events.</summary>
        /// <param name="userId">The Twitch user id.</param>
        public static string UserDrops(string userId) => $"user-drop-events.{userId}";
        /// <summary>Build the PubSub topic carrying a user's onsite notifications.</summary>
        /// <param name="userId">The Twitch user id.</param>
        public static string UserNotifications(string userId) => $"onsite-notifications.{userId}";
        /// <summary>Build the PubSub topic carrying a channel's stream up/down state.</summary>
        /// <param name="channelId">The Twitch channel id.</param>
        public static string ChannelStreamState(string channelId) => $"video-playback-by-id.{channelId}";
        /// <summary>Build the PubSub topic carrying a channel's broadcast-settings updates.</summary>
        /// <param name="channelId">The Twitch channel id.</param>
        public static string ChannelStreamUpdate(string channelId) => $"broadcast-settings-update.{channelId}";
    }

    // ----- Intervals -----
    public static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(59);
    public static readonly TimeSpan PingInterval = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan OnlineDelay = TimeSpan.FromSeconds(120);
}
