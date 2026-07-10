using DropHarvester.Services;
using DropHarvester.Services.Twitch;
using Microsoft.Extensions.DependencyInjection;

namespace DropHarvester;

/// <summary>
/// Registers the shared harvesting engine and its supporting services (persistence, event bus, auth,
/// stats, webhooks, debug/status server). Both front-ends call this: the desktop MAUI app and the
/// headless <c>DropHarvester.Daemon</c>. Each then adds its own layer on top - the app registers
/// platform tray/notifications/sound, the installer-based updater, view-models and pages; the daemon
/// registers its hosted service and inline UI dispatcher.
/// </summary>
public static class DropHarvesterCoreServiceCollectionExtensions
{
    /// <summary>Register the shared harvesting engine and its supporting services into the DI container.</summary>
    /// <param name="services">The service collection to add registrations to.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddDropHarvesterCore(this IServiceCollection services)
    {
        // Persistence + config + in-process event bus.
        services.AddSingleton<IAuthStore, AuthStore>();
        services.AddSingleton<ISettingsStore, SettingsStore>();
        services.AddSingleton<IHarvesterEventBus, HarvesterEventBus>();
        services.AddSingleton<IClaimLedger, ClaimLedger>();

        // Auth + lifetime stats.
        services.AddSingleton<ITwitchAuth, TwitchAuthService>();
        services.AddSingleton<IStatsService, StatsService>();

        // Harvesting engine.
        services.AddSingleton<IGqlClient, GqlClient>();
        services.AddSingleton<IWatchService, WatchService>();
        services.AddSingleton<IInventoryService, InventoryService>();
        services.AddSingleton<IChannelManager, ChannelManager>();
        services.AddSingleton<IWebsocketPool, WebsocketPool>();
        services.AddSingleton<IGameSearchService, GameSearchService>();
        services.AddSingleton<IChannelPointsService, ChannelPointsService>();
        services.AddSingleton<IHarvesterOrchestrator, HarvesterOrchestrator>();

        // Webhook alerts + the localhost debug/status server.
        services.AddSingleton<IWebhookNotifier, WebhookNotifier>();
        services.AddSingleton<IDebugServer, DebugServer>();

        return services;
    }
}
