using System.Diagnostics;
using System.Text.Json;
using Octokit;

namespace DropHarvester.Services;

/// <summary>The result of an update check.</summary>
public sealed record UpdateInfo(
    string CurrentVersion,
    string? LatestVersion,
    bool UpdateAvailable,
    string? DownloadUrl,
    string? Error = null);

/// <summary>Download progress: completed fraction (0..1) and the current transfer rate in bytes/second.</summary>
public readonly record struct UpdateProgress(double Fraction, double BytesPerSecond);

/// <summary>
/// GitHub-Releases updater backed by a real installer (Inno Setup .exe on Windows, .pkg on macOS).
/// The check asks the GitHub API for the repo's latest published release; if its tag is newer than the
/// running build, the matching per-OS asset (the .exe / .pkg) is the download. A downloaded installer is
/// cached "pending" - the user applies it now ("Update now") or it auto-applies on the next launch.
/// </summary>
public interface IUpdateService
{
    string CurrentVersion { get; }

    /// <summary>Check GitHub Releases for a newer version (no download).</summary>
    /// <param name="ct">Cancels the request.</param>
    Task<UpdateInfo> CheckAsync(CancellationToken ct = default);

    /// <summary>Download the installer for <paramref name="info"/> into the pending cache, ready to
    /// apply. <paramref name="progress"/> reports the completed fraction and transfer rate. Returns true
    /// once fully downloaded.</summary>
    /// <param name="info">The update whose installer should be downloaded.</param>
    /// <param name="progress">Optional progress reporter receiving fraction + bytes/second.</param>
    /// <param name="ct">Cancels the download.</param>
    Task<bool> DownloadAsync(UpdateInfo info, IProgress<UpdateProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Version of a downloaded, not-yet-applied installer newer than the running build (else null).</summary>
    string? PendingVersion { get; }

    /// <summary>Run the pending installer now (silent on Windows). The caller should exit right after.
    /// Returns true if the installer was launched.</summary>
    bool ApplyPending();

    /// <summary>Startup hook (Windows): if a pending installer for a newer version exists, launch it
    /// silently and return true so the caller can exit and let it replace this build.</summary>
    bool TryAutoApplyOnStartup();
}

/// <summary>Default <see cref="IUpdateService"/>: GitHub-Releases installer updater backed by a LocalAppData pending cache.</summary>
public sealed class UpdateService : IUpdateService
{
    const string ExeName = "DropHarvester.exe";

    // The public repo whose Releases drive updates. Not a secret - it's the public project page.
    const string RepoOwner = "thomasloupe";
    const string RepoName = "DropHarvester";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public string CurrentVersion { get; } = ResolveVersion();

    /// <summary>Resolves the running build's 3-part semver from the assembly version, falling back to AppInfo then 1.0.0.</summary>
    static string ResolveVersion()
    {
        // OUR assembly version (bumped in the csproj), NOT AppInfo.VersionString (reads a stale
        // apphost/package version on unpackaged WinUI). We report a 3-part semver (MAJOR.MINOR.PATCH);
        // AssemblyVersion stores a 4th component internally (0), which we drop here. Comparison via
        // IsNewer/Pad is length-agnostic, so this stays compatible with any legacy 4-part version.
        var v = typeof(UpdateService).Assembly.GetName().Version;
        if (v is not null)
            return $"{v.Major}.{v.Minor}.{v.Build}";
        try { return AppInfo.Current.VersionString; } catch { }
        return "1.0.0";
    }

    // The installer extension we want for THIS OS: Windows ships an .exe, macOS a .pkg. Used to pick the
    // right asset off the release.
    static string AssetExtension => OperatingSystem.IsWindows() ? ".exe" : ".pkg";

    // Pending-installer cache in LocalAppData (never touches FileSystem.AppDataDirectory, whose lazy
    // initializer can throw during very early startup).
    static string PendingDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DropHarvester", "pending-update");
    static string PendingJson => Path.Combine(PendingDir, "pending.json");

    /// <summary>Asks GitHub for the repo's latest published release and reports whether it's newer than
    /// the running build, along with the download URL of this OS's installer asset.</summary>
    /// <param name="ct">Cancels the API request.</param>
    /// <returns>The check result, including any error message instead of throwing. A repo with no
    /// published releases is reported as "up to date" (no error), not a failure.</returns>
    public async Task<UpdateInfo> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            var gh = new GitHubClient(new ProductHeaderValue("DropHarvester", CurrentVersion));
            Release release;
            try
            {
                // latest = most recent NON-prerelease, non-draft release
                release = await gh.Repository.Release.GetLatest(RepoOwner, RepoName).ConfigureAwait(false);
            }
            catch (NotFoundException)
            {
                // no published releases yet -> nothing to update to (not an error)
                return new UpdateInfo(CurrentVersion, null, false, null);
            }

