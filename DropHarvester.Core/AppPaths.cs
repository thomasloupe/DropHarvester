namespace DropHarvester;

/// <summary>
/// Where the app persists its data (token, settings, stats, history). The MAUI app points this at
/// <c>FileSystem.AppDataDirectory</c> at startup; the headless daemon points it at its mounted data
/// volume (env <c>DROPHARVESTER_DATA</c>, default <c>/data</c>). Kept out of the engine so
/// <see cref="Services.JsonStore"/> and the stats/export paths never reference MAUI.
/// </summary>
public static class AppPaths
{
    /// <summary>Directory for all persisted JSON/CSV. Defaults to the current directory so a bare
    /// headless process still works; hosts should set this explicitly at startup.</summary>
    public static string DataDir { get; set; } = Directory.GetCurrentDirectory();
}
