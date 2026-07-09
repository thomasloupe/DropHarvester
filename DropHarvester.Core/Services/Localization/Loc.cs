namespace DropHarvester.Localization;

/// <summary>
/// App-wide string localization. Holds the active language and per-language key -> text tables, with
/// English as the base and the fallback for any missing entry. Both the UI (through a binding manager)
/// and engine-emitted log/status text resolve their strings here, so a language change re-translates
/// everything that re-reads its text.
/// </summary>
public static class Loc
{
    // language code -> (key -> translated text). English ("en") is the base and the fallback; a language
    // with no table (or a missing key) falls through to English, which is how the not-yet-translated
    // languages behave until their tables are filled in.
    static readonly Dictionary<string, Dictionary<string, string>> Tables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = LocStrings.En,
        ["es"] = LocStrings.Es,
        ["fr"] = LocStrings.Fr,
        ["de"] = LocStrings.De,
        ["ru"] = LocStrings.Ru,
        ["zh-Hans"] = LocStrings.ZhHans,
        ["ja"] = LocStrings.Ja,
        ["ko"] = LocStrings.Ko,
        ["nl"] = LocStrings.Nl,
    };

    static string _culture = "en";

    /// <summary>The active language code (e.g. "en", "es"). Assigning a new value raises <see cref="Changed"/>.</summary>
    public static string Culture
    {
        get => _culture;
        set
        {
            var c = string.IsNullOrWhiteSpace(value) ? "en" : value.Trim();
            if (string.Equals(c, _culture, StringComparison.OrdinalIgnoreCase))
                return;
            _culture = c;
            Changed?.Invoke();
        }
    }

    /// <summary>Raised whenever the active language changes, so bound UI can refresh its text.</summary>
    public static event Action? Changed;

    /// <summary>Translate a key into the active language, falling back to English and then to the key
    /// itself (so a missing key is visible rather than blank).</summary>
    /// <param name="key">The string key to look up.</param>
    public static string T(string key)
    {
        if (Tables.TryGetValue(_culture, out var table) && table.TryGetValue(key, out var s) && !string.IsNullOrEmpty(s))
            return s;
        if (Tables.TryGetValue("en", out var en) && en.TryGetValue(key, out var e))
            return e;
        return key;
    }

    /// <summary>Translate a key whose value is a composite format string and fill in the arguments.</summary>
    /// <param name="key">The string key to look up.</param>
    /// <param name="args">Arguments for the format placeholders.</param>
    public static string T(string key, params object?[] args) => string.Format(T(key), args);
}
