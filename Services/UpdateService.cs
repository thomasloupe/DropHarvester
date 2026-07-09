using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DropHarvester.Services;

/// <summary>The result of an update check.</summary>
public sealed record UpdateInfo(
    string CurrentVersion,
    string? LatestVersion,
    bool UpdateAvailable,
    string? DownloadUrl,
    string? Error = null);

/// <summary>
/// Self-hosted-manifest updater using a real installer (Inno Setup .exe on Windows, .pkg on macOS).
/// On startup the app silently checks the manifest and, if a newer version exists, downloads the
/// installer to a pending cache. The user can apply it NOW ("Update" button) or it auto-applies on the
/// next launch. No "download" step or yes/no prompt.
///
/// The manifest URL comes from <see cref="UpdateEndpoint"/> (kept out of source); it points at a small
/// JSON document of the shape:
/// <code>
/// { "windows": { "version": "1.4.5", "url": ".../DropHarvester-1.4.5-win-x64.exe" },
///   "mac":     { "version": "1.4.5", "url": ".../DropHarvester-1.4.5.pkg" } }
/// </code>
/// When no endpoint is configured, the update check is a no-op.
/// </summary>
public interface IUpdateService
{
    string CurrentVersion { get; }

    /// <summary>Check the manifest for a newer version (no download).</summary>
    /// <param name="ct">Cancels the manifest request.</param>
    Task<UpdateInfo> CheckAsync(CancellationToken ct = default);

    /// <summary>Download the installer for <paramref name="info"/> into the pending cache, ready to
    /// apply. <paramref name="progress"/> reports 0..1. Returns true once fully downloaded.</summary>
    /// <param name="info">The update whose installer should be downloaded.</param>
    /// <param name="progress">Optional progress reporter receiving values from 0.0 to 1.0.</param>
    /// <param name="ct">Cancels the download.</param>
    Task<bool> DownloadAsync(UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>Version of a downloaded, not-yet-applied installer newer than the running build (else null).</summary>
    string? PendingVersion { get; }

    /// <summary>Run the pending installer now (silent on Windows). The caller should exit right after.
    /// Returns true if the installer was launched.</summary>
    bool ApplyPending();

    /// <summary>Startup hook (Windows): if a pending installer for a newer version exists, launch it
    /// silently and return true so the caller can exit and let it replace this build.</summary>
    bool TryAutoApplyOnStartup();
}

/// <summary>Default <see cref="IUpdateService"/>: manifest-driven installer updater backed by a LocalAppData pending cache.</summary>
public sealed class UpdateService : IUpdateService
{
    const string ExeName = "DropHarvester.exe";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public string CurrentVersion { get; } = ResolveVersion();

    /// <summary>Resolves the running build's 3-part semver from the assembly version, falling back to AppInfo then 1.0.0.</summary>
    static string ResolveVersion()
    {
        // OUR assembly version (bumped in the csproj), NOT AppInfo.VersionString (reads a stale
        // apphost/package version on unpackaged WinUI). We report a 3-part semver (MAJOR.MINOR.PATCH);
        // AssemblyVersion stores a 4th component internally (0), which we drop here. Comparison via
        // IsNewer/Pad is length-agnostic, so this stays compatible with any legacy 4-part manifest.
        var v = typeof(UpdateService).Assembly.GetName().Version;
        if (v is not null)
            return $"{v.Major}.{v.Minor}.{v.Build}";
        try { return AppInfo.Current.VersionString; } catch { }
        return "1.0.0";
    }

    static string OsKey => OperatingSystem.IsWindows() ? "windows" : "mac";

    // Pending-installer cache in LocalAppData (never touches FileSystem.AppDataDirectory, whose lazy
    // initializer can throw during very early startup).
    static string PendingDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DropHarvester", "pending-update");
    static string PendingJson => Path.Combine(PendingDir, "pending.json");

    /// <summary>Fetches the manifest and reports whether a newer version is available for this OS.</summary>
    /// <param name="ct">Cancels the manifest request.</param>
    /// <returns>The check result, including any error message instead of throwing.</returns>
    public async Task<UpdateInfo> CheckAsync(CancellationToken ct = default)
    {
        var manifestUrl = UpdateEndpoint.ManifestUrl;
        if (string.IsNullOrWhiteSpace(manifestUrl))
            return new UpdateInfo(CurrentVersion, null, false, null); // no endpoint configured -> self-update disabled

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, manifestUrl);
            req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
            req.Headers.UserAgent.ParseAdd("DropHarvester");

            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

            if (!doc.RootElement.TryGetProperty(OsKey, out var os) || os.ValueKind != JsonValueKind.Object)
                return new UpdateInfo(CurrentVersion, null, false, null, $"No '{OsKey}' entry in the update manifest.");

            var latest = os.TryGetProperty("version", out var v) ? v.GetString() : null;
            var url = os.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(latest))
                return new UpdateInfo(CurrentVersion, null, false, null, "Update manifest has no version.");

            var available = IsNewer(latest, CurrentVersion);
            return new UpdateInfo(CurrentVersion, latest, available, url);
        }
        catch (Exception ex)
        {
            return new UpdateInfo(CurrentVersion, null, false, null, ex.Message);
        }
    }

    /// <summary>Downloads the installer for the given update into the pending cache, retrying transient failures, and marks it pending.</summary>
    /// <param name="info">The update whose installer should be downloaded.</param>
    /// <param name="progress">Optional progress reporter receiving values from 0.0 to 1.0.</param>
    /// <param name="ct">Cancels the download.</param>
    /// <returns>True once the installer is fully downloaded and recorded as pending.</returns>
    public async Task<bool> DownloadAsync(UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
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
                    using var resp = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    resp.EnsureSuccessStatusCode();
                    var total = resp.Content.Headers.ContentLength;
                    await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                    await using (var fs = File.Create(tmp))
                    {
                        var buffer = new byte[81920];
                        long read = 0;
                        int n;
                        while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                        {
                            await fs.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                            read += n;
                            if (total is > 0)
                                progress?.Report(Math.Clamp((double)read / total.Value, 0, 1));
                        }
                    }
                    break;
                }
                catch when (attempt < maxAttempts && !ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                }
            }

            if (File.Exists(target)) File.Delete(target);
            File.Move(tmp, target);
            progress?.Report(1.0);

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
    /// <param name="candidate">The version being tested (e.g. from the manifest).</param>
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
