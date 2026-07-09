using System.ComponentModel;

namespace DropHarvester.Localization;

/// <summary>
/// Binding source for localized UI text. Exposes an indexer keyed by string key so XAML can bind
/// <c>Text="{loc:Tr Some_Key}"</c>; when the active language changes it raises an indexer PropertyChanged
/// so every bound string re-reads its translation live, with no app restart.
/// </summary>
public sealed class LocalizationManager : INotifyPropertyChanged
{
    /// <summary>The shared instance XAML bindings resolve against.</summary>
    public static LocalizationManager Instance { get; } = new();

    // Raise with an EMPTY property name (not "Item[]"): MAUI only refreshes an indexer binding
    // ({loc:Tr Key} -> [Key]) when the changed name is null/empty or an exact match, so "Item[]" was
    // silently ignored and the UI kept its startup-language text until a page was rebuilt.
    LocalizationManager() =>
        Loc.Changed += () => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    /// <summary>The translated text for a key in the active language (English fallback).</summary>
    /// <param name="key">The string key to translate.</param>
    public string this[string key] => Loc.T(key);

    public event PropertyChangedEventHandler? PropertyChanged;
}
