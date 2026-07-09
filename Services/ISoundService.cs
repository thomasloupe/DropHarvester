namespace DropHarvester.Services;

/// <summary>An audio output device the claim sound can be routed to.</summary>
public sealed record AudioDevice(string Id, string Name);

/// <summary>Plays a user-chosen sound file (e.g. on drop-claimed), optionally through a chosen output
/// device. Per-platform: Windows routes to any device; macOS uses the system default output.</summary>
public interface ISoundService
{
    /// <summary>True where audio playback is implemented on this platform.</summary>
    bool IsSupported { get; }

    /// <summary>Output devices, "System default" first. Empty when unsupported.</summary>
    IReadOnlyList<AudioDevice> GetOutputDevices();

    /// <summary>Play a sound file through the given device id (null/empty = system default) at the
    /// given volume (0.0 silent to 1.0 full). Best-effort and non-throwing - failures are logged,
    /// never bubbled into the harvesting loop.</summary>
    /// <param name="filePath">Path to the audio file to play.</param>
    /// <param name="deviceId">Target output device id; null or empty routes to the system default.</param>
    /// <param name="volume">Playback volume from 0.0 (silent) to 1.0 (full).</param>
    void Play(string filePath, string? deviceId, double volume);
}

/// <summary>Fallback where no platform audio backend is available.</summary>
public sealed class NoopSoundService : ISoundService
{
    public bool IsSupported => false;

    /// <summary>Returns an empty device list; audio is not supported here.</summary>
    public IReadOnlyList<AudioDevice> GetOutputDevices() => Array.Empty<AudioDevice>();

    /// <summary>No-op; audio playback is not supported here.</summary>
    /// <param name="filePath">Ignored.</param>
    /// <param name="deviceId">Ignored.</param>
    /// <param name="volume">Ignored.</param>
    public void Play(string filePath, string? deviceId, double volume) { }
}
