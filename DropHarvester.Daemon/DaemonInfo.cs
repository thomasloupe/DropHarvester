using System.Reflection;

namespace DropHarvester.Daemon;

/// <summary>Build-time facts about the daemon (its version, for logs and the /status endpoint).</summary>
public static class DaemonInfo
{
    public static string Version { get; } =
        typeof(DaemonInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion?.Split('+')[0]
        ?? typeof(DaemonInfo).Assembly.GetName().Version?.ToString()
        ?? "unknown";
}
