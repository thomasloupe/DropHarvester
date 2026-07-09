using System.Globalization;

namespace DropHarvester.Converters;

/// <summary>True when the bound string is non-null and non-whitespace - handy for IsVisible.</summary>
public sealed class StringNotEmptyConverter : IValueConverter
{
    /// <summary>Returns true when the bound value is a non-null, non-whitespace string.</summary>
    /// <param name="value">The bound source value.</param>
    /// <param name="targetType">The binding target type.</param>
    /// <param name="parameter">The converter parameter (unused).</param>
    /// <param name="culture">The binding culture.</param>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && !string.IsNullOrWhiteSpace(s);

    /// <summary>Not supported; this converter is one-way and throws if called.</summary>
    /// <param name="value">The bound target value.</param>
    /// <param name="targetType">The binding source type.</param>
    /// <param name="parameter">The converter parameter.</param>
    /// <param name="culture">The binding culture.</param>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
