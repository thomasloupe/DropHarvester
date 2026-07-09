using AVFoundation;
using DropHarvester.Models.Events;
using DropHarvester.Services;
using Foundation;

namespace DropHarvester.Platforms.MacCatalyst;

/// <summary>
/// Plays the drop-claimed sound with AVAudioPlayer through the system's default output. Mac Catalyst
/// doesn't expose per-device output routing, so the device list is just "System default".
/// </summary>
public sealed class MacSoundService : ISoundService
{
    readonly IHarvesterEventBus _bus;
    AVAudioPlayer? _player; // retained so playback isn't cut short by GC

    /// <summary>Stores the event bus used to surface playback warnings.</summary>
    /// <param name="bus">Harvester event bus for publishing log and warning events.</param>
    public MacSoundService(IHarvesterEventBus bus) => _bus = bus;

    public bool IsSupported => true;

    /// <summary>Returns the single system-default device, since Mac Catalyst exposes no per-device routing.</summary>
    public IReadOnlyList<AudioDevice> GetOutputDevices()
        => new[] { new AudioDevice("", "System default") };

    /// <summary>Plays the given audio file through the system default output at the given volume, stopping any current playback first.</summary>
    /// <param name="filePath">Path to the audio file to play.</param>
    /// <param name="deviceId">Ignored on macOS; only the system default output is available.</param>
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
            _player?.Stop();
            _player = AVAudioPlayer.FromUrl(NSUrl.FromFilename(filePath), out var err);
            if (err is not null || _player is null)
            {
                Warn($"Couldn't play claim sound: {err?.LocalizedDescription ?? "unknown error"}");
                return;
            }
            _player.Volume = (float)Math.Clamp(volume, 0.0, 1.0);
            _player.Play();
        }
        catch (Exception ex)
        {
            Warn($"Couldn't play claim sound: {ex.Message}");
        }
    }

    /// <summary>Publishes a warning-level log event on the harvester event bus.</summary>
    /// <param name="message">The warning text.</param>
    void Warn(string message) => _bus.Publish(new LogEvent(message, HarvesterLogLevel.Warn));
}
