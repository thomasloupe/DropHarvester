using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DropHarvester.Models.Events;
using DropHarvester.Services.Twitch;

namespace DropHarvester.Services;

/// <summary>
/// A tiny localhost-only HTTP debug server for inspecting what the app is doing under the hood:
/// the live harvester snapshot (per-campaign/drop decisions, claim attribution, benched channels) and a
/// rolling log. Enabled from Settings. Uses a raw <see cref="TcpListener"/> on 127.0.0.1 (not
/// HttpListener) so it needs no URL reservation / admin rights and works the same on Windows + macOS.
/// </summary>
public interface IDebugServer
{
    bool IsRunning { get; }
    int Port { get; }
    /// <summary>Start the server. <paramref name="allowRemote"/> binds 0.0.0.0 instead of loopback
    /// (used by the headless daemon so the port is reachable from outside the container); the desktop
    /// app leaves it false and stays localhost-only.</summary>
    void Start(int port, bool allowRemote = false);

    /// <summary>Stop the server and release the listening socket.</summary>
    void Stop();
}

/// <summary>Localhost/loopback TcpListener implementation of <see cref="IDebugServer"/>.</summary>
public sealed class DebugServer : IDebugServer
{
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    readonly IHarvesterOrchestrator _harvester;
    readonly IHarvesterEventBus _bus;
    readonly IInventoryService _inventory;
    readonly object _logLock = new();
    readonly LinkedList<string> _log = new();
    const int MaxLog = 2000;

    TcpListener? _listener;
    CancellationTokenSource? _cts;

    public bool IsRunning { get; private set; }
    public int Port { get; private set; }

    /// <summary>Wires the rolling-log subscription; the HTTP listener is not started until Start.</summary>
    /// <param name="harvester">Orchestrator queried for the live debug snapshot.</param>
    /// <param name="bus">Event bus whose events feed the rolling log.</param>
    /// <param name="inventory">Inventory service used for the raw claim-history endpoint.</param>
    public DebugServer(IHarvesterOrchestrator harvester, IHarvesterEventBus bus, IInventoryService inventory)
    {
        _harvester = harvester;
        _bus = bus;
        _inventory = inventory;
        _bus.Event += OnEvent; // keep a rolling log regardless of whether the server is up
    }

    /// <summary>Formats a harvester event into a timestamped line and appends it to the capped rolling log.</summary>
    /// <param name="e">Event to record; unrecognized event types are ignored.</param>
    void OnEvent(HarvesterEvent e)
    {
        var stamp = e.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        var line = e switch
        {
            LogEvent l => $"[{stamp}] {l.Level.ToString().ToUpperInvariant()}: {l.Message}",
            HarvesterErrorEvent er => $"[{stamp}] ERROR: {er.Message}",
            DropClaimedEvent d => $"[{stamp}] CLAIMED {d.Campaign.Game.Name}: {d.Drop.Name}",
            CampaignCompletedEvent c => $"[{stamp}] CAMPAIGN DONE {c.Campaign.Game.Name}: {c.Campaign.Name}",
            ActiveTargetEvent t when t.Campaign is not null => $"[{stamp}] TARGET {t.Campaign.Game.Name} on {t.Channel?.DisplayName}",
            _ => null,
        };
        if (line is null)
            return;
        lock (_logLock)
        {
            _log.AddLast(line);
            while (_log.Count > MaxLog) _log.RemoveFirst();
        }
    }

    /// <summary>Restarts the listener on the given port and spawns the accept loop, logging the outcome.</summary>
    /// <param name="port">TCP port to listen on.</param>
    /// <param name="allowRemote">When true binds all interfaces (0.0.0.0); otherwise loopback only.</param>
    public void Start(int port, bool allowRemote = false)
    {
        Stop();
        try
        {
            Port = port;
            _listener = new TcpListener(allowRemote ? IPAddress.Any : IPAddress.Loopback, port);
            _listener.Start();
            IsRunning = true;
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
            _bus.Publish(new LogEvent(allowRemote
                ? $"Debug server started on http://0.0.0.0:{port}/ (exposed on your network)."
                : $"Debug server started on http://localhost:{port}/"));
        }
        catch (Exception ex)
        {
            IsRunning = false;
            _bus.Publish(new LogEvent($"Debug server couldn't start on port {port}: {ex.Message}", HarvesterLogLevel.Warn));
        }
    }

