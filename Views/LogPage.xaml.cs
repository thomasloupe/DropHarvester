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
    }
}
