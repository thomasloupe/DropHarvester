using DropHarvester.Models.Auth;
using DropHarvester.Models.Twitch;
using DropHarvester.Services;
using DropHarvester.Services.Twitch;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DropHarvester.Daemon;

/// <summary>
/// Owns the daemon's lifecycle: ensure a valid Twitch session (resume a persisted token, or take one from
/// a browser sign-in handed in via env or the login file), start the shared harvesting engine, and keep it
/// running until the session expires (loop back to re-auth) or the host shuts down (stop cleanly, flush stats).
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

    /// <summary>Resume a persisted session if the token still validates; otherwise log in with the token in
    /// <c>DH_TWITCH_TOKEN</c>, or print the browser sign-in steps and wait for the resulting twitch.tv
    /// address to be written to <c>login.txt</c> in the data dir. Waits until it succeeds or is cancelled.</summary>
    /// <param name="ct">Token that cancels the login loop on host shutdown.</param>
    /// <returns>True once authorized; false if cancelled before login completed.</returns>
    async Task<bool> EnsureAuthorizedAsync(CancellationToken ct)
    {
        try { if (await _auth.ValidateAsync(ct).ConfigureAwait(false)) { _log.LogInformation("Resumed saved Twitch session."); return true; } }
        catch (OperationCanceledException) { throw; }
        catch { /* fall through to a fresh login */ }
        if (_auth.IsLoggedIn) return true;

        _status.SetAuth(false, null);

        var envToken = Environment.GetEnvironmentVariable("DH_TWITCH_TOKEN");
        if (!string.IsNullOrWhiteSpace(envToken))
        {
            if (await TryTokenLoginAsync(envToken, "DH_TWITCH_TOKEN", ct).ConfigureAwait(false))
                return true;
        }

        var loginFile = Path.Combine(AppPaths.DataDir, "login.txt");
        PrintLoginSteps(loginFile);
        while (!ct.IsCancellationRequested)
        {
            if (File.Exists(loginFile))
            {
                string text;
                try
                {
                    text = await File.ReadAllTextAsync(loginFile, ct).ConfigureAwait(false);
                    File.Delete(loginFile); // it holds a credential: never leave it on disk
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log.LogWarning("Couldn't read {File}: {Message}", loginFile, ex.Message);
                    text = "";
                }

                if (text.Length > 0)
                {
                    if (await TryTokenLoginAsync(text, loginFile, ct).ConfigureAwait(false))
                        return true;
                    PrintLoginSteps(loginFile);
                }
            }

            try { await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
        }
        return false;
    }

    /// <summary>Log in with a token (or the sign-in redirect address carrying it) and log the outcome.</summary>
    /// <param name="tokenOrUrl">The access token, or the twitch.tv address it arrived in.</param>
    /// <param name="source">Where it came from, for the log line.</param>
    /// <param name="ct">Token to cancel the validation.</param>
    /// <returns>True when the session was saved.</returns>
    async Task<bool> TryTokenLoginAsync(string tokenOrUrl, string source, CancellationToken ct)
    {
        TokenLoginResult result;
        try { result = await _auth.LogInWithTokenAsync(tokenOrUrl, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogError(ex, "Logging in with the token from {Source} failed.", source);
            return false;
        }

        switch (result)
        {
            case TokenLoginResult.Success:
                _log.LogInformation("Logged in with the token from {Source}.", source);
                return true;
            case TokenLoginResult.NoToken:
                _log.LogWarning("No access token found in {Source}. It should be the twitch.tv address containing #access_token=.", source);
                break;
            case TokenLoginResult.WrongClient:
                _log.LogWarning("The token from {Source} belongs to a different Twitch app. Use the sign-in link below to get one.", source);
                break;
            default:
                _log.LogWarning("Twitch rejected the token from {Source} (expired or revoked).", source);
                break;
        }
        return false;
    }

    /// <summary>Prints the browser sign-in steps: open the sign-in link anywhere, then hand the resulting
    /// twitch.tv address back via the login file (no restart) or <c>DH_TWITCH_TOKEN</c> (restart).</summary>
    /// <param name="loginFile">Path of the file the daemon watches for the address.</param>
    void PrintLoginSteps(string loginFile)
    {
        _log.LogWarning(" ");
        _log.LogWarning("==================== TWITCH LOGIN REQUIRED ====================");
        _log.LogWarning("  1) In any browser, open:");
        _log.LogWarning("     {Url}", TwitchConstants.BrowserLoginUrl);
        _log.LogWarning("  2) Sign in with your Twitch username and password. You land on");
        _log.LogWarning("     twitch.tv with #access_token=... in the address bar - copy that address.");
        _log.LogWarning("  3) Write it to {File} (picked up within seconds),", loginFile);
        _log.LogWarning("     or set DH_TWITCH_TOKEN to it and restart the container.");
        _log.LogWarning("  Waiting for the login...");
        _log.LogWarning("===============================================================");
        _log.LogWarning(" ");
    }

    /// <summary>Builds a display label for the logged-in account, preferring display name, then username, then user id.</summary>
    /// <returns>The best available account label, or "unknown".</returns>
    string AccountLine() =>
        !string.IsNullOrEmpty(_auth.State.DisplayName) ? _auth.State.DisplayName!
        : !string.IsNullOrEmpty(_auth.State.Username) ? _auth.State.Username!
        : _auth.State.UserId ?? "unknown";
}
