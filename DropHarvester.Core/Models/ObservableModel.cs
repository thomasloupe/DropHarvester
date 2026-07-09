using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DropHarvester.Models;

/// <summary>
/// Base for domain models that are bound to the UI but mutated from background threads (the harvesting
/// loop). WinUI hard-crashes (combase E_INVALIDARG) if a bound property's change notification is
/// raised off the UI thread, so we marshal PropertyChanged/PropertyChanging to the main thread.
/// The field value itself is still set synchronously; only the notification is dispatched.
/// </summary>
public abstract class ObservableModel : ObservableObject
{
    /// <summary>Raise PropertyChanged, marshaling the notification to the UI thread when called off it.</summary>
    /// <param name="e">The property-changed arguments to raise.</param>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!UiDispatch.Current.IsDispatchRequired)
            base.OnPropertyChanged(e);
        else
            UiDispatch.Current.Post(() => base.OnPropertyChanged(e));
    }

    /// <summary>Raise PropertyChanging, marshaling the notification to the UI thread when called off it.</summary>
    /// <param name="e">The property-changing arguments to raise.</param>
    protected override void OnPropertyChanging(System.ComponentModel.PropertyChangingEventArgs e)
    {
        if (!UiDispatch.Current.IsDispatchRequired)
            base.OnPropertyChanging(e);
        else
            UiDispatch.Current.Post(() => base.OnPropertyChanging(e));
    }
}
