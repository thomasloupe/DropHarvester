using DropHarvester.ViewModels;

namespace DropHarvester.Views;

/// <summary>Code-behind for the "What's changed" popup: loads the changelog when shown and closes the
/// modal on the X button or a tap on the dimmed backdrop.</summary>
public partial class ChangelogPage : ContentPage
{
    readonly ChangelogViewModel _vm;

    /// <summary>Initializes the popup and binds the changelog view model.</summary>
    /// <param name="vm">The changelog view model to bind and load.</param>
    public ChangelogPage(ChangelogViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    /// <summary>Loads the changelog each time the popup is shown.</summary>
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.LoadAsync();
    }

    /// <summary>Closes the popup when the X button is clicked.</summary>
    /// <param name="sender">The close button.</param>
    /// <param name="e">The click event args.</param>
    async void OnCloseClicked(object? sender, EventArgs e) => await CloseAsync();

    /// <summary>Closes the popup when the dimmed backdrop is tapped.</summary>
    /// <param name="sender">The backdrop element.</param>
    /// <param name="e">The tap event args.</param>
    async void OnBackdropTapped(object? sender, TappedEventArgs e) => await CloseAsync();

    /// <summary>Pops this modal popup off the navigation stack.</summary>
    async Task CloseAsync()
    {
        var nav = Navigation ?? Shell.Current?.Navigation;
        if (nav is not null && nav.ModalStack.Count > 0)
            await nav.PopModalAsync();
    }
}
