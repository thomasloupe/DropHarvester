namespace DropHarvester.Services;

/// <summary>
/// The desktop app's <see cref="IUiDispatcher"/>: marshals to MAUI's main thread. Registered as the
/// ambient <see cref="UiDispatch.Current"/> at startup so engine code (which only knows the interface)
/// gets true UI-thread marshaling, exactly as before the daemon extraction.
/// </summary>
public sealed class MauiUiDispatcher : IUiDispatcher
{
    public bool IsDispatchRequired => !MainThread.IsMainThread;

    /// <summary>Queues the action to run on the MAUI main thread without waiting for it. Any exception the
    /// action throws is caught and logged to crash.log rather than allowed to escape into the WinUI
    /// dispatcher, which would fail-fast (hard-crash) the whole process.</summary>
    /// <param name="action">The work to run on the UI thread.</param>
    public void Post(Action action) => MainThread.BeginInvokeOnMainThread(() => Guard(action));

    /// <summary>Runs the action on the MAUI main thread and returns a task that completes when it finishes.
    /// The action is guarded the same way as <see cref="Post"/>.</summary>
    /// <param name="action">The work to run on the UI thread.</param>
    /// <returns>A task that completes once the action has run.</returns>
    public Task InvokeAsync(Action action) => MainThread.InvokeOnMainThreadAsync(() => Guard(action));

    /// <summary>Runs a UI action, catching and logging any exception so a throwing callback can't fail-fast
    /// the process from inside the WinUI dispatcher.</summary>
    /// <param name="action">The UI work to run.</param>
    static void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) { App.CrashLog("UiDispatch", ex); }
    }
}
