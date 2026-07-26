using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DropHarvester.Services;

/// <summary>
/// Resilient JSON persistence for a single object per file in the app data folder. Built so a user's
/// settings/stats/ledger can NEVER be silently wiped by a bad load or an interrupted write:
/// <list type="bullet">
/// <item>Writes are atomic and durable - the new content is written to a temp file, flushed to disk, then
/// atomically renamed over the live file, so a crash or an installer killing the app mid-save can never
/// leave a half-written (and then unreadable) file behind.</item>
/// <item>Every good load/save keeps a last-known-good <c>.bak</c>. If the live file is ever unreadable, the
/// backup is used automatically and the live file is restored from it.</item>
/// <item>An unreadable live file is PRESERVED (copied to <c>.corrupt</c>) and never overwritten blindly, so
/// data is always recoverable - the old behavior returned defaults and let the next save destroy the file.</item>
/// <item>Loads are tolerant: a strict parse is tried first, then a field-by-field merge onto defaults, so a
/// future schema change that only affects one property loses that one field instead of the whole file.</item>
/// </list>
/// A genuinely missing file (first run) still returns defaults silently.
/// </summary>
public static class JsonStore
{
    // Read + write options. WriteIndented shapes the output; the tolerance flags only affect reading (they're
    // ignored when serializing), so old files that pick up a stray comment/trailing comma/quoted-number still load.
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    // One lock per file path so concurrent saves/loads of the SAME file can't interleave temp writes/renames.
    static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.OrdinalIgnoreCase);
    static object LockFor(string path) => Locks.GetOrAdd(path, _ => new object());

    /// <summary>Resolve the full path for a data file name inside the app data folder.</summary>
    /// <param name="fileName">The bare file name.</param>
    /// <returns>The absolute path under the configured data directory.</returns>
    public static string PathFor(string fileName) => Path.Combine(AppPaths.DataDir, fileName);

    /// <summary>Load and deserialize an object from a data file. Falls back to a last-known-good backup when
    /// the live file is unreadable (preserving the unreadable file for recovery), and only returns a fresh
    /// instance when the file genuinely doesn't exist or nothing anywhere can be read.</summary>
    /// <typeparam name="T">The type to deserialize.</typeparam>
    /// <param name="fileName">The bare file name to read.</param>
    /// <returns>The deserialized object, a recovered backup, or a new instance as a last resort.</returns>
    public static T Load<T>(string fileName) where T : new()
    {
        var path = PathFor(fileName);
        var bak = path + ".bak";
        lock (LockFor(path))
        {
            if (File.Exists(path))
            {
                if (TryReadFile<T>(path, out var value))
                {
                    // Good read: keep the backup current so a later corruption has something to fall back to.
                    TryRefreshBackup(path, bak);
                    return value;
                }
                // Present but unreadable: preserve it (never destroy the user's data), then try the backup.
                PreserveCorrupt(path);
            }

            // Live file missing or unreadable: recover from the last-known-good backup if there is one.
            if (File.Exists(bak) && TryReadFile<T>(bak, out var recovered))
            {
                TryCopy(bak, path); // restore the live file so it and the next save stay consistent
                return recovered;
            }

            // Nothing readable: genuine first run, or unrecoverable (the originals are preserved as .corrupt).
            return new T();
        }
    }

    /// <summary>Serialize a value to a data file atomically and durably, keeping the previous good content as
    /// a <c>.bak</c>. A failed save leaves the existing file (and its backup) untouched.</summary>
    /// <typeparam name="T">The type to serialize.</typeparam>
    /// <param name="fileName">The bare file name to write.</param>
    /// <param name="value">The value to serialize.</param>
    public static void Save<T>(string fileName, T value)
    {
        var path = PathFor(fileName);
        var tmp = path + ".tmp";
        var bak = path + ".bak";
        lock (LockFor(path))
        {
            try
            {
                var json = JsonSerializer.Serialize(value, Options);
                WriteAllTextDurable(tmp, json);   // fully written + flushed before it's put in place
                File.Move(tmp, path, overwrite: true); // atomic swap: the live file is never half-written
                TryCopy(path, bak);                // advance the last-known-good backup to match
            }
            catch
            {
                // Best-effort persistence: on any failure leave the previous good file and backup as they were.
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>Delete a data file and its sidecars (backup/temp/corrupt), ignoring any error.</summary>
    /// <param name="fileName">The bare file name to delete.</param>
    public static void Delete(string fileName)
    {
        var path = PathFor(fileName);
        lock (LockFor(path))
            foreach (var p in new[] { path, path + ".bak", path + ".tmp", path + ".corrupt" })
            {
                try { if (File.Exists(p)) File.Delete(p); }
                catch { /* it'll just be overwritten on the next save */ }
            }
    }

    /// <summary>Read and deserialize a file, returning false (rather than throwing) on any read/parse failure.</summary>
    /// <typeparam name="T">The type to deserialize.</typeparam>
    /// <param name="path">Full path to the file.</param>
    /// <param name="value">The deserialized value on success.</param>
    /// <returns>True when the file was read and produced a value.</returns>
    static bool TryReadFile<T>(string path, out T value) where T : new()
    {
        value = default!;
        try
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
                return false; // an empty file (e.g. a truncated write) is not "valid defaults" - recover instead
            return TryDeserialize(json, out value);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Deserialize JSON tolerantly: a strict parse first, then a per-property merge onto defaults so a
    /// single incompatible field can't discard the whole object.</summary>
    /// <typeparam name="T">The type to deserialize.</typeparam>
    /// <param name="json">The JSON text.</param>
    /// <param name="value">The resulting value on success.</param>
    /// <returns>True when a value was produced.</returns>
    static bool TryDeserialize<T>(string json, out T value) where T : new()
    {
        // Strict path - what almost every load hits.
        try
        {
            var strict = JsonSerializer.Deserialize<T>(json, Options);
            if (strict is not null) { value = strict; return true; }
        }
        catch { /* fall through to the tolerant merge */ }

        // Tolerant path - only for JSON objects mapping to a class with settable properties. Each property is
        // deserialized independently; one that fails (a changed type in a future version) keeps its default
        // instead of throwing away every other setting.
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                var result = new T();
                var props = typeof(T)
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p is { CanRead: true, CanWrite: true })
                    .ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);
                foreach (var jp in doc.RootElement.EnumerateObject())
                {
                    if (!props.TryGetValue(jp.Name, out var pi))
                        continue; // unknown/removed property - ignore
                    try
                    {
                        pi.SetValue(result, jp.Value.Deserialize(pi.PropertyType, Options));
                    }
                    catch { /* incompatible value for this one field - keep the default */ }
                }
                value = result;
                return true;
            }
        }
        catch { /* not recoverable */ }

        value = default!;
        return false;
    }

    /// <summary>Write text to a temp path and flush it all the way to disk before returning, so the file is
    /// complete on disk the instant it's renamed into place.</summary>
    /// <param name="tmpPath">Temp file to write.</param>
    /// <param name="contents">Text to write.</param>
    static void WriteAllTextDurable(string tmpPath, string contents)
    {
        var bytes = Encoding.UTF8.GetBytes(contents);
        using var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.Write(bytes, 0, bytes.Length);
        fs.Flush(flushToDisk: true);
    }

    /// <summary>Copy the live file to the backup when the backup is missing or older, ignoring errors.</summary>
    /// <param name="path">The live file.</param>
    /// <param name="bak">The backup path.</param>
    static void TryRefreshBackup(string path, string bak)
    {
        try
        {
            if (!File.Exists(bak) || File.GetLastWriteTimeUtc(path) > File.GetLastWriteTimeUtc(bak))
                File.Copy(path, bak, overwrite: true);
        }
        catch { /* backup is best-effort */ }
    }

    /// <summary>Copy a file over another, ignoring errors.</summary>
    /// <param name="from">Source path.</param>
    /// <param name="to">Destination path.</param>
    static void TryCopy(string from, string to)
    {
        try { File.Copy(from, to, overwrite: true); } catch { /* best-effort */ }
    }

    /// <summary>Preserve an unreadable file as <c>.corrupt</c> so it's never lost to a blind overwrite.</summary>
    /// <param name="path">The unreadable live file.</param>
    static void PreserveCorrupt(string path)
    {
        try { File.Copy(path, path + ".corrupt", overwrite: true); } catch { /* best-effort */ }
    }
}
