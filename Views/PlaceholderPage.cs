namespace DropHarvester.Views;

/// <summary>
/// Temporary themed placeholder used while a tab's real UI is being built out. Each real page
/// (Status, Campaigns, ...) replaces this in its own phase.
/// </summary>
public abstract class PlaceholderPage : ContentPage
{
    /// <summary>Builds the centered title and subtitle placeholder layout using the app theme.</summary>
    /// <param name="title">The heading text, also used as the page title.</param>
    /// <param name="subtitle">The muted subtitle text shown beneath the title.</param>
    protected PlaceholderPage(string title, string subtitle)
    {
        Title = title;
        var res = Application.Current!.Resources;
        BackgroundColor = (Color)res["DhBg"];

        Content = new VerticalStackLayout
        {
            Padding = 24,
            Spacing = 8,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center,
            Children =
            {
                new Label
                {
                    Text = title,
                    Style = (Style)res["H1"],
                    HorizontalTextAlignment = TextAlignment.Center,
                },
                new Label
                {
                    Text = subtitle,
                    Style = (Style)res["Muted"],
                    HorizontalTextAlignment = TextAlignment.Center,
                },
            },
        };
    }
}
