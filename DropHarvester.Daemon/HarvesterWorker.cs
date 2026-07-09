using DropHarvester.Services;
using DropHarvester.Services.Twitch;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DropHarvester.Daemon;

/// <summary>
/// Owns the daemon's lifecycle: ensure a valid Twitch session (resume a persisted token, or drive a
/// device-code login printed to the logs), start the shared harvesting engine, and keep it running until
/// the session expires (loop back to re-auth) or the host shuts down (stop cleanly, flush stats).
/// </summary>
public sealed class HarvesterWorker : BackgroundService
{
    readonly ITwitchAuth _auth;
    readonly IHarvesterOrchestrator _harvester;
    readonly IStatsService _stats;
    readonly DaemonStatus _status;
    readonly ReauthGate _reauth;
    readonly ILogger<HarvesterWorker> _log;

    /// <summary>Captures the services the daemon lifecycle needs; the event sink is taken only to force its construction.</summary>
    /// <param name="auth">Twitch auth service used to validate, resume, and begin sessions.</param>
    /// <param name="harvester">The harvesting orchestrator started and stopped by the loop.</param>
    /// <param name="stats">Stats service flushed on shutdown.</param>
    /// <param name="status">Live daemon status updated across the lifecycle.</param>
    /// <param name="reauth">Gate awaited to detect session expiry.</param>
    /// <param name="sink">Event sink injected only so it is constructed and subscribed before harvesting.</param>
    /// <param name="log">Logger for lifecycle messages.</param>
    public HarvesterWorker(
        ITwitchAuth auth,
        IHarvesterOrchestrator harvester,
        IStatsService stats,
        DaemonStatus status,
        ReauthGate reauth,
        DaemonEventSink sink, // injected so it's constructed (and subscribed to the bus) before harvesting
        ILogger<HarvesterWorker> log)
    {
        _auth = auth;
        _harvester = harvester;
        _stats = stats;
        _status = status;
        _reauth = reauth;
        _log = log;
        _ = sink;
    }

    /// <summary>Runs the auth-then-harvest loop until the host stops, re-authenticating whenever the session expires.</summary>
    /// <param name="stoppingToken">Token signaled when the host is shutting down.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("DropHarvester daemon v{Version} starting. Data dir: {Dir}", DaemonInfo.Version, AppPaths.DataDir);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!await EnsureAuthorizedAsync(stoppingToken).ConfigureAwait(false))
                    break; // cancelled during login

                _status.SetAuth(true, AccountLine());
                _reauth.Reset();
                _log.LogInformation("Logged in as {Account}. Starting harvester...", AccountLine());

                await _harvester.StartAsync().ConfigureAwait(false);
                _status.SetHarvesting(_harvester.IsRunning, _harvester.IsRunning ? "running" : "idle");
                if (!_harvester.IsRunning)
                    _log.LogWarning("Harvester did not start (not logged in?). Waiting for re-auth signal.");

                // Run until the session expires (sink trips the gate) or we're asked to stop.
                await _reauth.WaitAsync(stoppingToken).ConfigureAwait(false);

                _log.LogWarning("Pausing harvester to re-authenticate.");
                await _harvester.StopAsync().ConfigureAwait(false);
                _status.SetHarvesting(false, "re-authenticating");
            }
        }
        catch (OperationCanceledException) { /* host shutdown */ }
        catch (Exception ex)
        {
            _log.LogError(ex, "Daemon loop crashed.");
        }
        finally
        {
            try { await _harvester.StopAsync().ConfigureAwait(false); } catch { }
            try { _stats.Flush(); } catch { }
            _log.LogInformation("DropHarvester daemon stopped.");
        }
    }

    /// <summary>Resume a persisted session if the token still validates; otherwise drive a device-code
    /// login (printed to the logs) and poll until authorized. Retries until it succeeds or is cancelled.</summary>
    /// <param name="ct">Token that cancels the login loop on host shutdown.</param>
    /// <returns>True once authorized; false if cancelled before login completed.</returns>
    async Task<bool> EnsureAuthorizedAsync(CancellationToken ct)
    {
        try { if (await _auth.ValidateAsync(ct).ConfigureAwait(false)) { _log.LogInformation("Resumed saved Twitch session."); return true; } }
        catch (OperationCanceledException) { throw; }
        catch { /* fall through to device login */ }
        if (_auth.IsLoggedIn) return true;

        while (!ct.IsCancellationRequested)
        {
            _status.SetAuth(false, null);
            try
            {
                var device = await _auth.BeginDeviceLoginAsync(ct).ConfigureAwait(false);
                var url = string.IsNullOrWhiteSpace(device.VerificationUri) ? "https://www.twitch.tv/activate" : device.VerificationUri;
                var mins = Math.Max(1, (device.ExpiresIn > 0 ? device.ExpiresIn : 1800) / 60);

                _log.LogWarning(" ");
                _log.LogWarning("==================== TWITCH LOGIN REQUIRED ====================");
                _log.LogWarning("  1) On any device, open:  {Url}", url);
                _log.LogWarning("  2) Enter this code:      {Code}", device.UserCode);
                _log.LogWarning("  Waiting for you to authorize (code expires in ~{Min} min)...", mins);
                _log.LogWarning("===============================================================");
                _log.LogWarning(" ");

                if (await _auth.AwaitAuthorizationAsync(device, ct).ConfigureAwait(false))
                {
                    _log.LogInformation("Authorization complete.");
                    return true;
                }
                _log.LogWarning("Login was not completed (code expired or denied). Requesting a new code shortly...");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Device-code login failed; retrying shortly...");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
        }
        return false;
    }

    /// <summary>Builds a display label for the logged-in account, preferring display name, then username, then user id.</summary>
    /// <returns>The best available account label, or "unknown".</returns>
    string AccountLine() =>
        !string.IsNullOrEmpty(_auth.State.DisplayName) ? _auth.State.DisplayName!
        : !string.IsNullOrEmpty(_auth.State.Username) ? _auth.State.Username!
        : _auth.State.UserId ?? "unknown";
}
