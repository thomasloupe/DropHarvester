using DropHarvester.ViewModels;

namespace DropHarvester.Views;

/// <summary>Code-behind for the Stats tab, wiring the daily-drops bar chart and its hover tooltip.</summary>
public partial class StatsPage : ContentPage
{
    readonly StatsViewModel _vm;
    readonly BarChartDrawable _chart = new();
    List<DateOnly> _chartDays = new();

    /// <summary>Initializes the page, binds the view model, and attaches the chart drawable and its redraw handler.</summary>
    /// <param name="vm">The stats view model to bind to.</param>
    public StatsPage(StatsViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;

        Chart.Drawable = _chart;
        _vm.ChartInvalidated += RedrawChart;
        RedrawChart();
    }

    /// <summary>Rebuilds the chart's bars and day list from the view model and requests a repaint.</summary>
    void RedrawChart()
    {
        var data = _vm.ChartData;
        _chartDays = data.Select(d => d.day).ToList();
        _chart.Bars = data.Select(d => (d.day.ToString("M/d"), d.count)).ToList();
        Chart.Invalidate();
    }

    /// <summary>Shows a tooltip listing the drops claimed on the day under the pointer, mirroring BarChartDrawable's bar layout.</summary>
    /// <param name="sender">The chart element raising the pointer event.</param>
    /// <param name="e">The pointer event carrying the cursor position.</param>
    void OnChartPointerMoved(object? sender, PointerEventArgs e)
    {
        var pos = e.GetPosition(Chart);
        var n = _chartDays.Count;
        if (pos is not { } p || n == 0 || Chart.Width <= 0)
        {
            ChartTooltip.IsVisible = false;
            return;
        }

        const float gap = 8f;
        var barW = ((float)Chart.Width - gap * (n + 1)) / n;
        var idx = barW > 0 ? (int)((p.X - gap) / (barW + gap)) : -1;
        if (idx < 0 || idx >= n)
        {
            ChartTooltip.IsVisible = false;
            return;
        }

        var day = _chartDays[idx];
        var drops = _vm.DropsOn(day);
        ChartTooltip.Text = drops.Count == 0
            ? $"{day:MMM d}: no drops claimed"
            : $"{day:MMM d} ({drops.Count}): {string.Join(", ", drops)}";
        ChartTooltip.IsVisible = true;
    }

    /// <summary>Hides the chart tooltip when the pointer leaves the chart.</summary>
    /// <param name="sender">The chart element raising the pointer event.</param>
    /// <param name="e">The pointer event arguments.</param>
    void OnChartPointerExited(object? sender, PointerEventArgs e) => ChartTooltip.IsVisible = false;
}
