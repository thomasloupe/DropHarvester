using System.Globalization;

namespace DropHarvester.Converters;

/// <summary>Returns the logical negation of a bool - handy for IsVisible bindings.</summary>
public sealed class InvertedBoolConverter : IValueConverter
{
    /// <summary>Returns the negation of the bound bool, or the value unchanged if it is not a bool.</summary>
    /// <param name="value">The bound source value.</param>
    /// <param name="targetType">The binding target type.</param>
    /// <param name="parameter">The converter parameter (unused).</param>
    /// <param name="culture">The binding culture.</param>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : value!;

    /// <summary>Returns the negation of the bound bool, or the value unchanged if it is not a bool.</summary>
    /// <param name="value">The bound target value.</param>
    /// <param name="targetType">The binding source type.</param>
    /// <param name="parameter">The converter parameter (unused).</param>
    /// <param name="culture">The binding culture.</param>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : value!;
}
