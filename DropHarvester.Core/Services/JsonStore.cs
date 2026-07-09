using System.Text.Json;

namespace DropHarvester.Services;

/// <summary>
/// Small resilient JSON persistence helper: load-or-default and best-effort save of a single
/// object to a file in the app data folder. Corrupt/unreadable files never brick the app.
/// Mirrors the pattern used by Cowculator's SessionStore.
/// </summary>
public static class JsonStore
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Resolve the full path for a data file name inside the app data folder.</summary>
    /// <param name="fileName">The bare file name.</param>
    /// <returns>The absolute path under the configured data directory.</returns>
    public static string PathFor(string fileName) => Path.Combine(AppPaths.DataDir, fileName);

    /// <summary>Load and deserialize an object from a data file, returning a new instance on any failure.</summary>
    /// <typeparam name="T">The type to deserialize.</typeparam>
    /// <param name="fileName">The bare file name to read.</param>
    /// <returns>The deserialized object, or a new instance when the file is missing or unreadable.</returns>
    public static T Load<T>(string fileName) where T : new()
    {
        try
        {
            var path = PathFor(fileName);
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<T>(json, Options) ?? new T();
            }
        }
        catch
        {
            // Corrupt/unreadable state shouldn't brick the app - start fresh.
        }
        return new T();
    }

    /// <summary>Serialize a value to a data file (best effort; transient IO errors are ignored).</summary>
    /// <typeparam name="T">The type to serialize.</typeparam>
    /// <param name="fileName">The bare file name to write.</param>
    /// <param name="value">The value to serialize.</param>
    public static void Save<T>(string fileName, T value)
    {
        try
        {
            File.WriteAllText(PathFor(fileName), JsonSerializer.Serialize(value, Options));
        }
        catch
        {
            // Best-effort persistence; ignore transient IO errors.
        }
    }

    /// <summary>Delete a data file if it exists, ignoring any error.</summary>
    /// <param name="fileName">The bare file name to delete.</param>
    public static void Delete(string fileName)
    {
        try
        {
            var path = PathFor(fileName);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Ignore - it'll just be overwritten on the next save.
        }
    }
}
