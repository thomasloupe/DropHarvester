using DropHarvester.Models.Events;
using DropHarvester.Services;
using NAudio.Wave;

namespace DropHarvester.Platforms.Windows;

/// <summary>
/// Plays the drop-claimed sound with NAudio's WaveOut, which auto-converts any common audio format
/// and lets us target a specific output device by number (-1 = system default). WaveOutEvent runs on
/// its own thread and needs no window handle, so it's safe to call from the harvesting background thread.
/// </summary>
public sealed class WindowsSoundService : ISoundService
{
    readonly IHarvesterEventBus _bus;
    readonly object _lock = new();
    IWavePlayer? _output;
    WaveStream? _reader;

    /// <summary>Stores the event bus used to surface playback warnings.</summary>
    /// <param name="bus">Harvester event bus for publishing log and warning events.</param>
    public WindowsSoundService(IHarvesterEventBus bus) => _bus = bus;

    public bool IsSupported => true;

    /// <summary>Enumerates the available WaveOut output devices, prefixed with the system default.</summary>
    public IReadOnlyList<AudioDevice> GetOutputDevices()
    {
        // -1 is WAVE_MAPPER (the system's default output device).
        var list = new List<AudioDevice> { new("-1", "System default") };
        try
        {
            for (var n = 0; n < WaveOut.DeviceCount; n++)
                list.Add(new AudioDevice(n.ToString(), WaveOut.GetCapabilities(n).ProductName));
        }
        catch { /* enumeration is best-effort */ }
        return list;
    }

    /// <summary>Plays the given audio file on the chosen output device at the given volume, stopping any current playback first.</summary>
    /// <param name="filePath">Path to the audio file to play.</param>
    /// <param name="deviceId">WaveOut device number as a string, or null/invalid for the system default.</param>
    /// <param name="volume">Playback volume from 0.0 to 1.0.</param>
    public void Play(string filePath, string? deviceId, double volume)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                Warn($"Claim sound file not found: {filePath}");
                return;
            }
            Stop(); // don't overlap with a previous still-playing sound

            var reader = CreateReader(filePath);
            var output = new WaveOutEvent
            {
                DeviceNumber = int.TryParse(deviceId, out var n) ? n : -1,
                Volume = (float)Math.Clamp(volume, 0.0, 1.0),
            };
            output.PlaybackStopped += (_, _) =>
            {
                lock (_lock)
                {
                    try { output.Dispose(); } catch { }
                    try { reader.Dispose(); } catch { }
                    if (ReferenceEquals(_output, output)) { _output = null; _reader = null; }
                }
            };
            output.Init(reader);
            output.Play();
            lock (_lock) { _output = output; _reader = reader; }
        }
        catch (Exception ex)
        {
            Warn($"Couldn't play claim sound: {ex.Message}");
            Stop();
        }
    }

    // AudioFileReader covers wav/mp3/aiff; MediaFoundationReader covers m4a/aac/wma/etc.
    /// <summary>Opens a WaveStream reader appropriate to the file's extension.</summary>
    /// <param name="path">Path to the audio file.</param>
    static WaveStream CreateReader(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".wav" or ".mp3" or ".aiff" or ".aif"
            ? new AudioFileReader(path)
            : new MediaFoundationReader(path);
    }

    /// <summary>Stops and disposes any in-progress playback and its reader.</summary>
    void Stop()
    {
        lock (_lock)
        {
            try { _output?.Stop(); } catch { }
            try { _output?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            _output = null;
            _reader = null;
        }
    }

    /// <summary>Publishes a warning-level log event on the harvester event bus.</summary>
    /// <param name="message">The warning text.</param>
    void Warn(string message) => _bus.Publish(new LogEvent(message, HarvesterLogLevel.Warn));
}
