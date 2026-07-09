using DropHarvester.Models;

namespace DropHarvester.Services;

/// <summary>Loads/saves <see cref="AppSettings"/> as JSON in the app data folder.</summary>
public interface ISettingsStore
{
    AppSettings Settings { get; }
    /// <summary>Persist the current settings.</summary>
    void Save();
}

/// <summary>Default <see cref="ISettingsStore"/> backed by <see cref="JsonStore"/> and a settings.json file.</summary>
public sealed class SettingsStore : ISettingsStore
{
    const string FileName = "settings.json";

    public AppSettings Settings { get; }

    /// <summary>Load persisted settings (or defaults) into memory.</summary>
    public SettingsStore()
    {
        Settings = JsonStore.Load<AppSettings>(FileName);
    }

    /// <summary>Persist the current settings.</summary>
    public void Save() => JsonStore.Save(FileName, Settings);
}
