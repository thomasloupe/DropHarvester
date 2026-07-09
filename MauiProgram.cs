using DropHarvester.Services;
using DropHarvester.ViewModels;
using DropHarvester.Views;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;
#if WINDOWS
using DropHarvester.Platforms.Windows;
#elif MACCATALYST
using DropHarvester.Platforms.MacCatalyst;
#endif

namespace DropHarvester;

/// <summary>Builds and configures the MAUI application: DI registrations, platform services, and lifecycle hooks.</summary>
public static class MauiProgram
{
    /// <summary>Registers services, view models, pages, and platform lifecycle hooks and builds the MAUI app.</summary>
    public static MauiApp CreateMauiApp()
    {
        // Point the (MAUI-free) engine at this app's UI thread and data folder. The headless daemon
        // installs an inline dispatcher + a mounted data volume instead. Set before anything can
        // mutate a UI-bound model off-thread.
        UiDispatch.Current = new MauiUiDispatcher();
        UiDispatch.OffThreadObserved = App.LogOffThreadNotification;
        AppPaths.DataDir = FileSystem.AppDataDirectory;

        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        RegisterPlatformServices(builder.Services);

        // Shared harvesting engine: auth, GQL, inventory, watch, websockets, orchestrator, persistence,
        // event bus, stats, webhooks, debug/status server.
        builder.Services.AddDropHarvesterCore();

        builder.Services.AddSingleton<IUpdateService, UpdateService>();
        // The coordinator (instantiated at startup) subscribes to the bus and drives tray status,
        // native notifications and the claim sound.
        builder.Services.AddSingleton<AlertsCoordinator>();

        builder.Services.AddSingleton<StatusViewModel>();
        builder.Services.AddSingleton<CampaignsViewModel>();
        builder.Services.AddSingleton<ChannelsViewModel>();
        builder.Services.AddSingleton<SettingsViewModel>();
        builder.Services.AddSingleton<StatsViewModel>();
        builder.Services.AddSingleton<LogViewModel>();

        builder.Services.AddSingleton<AppShell>();
        builder.Services.AddSingleton<StatusPage>();
        builder.Services.AddSingleton<CampaignsPage>();
        builder.Services.AddSingleton<ChannelsPage>();
        builder.Services.AddSingleton<StatsPage>();
        builder.Services.AddSingleton<SettingsPage>();
        builder.Services.AddSingleton<LogPage>();

#if WINDOWS
        // Once the native window exists: wire the tray icon and restore/remember window bounds.
        builder.ConfigureLifecycleEvents(events =>
        {
            events.AddWindows(windows => windows
                .OnLaunched((app, _) =>
                {
                    // Log (and survive) any unhandled XAML/binding exception on the UI thread.
                    app.UnhandledException += (_, e) =>
                    {
                        App.CrashLog("WinUI", e.Exception);
                        e.Handled = true;
                    };
                })
                .OnWindowCreated(nativeWindow =>
                {
                    var services = IPlatformApplication.Current?.Services;
                    if (services?.GetService<ITrayService>() is WindowsTrayService tray)
                        tray.InitializeNative(nativeWindow);
                    if (services?.GetService<ISettingsStore>() is { } settings)
                        RestoreAndTrackWindowBounds(nativeWindow, settings);
                }));
        });
#endif

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }

#if WINDOWS
    /// <summary>
    /// Restore the remembered window position/size and persist it on move/resize. Uses the native
    /// AppWindow (physical pixels) because MAUI's Window.X/Y don't apply on Windows at startup.
    /// </summary>
    /// <param name="nativeWindow">The native WinUI window to position and track.</param>
    /// <param name="settings">The settings store holding and receiving the saved bounds.</param>
    static void RestoreAndTrackWindowBounds(Microsoft.UI.Xaml.Window nativeWindow, ISettingsStore settings)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
        var s = settings.Settings;

        if (s.WindowWidth is > 400 && s.WindowHeight is > 300
            && s.WindowX is { } x && s.WindowY is { } y
            && x is > -3000 and < 20000 && y is > -3000 and < 20000)
        {
            appWindow.MoveAndResize(new Windows.Graphics.RectInt32((int)x, (int)y, (int)s.WindowWidth.Value, (int)s.WindowHeight.Value));
        }

        appWindow.Changed += (aw, args) =>
        {
            if (!args.DidPositionChange && !args.DidSizeChange)
                return;
            var pos = aw.Position;
            var size = aw.Size;
            // Ignore minimized / hidden states (tiny size or off-screen -32000 position).
            if (size.Width < 200 || size.Height < 200 || pos.X < -3000 || pos.Y < -3000)
                return;
            s.WindowX = pos.X;
            s.WindowY = pos.Y;
            s.WindowWidth = size.Width;
            s.WindowHeight = size.Height;
            settings.Save();
        };
    }
#endif

    /// <summary>Registers per-platform tray / notification / autostart / sound implementations.</summary>
    /// <param name="services">The service collection to register platform services into.</param>
    static void RegisterPlatformServices(IServiceCollection services)
    {
#if WINDOWS
        services.AddSingleton<ITrayService, WindowsTrayService>();
        services.AddSingleton<INotificationService, WindowsNotificationService>();
        services.AddSingleton<IAutostartService, WindowsAutostartService>();
        services.AddSingleton<ISoundService, WindowsSoundService>();
#elif MACCATALYST
        // macOS keeps the app alive when the window closes, so harvesting continues without a tray.
        // A menu-bar (NSStatusItem) item needs AppKit interop and is validated in the macOS pass.
        services.AddSingleton<ITrayService, NoopTrayService>();
        services.AddSingleton<INotificationService, MacNotificationService>();
        services.AddSingleton<IAutostartService, MacAutostartService>();
        services.AddSingleton<ISoundService, MacSoundService>();
#else
        services.AddSingleton<ITrayService, NoopTrayService>();
        services.AddSingleton<INotificationService, NoopNotificationService>();
        services.AddSingleton<IAutostartService, NoopAutostartService>();
        services.AddSingleton<ISoundService, NoopSoundService>();
#endif
    }
}
