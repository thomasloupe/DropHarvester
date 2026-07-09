namespace DropHarvester.Services;

/// <summary>
/// Supplies the self-update manifest URL. The URL is injected by an optional, gitignored partial
/// (<c>UpdateEndpoint.Private.cs</c>) so the deployment endpoint never lives in the public source. When
/// that file isn't present, the URL stays empty and <see cref="UpdateService"/> simply skips its update
/// check - the app still runs and harvests normally, it just won't self-update.
/// </summary>
internal static partial class UpdateEndpoint
{
    /// <summary>Implemented only by the gitignored partial; when absent, the call compiles away and the
    /// URL is left empty.</summary>
    /// <param name="url">Set to the update-manifest URL by the private partial, if present.</param>
    static partial void Configure(ref string url);

    /// <summary>The update-manifest URL, or an empty string when no endpoint is configured for this build.</summary>
    public static string ManifestUrl
    {
        get
        {
            var url = "";
            Configure(ref url);
            return url;
        }
    }
}
