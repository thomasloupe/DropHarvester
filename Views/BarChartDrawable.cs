using Microsoft.Maui.Graphics;

namespace DropHarvester.Views;

/// <summary>Minimal bar chart drawn with Microsoft.Maui.Graphics - no third-party charting dep.</summary>
public sealed class BarChartDrawable : IDrawable
{
    public IReadOnlyList<(string label, int value)> Bars { get; set; } = Array.Empty<(string, int)>();
    public Color BarColor { get; set; } = Color.FromArgb("#9146FF");
    public Color TextColor { get; set; } = Color.FromArgb("#9BA1B0");

    /// <summary>Draws the bars, their value labels, and x-axis labels scaled to fit the given rect.</summary>
    /// <param name="canvas">The canvas to draw onto.</param>
    /// <param name="rect">The bounds within which the chart is rendered.</param>
    public void Draw(ICanvas canvas, RectF rect)
    {
        if (Bars.Count == 0)
            return;

        const float labelH = 18f;
        const float gap = 8f;
        var max = Math.Max(1, Bars.Max(b => b.value));
        var barAreaH = rect.Height - labelH - 6;
        var barW = (rect.Width - gap * (Bars.Count + 1)) / Bars.Count;

        canvas.FontSize = 11;
        for (int i = 0; i < Bars.Count; i++)
        {
            var (label, value) = Bars[i];
            var x = rect.Left + gap + i * (barW + gap);
            var h = (float)value / max * barAreaH;
            var y = rect.Top + barAreaH - h;

            canvas.FillColor = BarColor;
            canvas.FillRoundedRectangle(x, y, barW, h, 4);

            canvas.FontColor = TextColor;
            if (value > 0)
                canvas.DrawString(value.ToString(), x, y - 14, barW, 12, HorizontalAlignment.Center, VerticalAlignment.Center);
            canvas.DrawString(label, x, rect.Bottom - labelH, barW, labelH, HorizontalAlignment.Center, VerticalAlignment.Center);
        }
    }
}
