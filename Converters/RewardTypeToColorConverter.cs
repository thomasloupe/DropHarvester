using System.Globalization;

namespace DropHarvester.Converters;

/// <summary>
/// Maps a reward-type label ("EMOTE" / "BADGE" / "DROP") to the tag color used on the drop cards and in
/// the legend: emote = blue, badge = gold, item drop = green. Anything else falls back to the muted gray.
/// Keeps the type tags color-coded consistently everywhere from one place.
/// </summary>
public sealed class RewardTypeToColorConverter : IValueConverter
{
    /// <summary>Resolves the tag color for the bound reward-type label.</summary>
    /// <param name="value">The reward-type label string (EMOTE / BADGE / DROP).</param>
    /// <param name="targetType">The binding target type.</param>
    /// <param name="parameter">Unused.</param>
    /// <param name="culture">The binding culture.</param>
    /// <returns>The color for that reward type.</returns>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = (value as string) switch
        {
            "EMOTE" => "DhBlue",
            "BADGE" => "DhGold",
            "DROP" => "DhGreen",
            _ => "DhMuted",
        };
        var res = Application.Current?.Resources;
        return res is not null && res.TryGetValue(key, out var color) && color is Color c ? c : Colors.Gray;
    }

    /// <summary>Not supported; this converter is one-way and throws if called.</summary>
    /// <param name="value">The bound target value.</param>
    /// <param name="targetType">The binding source type.</param>
    /// <param name="parameter">The converter parameter.</param>
    /// <param name="culture">The binding culture.</param>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
