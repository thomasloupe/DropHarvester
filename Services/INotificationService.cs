namespace DropHarvester.Services;

/// <summary>Native desktop notifications (Windows toast / macOS notification center).</summary>
public interface INotificationService
{
    /// <summary>Shows a native desktop notification with the given title and body.</summary>
    /// <param name="title">Notification title.</param>
    /// <param name="message">Notification body text.</param>
    void Notify(string title, string message);
}

/// <summary>Fallback used where a platform notification implementation isn't available.</summary>
public sealed class NoopNotificationService : INotificationService
{
    /// <summary>No-op; desktop notifications are not supported here.</summary>
    /// <param name="title">Ignored.</param>
    /// <param name="message">Ignored.</param>
    public void Notify(string title, string message) { }
}
