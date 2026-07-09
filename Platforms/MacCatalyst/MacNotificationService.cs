using DropHarvester.Services;
using UserNotifications;

namespace DropHarvester.Platforms.MacCatalyst;

/// <summary>macOS notification-center notifications via UserNotifications.</summary>
public sealed class MacNotificationService : INotificationService
{
    /// <summary>Requests alert and sound notification authorization from the user.</summary>
    public MacNotificationService()
    {
        try
        {
            UNUserNotificationCenter.Current.RequestAuthorization(
                UNAuthorizationOptions.Alert | UNAuthorizationOptions.Sound,
                (granted, error) => { });
        }
        catch
        {
            // If authorization can't be requested, Notify() simply no-ops.
        }
    }

    /// <summary>Posts a notification-center notification with the given title and body.</summary>
    /// <param name="title">The notification title.</param>
    /// <param name="message">The notification body text.</param>
    public void Notify(string title, string message)
    {
        try
        {
            var content = new UNMutableNotificationContent
            {
                Title = title,
                Body = message,
            };
            var request = UNNotificationRequest.FromIdentifier(
                Guid.NewGuid().ToString(), content, trigger: null);
            UNUserNotificationCenter.Current.AddNotificationRequest(request, error => { });
        }
        catch
        {
            // Best-effort; never disrupt harvesting.
        }
    }
}
