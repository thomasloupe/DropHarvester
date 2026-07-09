using DropHarvester.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DropHarvester.Daemon;

/// <summary>
/// Optionally runs the Core <see cref="IDebugServer"/> in the container - the rich debug endpoints
/// (<c>/snapshot</c> per-campaign/drop decisions, <c>/log</c> rolling log, <c>/claims-raw</c> Twitch
/// claim history). Opt-in via <c>DH_DEBUG_SERVER</c>; bound to 0.0.0.0 so a mapped port reaches the
/// host. Separate from the always-on <see cref="HealthServer"/> (/healthz + /status).
/// </summary>
public sealed class DebugServerHost : IHostedService
{
    readonly IDebugServer _debug;
    readonly ISettingsStore _settings;
    readonly ILogger<DebugServerHost> _log;

    /// <summary>Captures the debug server and settings needed to optionally start the rich debug endpoints.</summary>
    /// <param name="debug">The Core debug server implementation to start and stop.</param>
    /// <param name="settings">Settings store providing the debug-server enable flag and port.</param>
    /// <param name="log">Logger for the debug server lifecycle.</param>
    public DebugServerHost(IDebugServer debug, ISettingsStore settings, ILogger<DebugServerHost> log)
    {
        _debug = debug;
        _settings = settings;
        _log = log;
    }

    /// <summary>Starts the debug server bound to all interfaces when enabled in settings; otherwise does nothing.</summary>
    /// <param name="cancellationToken">Unused; part of the hosted-service contract.</param>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var s = _settings.Settings;
        if (!s.DebugServerEnabled)
            return Task.CompletedTask;

        _debug.Start(s.DebugServerPort, allowRemote: true);
        _log.LogInformation("Debug server enabled on 0.0.0.0:{Port} (/snapshot, /log, /claims-raw).", s.DebugServerPort);
        return Task.CompletedTask;
    }

    /// <summary>Stops the debug server.</summary>
    /// <param name="cancellationToken">Unused; part of the hosted-service contract.</param>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _debug.Stop();
        return Task.CompletedTask;
    }
}
