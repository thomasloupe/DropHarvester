using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DropHarvester.Daemon;

/// <summary>
/// Tiny HTTP server for container orchestration: <c>/healthz</c> (200 "ok" for docker healthcheck) and
/// <c>/status</c> (live JSON state for at-a-glance monitoring). Raw <see cref="TcpListener"/> on
/// 0.0.0.0:DH_HEALTH_PORT (default 8080) so it needs no extra dependencies and is reachable both from
/// the container's own healthcheck and, if the port is published, the host.
/// </summary>
public sealed class HealthServer : IHostedService
{
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    readonly DaemonStatus _status;
    readonly ILogger<HealthServer> _log;
    TcpListener? _listener;
    CancellationTokenSource? _cts;

    /// <summary>Captures the status source and logger for the health/status HTTP server.</summary>
    /// <param name="status">Live daemon status served at /status.</param>
    /// <param name="log">Logger for server lifecycle and errors.</param>
    public HealthServer(DaemonStatus status, ILogger<HealthServer> log)
    {
        _status = status;
        _log = log;
    }

    /// <summary>Starts the TCP listener and accept loop unless the health endpoint is disabled; start failures are logged, not thrown.</summary>
    /// <param name="cancellationToken">Unused; part of the hosted-service contract.</param>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!DaemonConfig.HealthEnabled)
        {
            _log.LogInformation("Health endpoint disabled (DH_HEALTH_ENABLED=false).");
            return Task.CompletedTask;
        }

        var port = DaemonConfig.HealthPort;
        try
        {
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
            _log.LogInformation("Health endpoint listening on 0.0.0.0:{Port} (/healthz, /status).", port);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Health endpoint could not start on port {Port}.", port);
        }
        return Task.CompletedTask;
    }

    /// <summary>Cancels the accept loop and stops the listener.</summary>
    /// <param name="cancellationToken">Unused; part of the hosted-service contract.</param>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        return Task.CompletedTask;
    }

    /// <summary>Accepts incoming TCP connections until cancelled, dispatching each to be handled concurrently.</summary>
    /// <param name="ct">Token that stops the accept loop.</param>
    async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { } l)
        {
            TcpClient client;
            try { client = await l.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch { break; }
            _ = HandleAsync(client, ct);
        }
    }

    /// <summary>Reads one HTTP request line, routes it, and writes the response; swallows any connection error.</summary>
    /// <param name="client">The accepted client connection to serve and dispose.</param>
    /// <param name="ct">Token that cancels the read and write.</param>
    async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII);
                var requestLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(requestLine))
                    return;

                var parts = requestLine.Split(' ');
                var path = (parts.Length > 1 ? parts[1] : "/").Split('?')[0].TrimEnd('/');

                var (code, contentType, body) = Route(path);
                var bodyBytes = Encoding.UTF8.GetBytes(body);
                var header = $"HTTP/1.1 {code}\r\nContent-Type: {contentType}; charset=utf-8\r\n"
                    + $"Content-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct).ConfigureAwait(false);
                await stream.WriteAsync(bodyBytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            catch { /* best-effort; drop the connection on any error */ }
        }
    }

    /// <summary>Maps a request path to the HTTP status, content type, and body to return.</summary>
    /// <param name="path">The normalized request path with query and trailing slash stripped.</param>
    /// <returns>A tuple of status line, content type, and response body.</returns>
    (string code, string contentType, string body) Route(string path) => path switch
    {
        "" or "/healthz" or "/health" => ("200 OK", "text/plain", "ok"),
        "/status" => ("200 OK", "application/json", JsonSerializer.Serialize(_status.Snapshot(), JsonOpts)),
        _ => ("404 Not Found", "text/plain", "Not found. Try /healthz or /status"),
    };
}
