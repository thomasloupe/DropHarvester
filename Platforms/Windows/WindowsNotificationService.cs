using DropHarvester.Services;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace DropHarvester.Platforms.Windows;

/// <summary>Windows toast notifications via the Windows App SDK (works unpackaged).</summary>
public sealed class WindowsNotificationService : INotificationService
{
    static bool _registered;

    /// <summary>Registers the app with the Windows App SDK notification manager once per process.</summary>
    public WindowsNotificationService()
    {
        try
        {
            if (!_registered)
            {
                AppNotificationManager.Default.Register();
                _registered = true;
            }
        }
        catch
        {
            // If registration fails (rare unpackaged edge cases), Notify() simply no-ops.
        }
    }

    /// <summary>Builds and shows a two-line toast notification.</summary>
    /// <param name="title">The toast's first line.</param>
    /// <param name="message">The toast's second line.</param>
    public void Notify(string title, string message)
    {
        try
        {
            var toast = new AppNotificationBuilder()
                .AddText(title)
                .AddText(message)
                .BuildNotification();
            AppNotificationManager.Default.Show(toast);
        }
        catch
        {
            // Best-effort; never let a notification failure disrupt harvesting.
        }
    }
}
