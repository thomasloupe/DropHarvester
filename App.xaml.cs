using DropHarvester.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DropHarvester;

/// <summary>Root MAUI application: global crash logging, startup wiring, and the main window.</summary>
public partial class App : Application
{
    readonly IServiceProvider _services;

    static int _crashLogBusy;
    static string? _crashLogPath;

    /// <summary>Append an exception to crash.log so intermittent crashes are diagnosable.
    /// Deliberately does NOT touch <c>FileSystem.AppDataDirectory</c> (its lazy initializer can throw
    /// in unpackaged WinUI, and that throw re-enters this method via the unhandled-exception handler -
    /// which previously recursed until the stack overflowed, masking the real error). A re-entrancy
    /// guard plus a plain %LOCALAPPDATA% path make logging failure-proof.</summary>
    /// <param name="source">Short label identifying where the exception came from.</param>
    /// <param name="ex">The exception to record, if any.</param>
    public static void CrashLog(string source, Exception? ex)
    {
        if (Interlocked.Exchange(ref _crashLogBusy, 1) == 1)
            return; // already logging on this call chain - never recurse
        try
        {
            File.AppendAllText(CrashLogPath(), $"\n===== {DateTimeOffset.Now:o} [{source}] =====\n{ex}\n");
        }
        catch { }
        finally
        {
            Interlocked.Exchange(ref _crashLogBusy, 0);
        }
    }

    static int _offThreadLogged;

    /// <summary>Record (capped) when a UI-bound change notification is raised off the UI thread - the
    /// cause of the combase/CoreMessagingXP cross-thread failfast. Includes the call stack so the exact
    /// property + code path is pinpointable in crash.log. Capped so a genuine flood can't fill the log.</summary>
    /// <param name="what">Description of the off-thread property set, used in the logged message.</param>
    public static void LogOffThreadNotification(string what)
    {
        if (Interlocked.Increment(ref _offThreadLogged) > 30)
            return;
        CrashLog("OffThreadNotify", new InvalidOperationException(
            $"UI-bound property set off the UI thread: {what}\n{Environment.StackTrace}"));
    }

    /// <summary>Resolve (once) a crash-log path that never depends on MAUI/WinRT storage init.</summary>
    static string CrashLogPath()
    {
        if (_crashLogPath is not null)
            return _crashLogPath;
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DropHarvester");
        try { Directory.CreateDirectory(dir); } catch { }
        return _crashLogPath = Path.Combine(dir, "crash.log");
    }

    /// <summary>Installs global crash handlers, applies any pending update, sets dark theme, and starts background services.</summary>
    /// <param name="services">The application's dependency-injection service provider.</param>
    public App(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;

        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { CrashLog("UnobservedTask", e.Exception); e.SetObserved(); };

        // Auto-apply a pending update BEFORE any window/UI: if a newer installer was downloaded last
        // session and the user didn't apply it, install it silently now and exit (it relaunches us).
        try
        {
            if (services.GetRequiredService<IUpdateService>().TryAutoApplyOnStartup())
            {
                Environment.Exit(0);
                return;
            }
        }
        catch { /* never let the updater block launch */ }

        // Apply the saved UI language before any page/shell is built so their text starts localized.
        try { DropHarvester.Localization.Loc.Culture = services.GetRequiredService<ISettingsStore>().Settings.Language; }
        catch { /* fall back to English */ }

        // DropHarvester is dark-mode only.
        UserAppTheme = AppTheme.Dark;

        // Instantiate the alerts coordinator so it subscribes to the harvester event bus.
        _ = services.GetRequiredService<AlertsCoordinator>();

        // Pre-warm the Log view-model so it's subscribed to the event bus before the startup messages
        // below fire (it's a singleton, so the Log tab reuses this instance) - otherwise the tab, built
        // lazily on first open, would miss e.g. the "Debug server started" line logged at launch.
        _ = services.GetRequiredService<ViewModels.LogViewModel>();

        // Start the debug server if it was left enabled.
        var startupSettings = services.GetRequiredService<ISettingsStore>().Settings;
        if (startupSettings.DebugServerEnabled)
            services.GetRequiredService<IDebugServer>().Start(startupSettings.DebugServerPort);

        // Pre-warm the Channels + Inventory view-models on launch so both tabs populate in the
        // background before they're opened: the Channels VM starts listening for harvester updates, and
        // the Inventory kicks off its load (no-op until logged in). Deferred so it never blocks startup.
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _ = services.GetRequiredService<ViewModels.ChannelsViewModel>();
            _ = services.GetRequiredService<ViewModels.CampaignsViewModel>().EnsureLoadedAsync();
        });

        // Reconcile OS autostart with the saved settings.
        var settings = services.GetRequiredService<ISettingsStore>().Settings;
        var autostart = services.GetRequiredService<IAutostartService>();
        if (autostart.IsSupported)
            autostart.SetEnabled(settings.Autostart, settings.AutostartIntoTray);
    }

    /// <summary>Creates the main application window hosting the shell and wires tray and stats-flush handlers.</summary>
    /// <param name="activationState">Platform activation state, or null.</param>
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var shell = _services.GetRequiredService<AppShell>();
        var window = new Window(shell)
        {
            Title = "DropHarvester",
            Width = 1040,
            Height = 760,
            MinimumWidth = 820,
            MinimumHeight = 560,
        };

        // Position/size persistence is applied natively per-platform (Windows: AppWindow in the
        // lifecycle hook). Let the tray service intercept window close to keep harvesting in the tray.
        var tray = _services.GetService<ITrayService>();
        tray?.AttachWindow(window);

        // Flush batched stats on background/close so watch-time isn't lost.
        var stats = _services.GetService<IStatsService>();
        window.Stopped += (_, _) => stats?.Flush();
        window.Destroying += (_, _) => stats?.Flush();

        return window;
    }
}
