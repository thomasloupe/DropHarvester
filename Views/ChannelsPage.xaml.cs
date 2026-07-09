using DropHarvester.ViewModels;

namespace DropHarvester.Views;

/// <summary>Code-behind for the Channels tab.</summary>
public partial class ChannelsPage : ContentPage
{
    /// <summary>Initializes the page and sets its binding context to the channels view model.</summary>
    /// <param name="vm">The channels view model to bind to.</param>
    public ChannelsPage(ChannelsViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
