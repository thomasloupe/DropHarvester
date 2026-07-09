namespace DropHarvester;

/// <summary>
/// Abstraction over "run this on the UI thread". The desktop (MAUI) app needs UI-bound property and
/// collection changes marshaled to the main thread (WinUI hard-crashes otherwise); a headless host
/// (the Docker daemon) has no UI thread, so everything runs inline. Engine code depends only on this
/// interface, never on MAUI's <c>MainThread</c>, which is what lets the engine live in a MAUI-free
/// class library.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>True when the caller is NOT on the UI thread and must marshal via <see cref="Post"/>
    /// / <see cref="InvokeAsync"/>. Always false headless (there is no UI thread to marshal to).</summary>
    bool IsDispatchRequired { get; }

    /// <summary>Queue an action to run on the UI thread (fire-and-forget, FIFO).</summary>
    /// <param name="action">The action to queue.</param>
    void Post(Action action);

    /// <summary>Run an action on the UI thread and await its completion.</summary>
    /// <param name="action">The action to run.</param>
    /// <returns>A task that completes once the action has run.</returns>
    Task InvokeAsync(Action action);
}

/// <summary>Headless dispatcher: no UI thread, so everything runs inline on the calling thread.</summary>
public sealed class InlineUiDispatcher : IUiDispatcher
{
    public bool IsDispatchRequired => false;
    /// <summary>Run the action inline on the calling thread.</summary>
    /// <param name="action">The action to run.</param>
    public void Post(Action action) => action();
    /// <summary>Run the action inline and return an already-completed task.</summary>
    /// <param name="action">The action to run.</param>
    /// <returns>An already-completed task.</returns>
    public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
}

/// <summary>
/// Ambient UI dispatcher. Defaults to <see cref="InlineUiDispatcher"/> (headless-safe); the MAUI app
/// swaps in a MainThread-backed dispatcher at startup. Static ambient state (rather than DI) so the
/// low-level <c>ObservableModel</c> / <c>UiObservableCollection</c> base types can reach it without
/// every model taking a constructor dependency.
/// </summary>
public static class UiDispatch
{
    public static IUiDispatcher Current { get; set; } = new InlineUiDispatcher();

    /// <summary>Optional diagnostic hook fired when an off-UI-thread collection mutation is observed.
    /// The MAUI app wires this to its crash logger; headless leaves it null.</summary>
    public static Action<string>? OffThreadObserved { get; set; }
}
