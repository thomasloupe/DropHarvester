namespace DropHarvester.Services;

/// <summary>
/// System-tray (Windows) / menu-bar (macOS) presence for a long-running harvester. Lets the app
/// keep harvesting in the background while the window is hidden. Implemented per-platform behind
/// this interface; a no-op fallback is used where an implementation isn't available yet.
/// </summary>
public interface ITrayService
{
    /// <summary>Whether a real tray/menu-bar item is available on this platform.</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Wire the app window so closing it hides to tray instead of quitting, and create the
    /// tray/menu-bar item with its context menu (status, pause/resume, open, quit).
    /// </summary>
    /// <param name="window">The app window to hook and control from the tray.</param>
    void AttachWindow(Window window);

    /// <summary>Update the tooltip / status line shown on the tray item.</summary>
    /// <param name="status">The status text to display.</param>
    void SetStatus(string status);

    /// <summary>Bring the window back into view from the tray.</summary>
    void ShowWindow();
}

/// <summary>Fallback used until a platform tray implementation is registered. Does nothing.</summary>
public sealed class NoopTrayService : ITrayService
{
    public bool IsSupported => false;

    /// <summary>No-op; no tray item is available here.</summary>
    /// <param name="window">Ignored.</param>
    public void AttachWindow(Window window) { }

    /// <summary>No-op; no tray item is available here.</summary>
    /// <param name="status">Ignored.</param>
    public void SetStatus(string status) { }

    /// <summary>No-op; no tray item is available here.</summary>
    public void ShowWindow() { }
}
