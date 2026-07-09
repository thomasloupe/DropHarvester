using DropHarvester.Services;
using Microsoft.Win32;

namespace DropHarvester.Platforms.Windows;

/// <summary>Autostart via the per-user Run registry key.</summary>
public sealed class WindowsAutostartService : IAutostartService
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "DropHarvester";

    public bool IsSupported => true;

    /// <summary>Returns whether the DropHarvester value exists under the per-user Run key.</summary>
    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Adds or removes the autostart Run-key entry, optionally passing --tray to start hidden.</summary>
    /// <param name="enabled">True to install the autostart entry; false to remove it.</param>
    /// <param name="intoTray">When enabling, whether to launch minimized to the tray.</param>
    public void SetEnabled(bool enabled, bool intoTray = false)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null)
                return;

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                    key.SetValue(ValueName, intoTray ? $"\"{exe}\" --tray" : $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Non-fatal: autostart is a convenience, not required for harvesting.
        }
    }
}