    /// <summary>Cancels the accept loop and stops the listener; safe to call when not running.</summary>
    public void Stop()
    {
        if (!IsRunning && _listener is null)
            return;
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        IsRunning = false;
    }

    /// <summary>Accepts client connections until cancelled or the listener stops, handling each one.</summary>
    /// <param name="ct">Token that ends the accept loop.</param>
    async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { } l)
        {
            TcpClient client;
            try { client = await l.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch { break; } // listener stopped
            _ = HandleClientAsync(client, ct);
        }
    }

    /// <summary>Reads one HTTP request line, routes it, and writes the response with permissive CORS.</summary>
    /// <param name="client">Connected client to service and dispose.</param>
    /// <param name="ct">Token to cancel the read/write.</param>
    async Task HandleClientAsync(TcpClient client, CancellationToken ct)
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
                var path = parts.Length > 1 ? parts[1] : "/";

                var (contentType, body) = await RouteAsync(path, ct).ConfigureAwait(false);
                var bodyBytes = Encoding.UTF8.GetBytes(body);
                var header = $"HTTP/1.1 200 OK\r\nContent-Type: {contentType}; charset=utf-8\r\n"
                    + $"Content-Length: {bodyBytes.Length}\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct).ConfigureAwait(false);
                await stream.WriteAsync(bodyBytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            catch { }
        }
    }

    /// <summary>Maps a request path to a content type and body, serving the index, snapshot, log, or claims.</summary>
    /// <param name="rawPath">Raw request path, including any query string, to route.</param>
    /// <param name="ct">Token to cancel any async work while building the body.</param>
    /// <returns>The content type and response body for the path (a not-found message for unknown paths).</returns>
    async Task<(string contentType, string body)> RouteAsync(string rawPath, CancellationToken ct)
    {
        var split = rawPath.Split('?', 2);
        var path = split[0].TrimEnd('/');
        var query = ParseQuery(split.Length > 1 ? split[1] : "");
        try
        {
            return path switch
            {
                "" or "/index" => ("text/html", IndexHtml()),
                "/snapshot" => ("application/json", JsonSerializer.Serialize(_harvester.GetDebugSnapshot(), JsonOpts)),
                "/campaigns-all" => ("application/json", JsonSerializer.Serialize(_harvester.DebugAllCampaigns(), JsonOpts)),
                "/dashboard" => ("application/json", JsonSerializer.Serialize(await DashboardAsync(ct).ConfigureAwait(false), JsonOpts)),
                "/log" => ("text/plain", LogText()),
                "/crashlog" => ("text/plain", CrashLogText()),
                "/claims-raw" => ("application/json", await _inventory.GetClaimHistoryRawJsonAsync(ct).ConfigureAwait(false)),
                "/harvest" => ("application/json", Harvest(query)),
                "/watch-probe" => ("application/json", JsonSerializer.Serialize(await _harvester.DebugWatchProbeAsync(ct).ConfigureAwait(false), JsonOpts)),
                "/authstate" => ("application/json", JsonSerializer.Serialize(_harvester.DebugAuthState(), JsonOpts)),
                _ => ("text/plain", "Not found. Try / , /snapshot , /campaigns-all , /claims-raw , /watch-probe , /authstate , /harvest , /crashlog or /log"),
            };
        }
        catch (Exception ex)
        {
            return ("text/plain", $"Error building {path}: {ex.Message}\n{ex}");
        }
    }

    /// <summary>Handles /dashboard: the raw dashboard summary of EVERY campaign (Status + Linked), which is
    /// what the harvester filters on to decide which campaigns it will even consider (Active + Linked).</summary>
    /// <param name="ct">Cancels the dashboard fetch.</param>
    /// <returns>All campaign summaries with their status and link state.</returns>
    async Task<object> DashboardAsync(CancellationToken ct)
    {
        var sums = await _inventory.FetchDashboardAsync(ct).ConfigureAwait(false);
        return new
        {
            Count = sums.Count,
            HarvesterConsiders = "Status == Active AND (Linked OR game on 'Harvest unlinked games')",
            Campaigns = sums
                .OrderBy(s => s.Game.Name).ThenBy(s => s.Name)
                .Select(s => new { s.Id, Game = s.Game.Name, s.Name, Status = s.Status.ToString(), s.Linked, s.StartsAt, s.EndsAt })
                .ToList(),
        };
    }

    /// <summary>Handles /harvest: with ?clear=1 releases the override, with ?id=X pins that campaign/drop,
    /// and with no query returns the current override plus the list of harvestable targets to pick from.</summary>
    /// <param name="query">Parsed query parameters from the request.</param>
    /// <returns>A JSON result describing the action taken or the available targets.</returns>
    string Harvest(Dictionary<string, string> query)
    {
        if (query.ContainsKey("clear"))
        {
            _harvester.ClearCampaignOverride();
            return JsonSerializer.Serialize(new { Result = "Override cleared - resuming automatic selection." }, JsonOpts);
        }
        if (query.TryGetValue("id", out var id) && !string.IsNullOrWhiteSpace(id))
            return JsonSerializer.Serialize(new { Result = _harvester.DebugForceHarvest(id) }, JsonOpts);

        return JsonSerializer.Serialize(new
        {
            Usage = "/harvest?id=<campaignId or dropId> to pin a target, /harvest?clear=1 to release.",
            CurrentOverride = _harvester.OverrideCampaignId,
            Targets = _harvester.DebugHarvestTargets(),
        }, JsonOpts);
    }

    /// <summary>Parses a raw URL query string into a case-insensitive key/value map (URL-decoded).</summary>
    /// <param name="raw">The query portion after '?', without the leading '?'.</param>
    /// <returns>Decoded key/value pairs; valueless keys map to an empty string.</returns>
    static Dictionary<string, string> ParseQuery(string raw)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(raw))
            return map;
        foreach (var pair in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(kv[0]);
            map[key] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
        }
        return map;
    }

    /// <summary>Returns the rolling log joined into a single newline-separated string.</summary>
    string LogText()
    {
        lock (_logLock)
            return string.Join("\n", _log);
    }

    /// <summary>Returns the contents of the app's crash.log (unhandled exceptions and caught UI-dispatch
    /// errors), or a placeholder when it doesn't exist yet.</summary>
    static string CrashLogText()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DropHarvester", "crash.log");
            return File.Exists(path) ? File.ReadAllText(path) : "No crash log yet - nothing has crashed or been caught.";
        }
        catch (Exception ex) { return $"Couldn't read crash.log: {ex.Message}"; }
    }

    /// <summary>Returns the debug index page HTML linking to the snapshot, claims, and log endpoints.</summary>
    static string IndexHtml() =>
        "<!doctype html><html><head><meta charset=utf-8><title>DropHarvester debug</title>"
        + "<style>body{font-family:system-ui;background:#0e0f13;color:#f1f2f5;padding:24px}"
        + "a{color:#a970ff;font-size:18px;display:block;margin:10px 0}</style></head><body>"
        + "<h1>DropHarvester debug</h1>"
        + "<a href=\"/snapshot\">/snapshot</a> - live harvester state + per-campaign/drop decisions (JSON)"
        + "<a href=\"/campaigns-all\">/campaigns-all</a> - EVERY discovered campaign (incl. finished/inactive) + why it's skipped (JSON)"
        + "<a href=\"/claims-raw\">/claims-raw</a> - raw claim history from Twitch (gameEventDrops, JSON)"
        + "<a href=\"/watch-probe\">/watch-probe</a> - send one minute-watched heartbeat now, dump the full request/response (JSON)"
        + "<a href=\"/authstate\">/authstate</a> - current auth/session context, secrets redacted (JSON)"
        + "<a href=\"/harvest\">/harvest</a> - list harvestable targets; ?id=&lt;id&gt; pins one, ?clear=1 releases (JSON)"
        + "<a href=\"/log\">/log</a> - rolling log (text)"
        + "<a href=\"/crashlog\">/crashlog</a> - caught crashes and UI-dispatch errors (text)"
        + "</body></html>";
}
