using DropHarvester.Services;

namespace DropHarvester.Platforms.MacCatalyst;

/// <summary>
/// Autostart on macOS via a per-user LaunchAgent plist that `open`s the .app at login. Enabled
/// state is simply whether the plist exists.
/// </summary>
public sealed class MacAutostartService : IAutostartService
{
    const string Label = "com.dropharvester.app";

    public bool IsSupported => true;

    static string PlistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", $"{Label}.plist");

    /// <summary>Returns whether the login LaunchAgent plist exists.</summary>
    public bool IsEnabled() => File.Exists(PlistPath);

    /// <summary>Writes or deletes the login LaunchAgent plist to enable or disable autostart.</summary>
    /// <param name="enabled">True to install the LaunchAgent; false to remove it.</param>
    /// <param name="intoTray">Unused on macOS; present for interface parity.</param>
    public void SetEnabled(bool enabled, bool intoTray = false)
    {
        try
        {
            if (enabled)
            {
                var appPath = ResolveAppBundlePath();
                if (appPath is null)
                    return;
                Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
                File.WriteAllText(PlistPath, BuildPlist(appPath));
            }
            else if (File.Exists(PlistPath))
            {
                File.Delete(PlistPath);
            }
        }
        catch
        {
            // Non-fatal convenience feature.
        }
    }

    /// <summary>Builds the LaunchAgent plist XML that opens the given app bundle at login.</summary>
    /// <param name="appPath">Filesystem path to the .app bundle to launch.</param>
    static string BuildPlist(string appPath) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>Label</key>
            <string>{Label}</string>
            <key>ProgramArguments</key>
            <array>
                <string>/usr/bin/open</string>
                <string>{appPath}</string>
            </array>
            <key>RunAtLoad</key>
            <true/>
        </dict>
        </plist>
        """;

    /// <summary>Walk up from the running binary to the enclosing *.app bundle directory.</summary>
    static string? ResolveAppBundlePath()
    {
        var dir = Environment.ProcessPath;
        while (!string.IsNullOrEmpty(dir))
        {
            if (dir.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
