using System.Text.Json;
using Octokit;

namespace DropHarvester.Services;

/// <summary>One version's entry in the "What's changed" list: its version, the release notes, and whether
/// it's the version the user is running (YOU ARE HERE) or the newest one available (LATEST).</summary>
public sealed class ReleaseNote
{
    public required string Version { get; init; }
    public required string Notes { get; init; }

    /// <summary>The version the app is currently running - gets the yellow "YOU ARE HERE" badge.</summary>
    public bool IsCurrent { get; set; }

    /// <summary>The newest version available - gets the purple "LATEST" badge.</summary>
    public bool IsLatest { get; set; }

    /// <summary>"v1.20.0" for display.</summary>
    public string DisplayVersion => $"v{Version}";
}

/// <summary>Supplies the release-notes changelog for the "View Changes" popup.</summary>
public interface IChangelogService
{
    /// <summary>The full changelog, newest first, with the running and latest versions flagged. Combines the
    /// notes bundled with this build (everything through the running version) with any newer releases pulled
    /// from GitHub, so an up-to-date app makes no network call and an outdated one fetches only the delta.</summary>
    /// <param name="ct">Cancels the GitHub fetch.</param>
    Task<IReadOnlyList<ReleaseNote>> GetChangelogAsync(CancellationToken ct = default);
}

/// <summary>Default <see cref="IChangelogService"/>: reads the bundled <c>changelog.json</c> and tops it up
/// with releases newer than the running build from the public GitHub Releases API.</summary>
public sealed class ChangelogService : IChangelogService
{
    // Same public repo the updater reads. Not a secret.
    const string RepoOwner = "thomasloupe";
    const string RepoName = "DropHarvester";

    readonly IUpdateService _update;

    /// <summary>Creates the service, taking the updater to learn the running build's version.</summary>
    /// <param name="update">The update service, used only for its <see cref="IUpdateService.CurrentVersion"/>.</param>
    public ChangelogService(IUpdateService update) => _update = update;

    /// <summary>On-disk shape of one bundled changelog entry.</summary>
    sealed class BundledEntry
    {
        public string Version { get; set; } = "";
        public string Notes { get; set; } = "";
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ReleaseNote>> GetChangelogAsync(CancellationToken ct = default)
    {
        var current = _update.CurrentVersion;
        // key by normalized version so a bundled entry and a fetched one for the same version never double up
        var byVersion = new Dictionary<string, ReleaseNote>(StringComparer.OrdinalIgnoreCase);

        // 1. bundled notes shipped with this build (everything up to the running version) - no network
        foreach (var e in await LoadBundledAsync().ConfigureAwait(false))
            if (!string.IsNullOrWhiteSpace(e.Version))
                byVersion[Norm(e.Version)] = new ReleaseNote { Version = Norm(e.Version), Notes = e.Notes?.Trim() ?? "" };

        // 2. only the releases NEWER than what we shipped with, from GitHub (one page, newest first). This is
        //    the whole point of bundling: we never re-fetch the history the app already carries, just the gap
        //    between the running build and the latest release. Best-effort: offline just shows the bundled set.
        try
        {
            var gh = new GitHubClient(new ProductHeaderValue("DropHarvester", current));
            var releases = await gh.Repository.Release.GetAll(
                RepoOwner, RepoName, new ApiOptions { PageSize = 30, PageCount = 1 }).ConfigureAwait(false);
            foreach (var r in releases)
            {
                if (r.Draft || r.Prerelease || string.IsNullOrWhiteSpace(r.TagName))
                    continue;
                if (!IsNewer(r.TagName, current))
                    continue; // bundled already covers everything at/below the running version
                byVersion[Norm(r.TagName)] = new ReleaseNote { Version = Norm(r.TagName), Notes = (r.Body ?? "").Trim() };
            }
        }
        catch { /* offline / rate-limited: the bundled changelog still shows */ }

        var ordered = byVersion.Values.OrderByDescending(r => Parse(r.Version)).ToList();
        if (ordered.Count == 0)
            return ordered;

        // LATEST = the newest we know of; YOU ARE HERE = the running build's version
        ordered[0].IsLatest = true;
        foreach (var r in ordered)
            r.IsCurrent = SameVersion(r.Version, current);
        return ordered;
    }

    /// <summary>Reads and parses the bundled <c>changelog.json</c> app asset; empty list if missing/unreadable.</summary>
    static async Task<List<BundledEntry>> LoadBundledAsync()
    {
        try
        {
            await using var stream = await FileSystem.OpenAppPackageFileAsync("changelog.json").ConfigureAwait(false);
            var entries = await JsonSerializer.DeserializeAsync<List<BundledEntry>>(
                stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }).ConfigureAwait(false);
            return entries ?? new List<BundledEntry>();
        }
        catch { return new List<BundledEntry>(); }
    }

    /// <summary>Trims whitespace and a leading v from a version tag.</summary>
    /// <param name="v">The raw version/tag string.</param>
    static string Norm(string v) => v.Trim().TrimStart('v', 'V');

    /// <summary>Whether two version strings are the same release.</summary>
    /// <param name="a">First version.</param>
    /// <param name="b">Second version.</param>
    static bool SameVersion(string a, string b) => Parse(a) == Parse(b);

    /// <summary>Whether <paramref name="candidate"/> is a newer version than <paramref name="current"/>.</summary>
    /// <param name="candidate">The version being tested (e.g. a GitHub tag).</param>
    /// <param name="current">The running build's version.</param>
    static bool IsNewer(string candidate, string current) => Parse(candidate) > Parse(current);

    /// <summary>Parses a dotted version (padded to 4 components) for comparison; 0.0.0.0 if unparseable.</summary>
    /// <param name="v">The version string.</param>
    static Version Parse(string v)
    {
        var n = Norm(v);
        var parts = n.Split('.');
        while (parts.Length < 4) { n += ".0"; parts = n.Split('.'); }
        return Version.TryParse(n, out var ver) ? ver : new Version(0, 0, 0, 0);
    }
}
