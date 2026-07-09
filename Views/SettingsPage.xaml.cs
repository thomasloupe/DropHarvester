using DropHarvester.ViewModels;

namespace DropHarvester.Views;

/// <summary>Code-behind for the Settings tab, including the priority drag-to-reorder gesture.</summary>
public partial class SettingsPage : ContentPage
{
    readonly SettingsViewModel _vm;
    VisualElement? _panView;
    string? _panItem;
    int _panStartIndex = -1;

    /// <summary>Initializes the page and sets its binding context to the settings view model.</summary>
    /// <param name="vm">The settings view model to bind to.</param>
    public SettingsPage(SettingsViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    /// <summary>Refreshes the pending-update state so an already-downloaded update surfaces here without a fresh check.</summary>
    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.RefreshPendingUpdate();
    }

    /// <summary>Scrolls the content pane to the section named by the clicked nav button's CommandParameter.</summary>
    /// <param name="sender">The nav button whose CommandParameter is the target element's x:Name.</param>
    /// <param name="e">The click event arguments.</param>
    async void OnNavClicked(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: string name } && FindByName(name) is Element target)
        {
            try { await ContentScroll.ScrollToAsync(target, ScrollToPosition.Start, true); }
            catch { /* element not laid out yet - ignore */ }
        }
    }

    // Priority drag-to-reorder via PAN (native DnD's Drop never fires in an unpackaged WinUI app -
    // dotnet/maui#26887 - so we drive the reorder from the pan's cumulative Y translation instead).
    // Approximate row height; round() tolerates the imprecision when stepping the item between slots.
    const double PriorityRowHeight = 40;

    /// <summary>Reorders the dragged priority game by the number of row-heights the pan travelled, committing on release.</summary>
    /// <param name="sender">The pan gesture recognizer, or the row view it is attached to.</param>
    /// <param name="e">The pan update carrying gesture status and cumulative translation.</param>
    void OnPriorityPan(object? sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                // sender is either the recognizer (its Parent is the row) or the row view itself - resolve
                // to the actual row Border, NEVER its container, or translating drags the whole list as one.
                var el = sender as Element;
                _panView = (el as VisualElement) ?? (el?.Parent as VisualElement);
                if (_panView?.BindingContext is not string) // grabbed the container, not a row - bail out safely
                    _panView = null;
                _panItem = _panView?.BindingContext as string;
                _panStartIndex = _panItem is null ? -1 : _vm.PriorityGames.IndexOf(_panItem);
                break;
            case GestureStatus.Running:
                // Follow the cursor VISUALLY only - don't reorder yet. Reordering mid-drag relocates the
                // row's view and resets the pan, which is what made it crawl one slot at a time.
                if (_panView is not null)
                    _panView.TranslationY = e.TotalY;
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                if (_panView is not null && _panItem is not null && _panStartIndex >= 0)
                {
                    // Final drag distance -> how many slots to move (can be many at once, so top/bottom is
                    // one drag). Use the view's own TranslationY since e.TotalY may reset on Completed.
                    var moved = (int)Math.Round(_panView.TranslationY / PriorityRowHeight);
                    _panView.TranslationY = 0;
                    _vm.MovePriorityToIndex(_panItem, _panStartIndex + moved);
                    _vm.CommitPriorityOrder();
                }
                _panView = null;
                _panItem = null;
                _panStartIndex = -1;
                break;
        }
    }
}
