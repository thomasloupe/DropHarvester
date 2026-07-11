using DropHarvester.ViewModels;

namespace DropHarvester.Views;

/// <summary>Code-behind for the Log tab.</summary>
public partial class LogPage : ContentPage
{
    /// <summary>Initializes the page and sets its binding context to the log view model.</summary>
    /// <param name="vm">The log view model to bind to.</param>
    public LogPage(LogViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
        // Follow the newest line: whenever the stacked lines grow (a line was appended), scroll to the
        // bottom. This replaces the CollectionView's KeepLastItemInView without its whole-list re-render.
        LogStack.SizeChanged += (_, _) => _ = FollowTailAsync();
    }

    /// <summary>Scrolls the log to the bottom so the latest line stays visible.</summary>
    async Task FollowTailAsync()
    {
        try { await LogScroll.ScrollToAsync(0, LogScroll.ContentSize.Height, animated: false); }
        catch { /* view not laid out yet; the next size change re-tries */ }
    }
}
