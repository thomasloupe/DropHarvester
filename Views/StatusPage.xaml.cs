using DropHarvester.ViewModels;

namespace DropHarvester.Views;

/// <summary>Code-behind for the Status tab.</summary>
public partial class StatusPage : ContentPage
{
    readonly StatusViewModel _vm;

    /// <summary>Initializes the page and sets its binding context to the status view model.</summary>
    /// <param name="vm">The status view model to bind to.</param>
    public StatusPage(StatusViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    /// <summary>Refreshes the pending-update state so an update downloaded elsewhere surfaces here.</summary>
    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.RefreshPendingUpdate();
    }
}
