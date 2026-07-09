using CommunityToolkit.Mvvm.ComponentModel;

namespace DropHarvester.ViewModels;

/// <summary>
/// Base for view-models whose observable properties may be updated from a background thread (harvester
/// events published on the harvesting loop, or async continuations that resumed off the UI thread).
/// WinUI hard-crashes (combase E_INVALIDARG / CoreMessagingXP failfast) if a bound property's change
/// notification is raised off the UI thread - so, exactly like <see cref="Models.ObservableModel"/>
/// does for domain models, we marshal the notification to the main thread. The field is still set
/// synchronously; only the notification is dispatched, and on the UI thread (the normal case) it's
/// raised inline with no behavior change. Off-thread notifications are logged (capped) so the exact
/// offending property is visible in crash.log for diagnosis.
/// </summary>
public abstract class ObservableViewModel : ObservableObject
{
    /// <summary>Raises the change notification inline on the UI thread, or marshals it there (logging) when off-thread.</summary>
    /// <param name="e">The property-changed arguments to raise.</param>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (MainThread.IsMainThread)
        {
            RaiseChanged(e);
            return;
        }
        App.LogOffThreadNotification($"{GetType().Name}.{e.PropertyName}");
        MainThread.BeginInvokeOnMainThread(() => RaiseChanged(e));
    }

    /// <summary>Raises the changing notification inline on the UI thread, or marshals it there when off-thread.</summary>
    /// <param name="e">The property-changing arguments to raise.</param>
    protected override void OnPropertyChanging(System.ComponentModel.PropertyChangingEventArgs e)
    {
        if (MainThread.IsMainThread)
        {
            RaiseChanging(e);
            return;
        }
        MainThread.BeginInvokeOnMainThread(() => RaiseChanging(e));
    }

    /// <summary>Raises PropertyChanged, catching and logging any exception a WinUI binding update throws
    /// so a bad bound value (e.g. E_INVALIDARG on a control) logs to crash.log instead of fail-fasting the
    /// whole process from inside the UI dispatcher.</summary>
    /// <param name="e">The property-changed arguments to raise.</param>
    void RaiseChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        try { base.OnPropertyChanged(e); }
        catch (Exception ex) { App.CrashLog($"OnPropertyChanged {GetType().Name}.{e.PropertyName}", ex); }
    }

    /// <summary>Raises PropertyChanging, catching and logging any WinUI binding exception (see <see cref="RaiseChanged"/>).</summary>
    /// <param name="e">The property-changing arguments to raise.</param>
    void RaiseChanging(System.ComponentModel.PropertyChangingEventArgs e)
    {
        try { base.OnPropertyChanging(e); }
        catch (Exception ex) { App.CrashLog($"OnPropertyChanging {GetType().Name}.{e.PropertyName}", ex); }
    }
}
