using DropHarvester;
using DropHarvester.Daemon;
using DropHarvester.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// The engine persists to a data volume and never touches a UI thread in a container. Both must be set
// BEFORE any Core service is constructed (the settings/auth/stats stores read the data dir in their
// constructors), so do it first thing.
AppPaths.DataDir = Environment.GetEnvironmentVariable("DROPHARVESTER_DATA") is { Length: > 0 } dir ? dir : "/data";
UiDispatch.Current = new InlineUiDispatcher();
try { Directory.CreateDirectory(AppPaths.DataDir); } catch { /* surfaced later if persistence fails */ }

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});
if (Enum.TryParse<LogLevel>(Environment.GetEnvironmentVariable("DH_LOG_LEVEL"), ignoreCase: true, out var level))
    builder.Logging.SetMinimumLevel(level);

builder.Services.AddDropHarvesterCore();
builder.Services.AddSingleton<DaemonStatus>();
builder.Services.AddSingleton<ReauthGate>();
builder.Services.AddSingleton<DaemonEventSink>();
builder.Services.AddHostedService<HarvesterWorker>();
builder.Services.AddHostedService<HealthServer>();
builder.Services.AddHostedService<DebugServerHost>();

var host = builder.Build();

// Overlay env-var config onto the persisted settings before the worker starts harvesting.
var settings = host.Services.GetRequiredService<ISettingsStore>();
DaemonConfig.Apply(settings.Settings, host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Config"));
settings.Save();

await host.RunAsync();
