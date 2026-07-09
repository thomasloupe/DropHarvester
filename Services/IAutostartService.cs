namespace DropHarvester.Services;

/// <summary>Register/unregister the app to start when the user logs into the OS.</summary>
public interface IAutostartService
{
    bool IsSupported { get; }

    /// <summary>Returns whether autostart is currently registered for this user.</summary>
    bool IsEnabled();

    /// <summary>Register/unregister autostart; when <paramref name="intoTray"/> the launch is hidden.</summary>
    /// <param name="enabled">True to register autostart, false to unregister it.</param>
    /// <param name="intoTray">When true, the auto-launched app starts hidden in the tray.</param>
    void SetEnabled(bool enabled, bool intoTray = false);
}

/// <summary>Fallback where autostart isn't wired for the platform.</summary>
public sealed class NoopAutostartService : IAutostartService
{
    public bool IsSupported => false;

    /// <summary>Always returns false; autostart is not supported here.</summary>
    public bool IsEnabled() => false;

    /// <summary>No-op; autostart is not supported here.</summary>
    /// <param name="enabled">Ignored.</param>
    /// <param name="intoTray">Ignored.</param>
    public void SetEnabled(bool enabled, bool intoTray = false) { }
}
