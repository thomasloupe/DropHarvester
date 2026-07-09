using System.Globalization;
using System.Text.Json;

namespace DropHarvester.Services.Twitch;

/// <summary>Defensive JsonElement navigation - Twitch responses are deeply nested and vary.</summary>
public static class JsonExtensions
{
    /// <summary>Get a child object/array property, or null if absent (or the element is null).</summary>
    /// <param name="el">Element to read the property from.</param>
    /// <param name="name">Property name to look up.</param>
    public static JsonElement? Prop(this JsonElement el, string name)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var child)
            && child.ValueKind != JsonValueKind.Null)
            return child;
        return null;
    }

    /// <summary>Walk a chain of property names, stopping at the first missing/null link.</summary>
    /// <param name="el">Element to start the walk from.</param>
    /// <param name="names">Property names to descend through in order.</param>
    public static JsonElement? Path(this JsonElement el, params string[] names)
    {
        JsonElement? cur = el;
        foreach (var name in names)
        {
            cur = cur?.Prop(name);
            if (cur is null) return null;
        }
        return cur;
    }

    /// <summary>Get a string-valued property, or null if absent or not a string.</summary>
    /// <param name="el">Element to read the property from.</param>
    /// <param name="name">Property name to look up.</param>
    public static string? Str(this JsonElement el, string name)
    {
        var p = el.Prop(name);
        return p?.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
    }

    /// <summary>Get this element's string value, or null if it isn't a string.</summary>
    /// <param name="el">Element to read as a string.</param>
    public static string? AsStr(this JsonElement el)
        => el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    /// <summary>Get an int-valued property, parsing numeric strings, or the fallback if absent/unparseable.</summary>
    /// <param name="el">Element to read the property from.</param>
    /// <param name="name">Property name to look up.</param>
    /// <param name="fallback">Value returned when the property is missing or not a parseable integer.</param>
    public static int IntOr(this JsonElement el, string name, int fallback = 0)
    {
        var p = el.Prop(name);
        if (p is null) return fallback;
        return p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var i)
            ? i
            : int.TryParse(p.Value.AsStr(), out var j) ? j : fallback;
    }

    /// <summary>Get a bool-valued property, or the fallback if absent or not a boolean.</summary>
    /// <param name="el">Element to read the property from.</param>
    /// <param name="name">Property name to look up.</param>
    /// <param name="fallback">Value returned when the property is missing or not a boolean.</param>
    public static bool BoolOr(this JsonElement el, string name, bool fallback = false)
    {
        var p = el.Prop(name);
        return p?.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    /// <summary>Get a property parsed as a UTC DateTimeOffset, or null if absent or unparseable.</summary>
    /// <param name="el">Element to read the property from.</param>
    /// <param name="name">Property name to look up.</param>
    public static DateTimeOffset? Date(this JsonElement el, string name)
    {
        var s = el.Str(name);
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
            ? dt
            : null;
    }

    /// <summary>Enumerate an array property; empty if absent or not an array.</summary>
    /// <param name="el">Element to read the property from.</param>
    /// <param name="name">Property name of the array to enumerate.</param>
    public static IEnumerable<JsonElement> Items(this JsonElement el, string name)
    {
        var p = el.Prop(name);
        if (p?.ValueKind == JsonValueKind.Array)
            foreach (var item in p.Value.EnumerateArray())
                yield return item;
    }

    /// <summary>Enumerate this element as an array; empty if it isn't one.</summary>
    /// <param name="el">Element to enumerate as an array.</param>
    public static IEnumerable<JsonElement> AsArray(this JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Array)
            foreach (var item in el.EnumerateArray())
                yield return item;
    }
}
