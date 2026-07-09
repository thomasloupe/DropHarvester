using System.Globalization;

namespace DropHarvester.Converters;

/// <summary>
/// Maps a bool to one of two colors. The converter parameter is "TrueResourceKey|FalseResourceKey"
/// referencing keys in the app's merged resource dictionaries (e.g. "DhGreen|DhMuted").
/// Falls back to green/muted when no parameter is supplied.
/// </summary>
public sealed class BoolToColorConverter : IValueConverter
{
    /// <summary>Resolves the true/false resource-key color for the bound bool, falling back to green/gray.</summary>
    /// <param name="value">The bound source value (expected to be a bool).</param>
    /// <param name="targetType">The binding target type.</param>
    /// <param name="parameter">The "TrueKey|FalseKey" resource-key pair.</param>
    /// <param name="culture">The binding culture.</param>
    /// <returns>The resolved Color for the current bool state.</returns>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        var (trueKey, falseKey) = ParseKeys(parameter as string);
        var key = flag ? trueKey : falseKey;
        var res = Application.Current?.Resources;
        if (res is not null && res.TryGetValue(key, out var color) && color is Color c)
            return c;
        return flag ? Colors.LimeGreen : Colors.Gray;
    }

    /// <summary>Not supported; this converter is one-way and throws if called.</summary>
    /// <param name="value">The bound target value.</param>
    /// <param name="targetType">The binding source type.</param>
    /// <param name="parameter">The converter parameter.</param>
    /// <param name="culture">The binding culture.</param>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    /// <summary>Splits the converter parameter into its true and false resource keys, defaulting when absent.</summary>
    /// <param name="param">The "TrueKey|FalseKey" parameter string, or null.</param>
    /// <returns>The resolved true and false resource keys.</returns>
    static (string trueKey, string falseKey) ParseKeys(string? param)
    {
        if (string.IsNullOrWhiteSpace(param))
            return ("DhGreen", "DhMuted");
        var parts = param.Split('|', 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : (parts[0], "DhMuted");
    }
}
