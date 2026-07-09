using DropHarvester.ViewModels;

namespace DropHarvester.Views;

/// <summary>Code-behind for the Campaigns tab.</summary>
public partial class CampaignsPage : ContentPage
{
    readonly CampaignsViewModel _vm;

    /// <summary>Initializes the page and sets its binding context to the campaigns view model.</summary>
    /// <param name="vm">The campaigns view model to bind to.</param>
    public CampaignsPage(CampaignsViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    /// <summary>Ensures campaign data is loaded each time the page appears.</summary>
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.EnsureLoadedAsync();
    }
}