            var latest = release.TagName;
            if (string.IsNullOrWhiteSpace(latest))
                return new UpdateInfo(CurrentVersion, null, false, null, "Latest release has no version tag.");

            // pick this OS's installer asset (the .exe on Windows, the .pkg on macOS)
            var asset = release.Assets.FirstOrDefault(
                a => a.Name.EndsWith(AssetExtension, StringComparison.OrdinalIgnoreCase));
            var url = asset?.BrowserDownloadUrl;

            var available = IsNewer(latest, CurrentVersion) && !string.IsNullOrEmpty(url);
            return new UpdateInfo(CurrentVersion, Norm(latest), available, url);
        }
        catch (Exception ex)
        {
            return new UpdateInfo(CurrentVersion, null, false, null, ex.Message);
        }
    }

    /// <summary>Downloads the installer for the given update into the pending cache, retrying transient failures, and marks it pending.</summary>
    /// <param name="info">The update whose installer should be downloaded.</param>
    /// <param name="progress">Optional progress reporter receiving fraction + bytes/second.</param>
    /// <param name="ct">Cancels the download.</param>
    /// <returns>True once the installer is fully downloaded and recorded as pending.</returns>
    public async Task<bool> DownloadAsync(UpdateInfo info, IProgress<UpdateProgress>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(info.DownloadUrl) || string.IsNullOrEmpty(info.LatestVersion))
            return false;

        try
        {
            Directory.CreateDirectory(PendingDir);

            var fileName = Path.GetFileName(new Uri(info.DownloadUrl).LocalPath);
            if (string.IsNullOrEmpty(fileName) || !Path.HasExtension(fileName))
                fileName = OperatingSystem.IsWindows()
                    ? $"DropHarvester-{info.LatestVersion}-win-x64.exe"
                    : $"DropHarvester-{info.LatestVersion}.pkg";
            var target = Path.Combine(PendingDir, fileName);
            var tmp = target + ".part";

            // Streamed download with a few retries (transient network / AV interference).
            const int maxAttempts = 3;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    using var dreq = new HttpRequestMessage(HttpMethod.Get, info.DownloadUrl);
                    dreq.Headers.UserAgent.ParseAdd("DropHarvester"); // GitHub asset downloads want a UA
                    using var resp = await Http.SendAsync(dreq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    resp.EnsureSuccessStatusCode();
                    var total = resp.Content.Headers.ContentLength;
                    await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                    await using (var fs = File.Create(tmp))
                    {
                        var buffer = new byte[81920];
                        long read = 0, lastReportBytes = 0;
                        var sw = Stopwatch.StartNew();
                        var lastReport = TimeSpan.Zero;
                        int n;
                        while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                        {
                            await fs.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                            read += n;
                            // report ~4x/second: the completed fraction plus the rate over the last window
                            var elapsed = sw.Elapsed;
                            if (progress is not null && total is > 0 && (elapsed - lastReport).TotalMilliseconds >= 250)
                            {
                                var window = (elapsed - lastReport).TotalSeconds;
                                var bps = window > 0 ? (read - lastReportBytes) / window : 0;
                                progress.Report(new UpdateProgress(Math.Clamp((double)read / total.Value, 0, 1), bps));
                                lastReport = elapsed;
                                lastReportBytes = read;
                            }
                        }
                    }
                    progress?.Report(new UpdateProgress(1.0, 0));
                    break;
                }
                catch when (attempt < maxAttempts && !ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                }
            }

            if (File.Exists(target)) File.Delete(target);
            File.Move(tmp, target);
            progress?.Report(new UpdateProgress(1.0, 0));

            // Mark it pending (written only after a complete download, so a pending entry is always
            // a full installer).
            File.WriteAllText(PendingJson, JsonSerializer.Serialize(new PendingUpdate(info.LatestVersion, fileName)));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public string? PendingVersion
    {
        get
        {
            try
            {
                if (!File.Exists(PendingJson))
                    return null;
                var p = JsonSerializer.Deserialize<PendingUpdate>(File.ReadAllText(PendingJson));
                if (p is null || string.IsNullOrEmpty(p.Version) || string.IsNullOrEmpty(p.File))
                    return null;
                if (!File.Exists(Path.Combine(PendingDir, p.File)) || !IsNewer(p.Version, CurrentVersion))
                {
                    ClearPending();
                    return null;
                }
                return p.Version;
            }
            catch { return null; }
        }
    }

    /// <summary>Launches the pending installer if its file is present on disk.</summary>
    /// <returns>True if the installer was launched.</returns>
    public bool ApplyPending()
    {
        try
        {
            if (!File.Exists(PendingJson))
                return false;
            var p = JsonSerializer.Deserialize<PendingUpdate>(File.ReadAllText(PendingJson));
            if (p is null || string.IsNullOrEmpty(p.File))
                return false;
            var path = Path.Combine(PendingDir, p.File);
            return File.Exists(path) && LaunchInstaller(path);
        }
        catch { return false; }
    }

    /// <summary>On Windows, silently launches a pending newer installer at startup so it can replace this build.</summary>
    /// <returns>True if a pending installer was launched (caller should then exit).</returns>
    public bool TryAutoApplyOnStartup()
    {
        // Only Windows auto-applies (it's fully silent). macOS opens the .pkg GUI, which we won't force
        // on every launch - there the user applies it from the Update button.
        if (!OperatingSystem.IsWindows())
            return false;
        return PendingVersion is not null && ApplyPending();
    }

    /// <summary>Deletes the pending-update cache directory and its contents.</summary>
    static void ClearPending()
    {
        try { if (Directory.Exists(PendingDir)) Directory.Delete(PendingDir, true); } catch { }
    }

    /// <summary>Launch the installer, then the caller exits so it can replace the running build.</summary>
    /// <param name="installerPath">Path to the downloaded .exe (Windows) or .pkg (macOS) installer.</param>
    /// <returns>True if the installer (or its wrapper batch) was started.</returns>
    static bool LaunchInstaller(string installerPath)
    {
        var ext = Path.GetExtension(installerPath).ToLowerInvariant();
        try
        {
            if (OperatingSystem.IsWindows() && ext == ".exe")
            {
                // Wait for this app to fully close, run the installer elevated + silent (it uninstalls the
                // old version, installs the new one, and relaunches us), then delete the installer + script.
                var batch = Path.Combine(PendingDir, "apply-update.bat");
                var content = "@echo off\r\n"
                    + ":waitloop\r\n"
                    + $"tasklist /FI \"IMAGENAME eq {ExeName}\" 2>NUL | find /I \"{ExeName}\" >NUL\r\n"
                    + "if \"%ERRORLEVEL%\"==\"0\" ( timeout /t 1 /nobreak >NUL & goto waitloop )\r\n"
                    + $"powershell -NoProfile -Command \"Start-Process -FilePath '{installerPath}' -ArgumentList '/VERYSILENT','/NORESTART' -Verb RunAs -Wait\"\r\n"
                    + $"del \"{installerPath}\"\r\n"
                    + "del \"%~f0\"\r\n";
                File.WriteAllText(batch, content);
                Process.Start(new ProcessStartInfo
                {
                    FileName = batch,
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
                return true;
            }

            if (ext == ".pkg")
            {
                // macOS: hand the .pkg to the system installer UI (they handle a running app gracefully).
                Process.Start(new ProcessStartInfo { FileName = "open", Arguments = $"\"{installerPath}\"", UseShellExecute = true });
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>Normalizes a version string by trimming whitespace and any leading v prefix.</summary>
    /// <param name="s">The raw version string.</param>
    static string Norm(string s) => s.Trim().TrimStart('v', 'V');

    /// <summary>Returns whether the candidate version is newer than the current one.</summary>
    /// <param name="candidate">The version being tested (e.g. a GitHub release tag).</param>
    /// <param name="current">The running build's version.</param>
    static bool IsNewer(string candidate, string current)
    {
        if (Version.TryParse(Pad(Norm(candidate)), out var a) && Version.TryParse(Pad(Norm(current)), out var b))
            return a > b;
        // Fallback: treat "different" as newer (matches the app's "any non-current version = update" rule).
        return !string.Equals(Norm(candidate), Norm(current), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Pads a dotted version string out to four numeric components so Version.TryParse succeeds.</summary>
    /// <param name="v">The version string to pad.</param>
    static string Pad(string v)
    {
        var parts = v.Split('.');
        while (parts.Length < 4) { v += ".0"; parts = v.Split('.'); }
        return v;
    }

    /// <summary>A downloaded installer awaiting application: its version and cached file name.</summary>
    sealed record PendingUpdate(string Version, string File);
}
