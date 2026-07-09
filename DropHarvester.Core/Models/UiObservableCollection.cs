using System.Collections.ObjectModel;

namespace DropHarvester.Models;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that is safe to mutate from any thread. WinUI hard-crashes
/// (combase E_INVALIDARG / CoreMessagingXP failfast) when a bound collection raises CollectionChanged
/// off the UI thread - and unlike a property notification (handled by <see cref="ObservableModel"/> /
/// ViewModels' ObservableViewModel), that can't be fixed by deferring just the event, because the
/// collection's contents change synchronously. So every mutation is marshaled, in order, to the UI
/// thread. On-thread mutations run inline (no behavior change); off-thread ones are dispatched (FIFO,
/// so order is preserved) and logged once with a stack trace so the offending call site is pinpointable
/// in crash.log.
/// </summary>
public sealed class UiObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>Insert an item, marshaling the mutation to the UI thread when needed.</summary>
    /// <param name="index">Zero-based position to insert at.</param>
    /// <param name="item">The item to insert.</param>
    protected override void InsertItem(int index, T item) => Run(() => base.InsertItem(index, item));
    /// <summary>Remove the item at an index, marshaling the mutation to the UI thread when needed.</summary>
    /// <param name="index">Zero-based position of the item to remove.</param>
    protected override void RemoveItem(int index) => Run(() => base.RemoveItem(index));
    /// <summary>Replace the item at an index, marshaling the mutation to the UI thread when needed.</summary>
    /// <param name="index">Zero-based position to replace.</param>
    /// <param name="item">The replacement item.</param>
    protected override void SetItem(int index, T item) => Run(() => base.SetItem(index, item));
    /// <summary>Move an item between positions, marshaling the mutation to the UI thread when needed.</summary>
    /// <param name="oldIndex">Current index of the item.</param>
    /// <param name="newIndex">Destination index.</param>
    protected override void MoveItem(int oldIndex, int newIndex) => Run(() => base.MoveItem(oldIndex, newIndex));
    /// <summary>Clear all items, marshaling the mutation to the UI thread when needed.</summary>
    protected override void ClearItems() => Run(() => base.ClearItems());

    /// <summary>Run a mutation inline on the UI thread, otherwise log the off-thread call and dispatch it (FIFO).</summary>
    /// <param name="mutate">The collection mutation to perform.</param>
    static void Run(Action mutate)
    {
        if (!UiDispatch.Current.IsDispatchRequired)
        {
            mutate();
            return;
        }
        UiDispatch.OffThreadObserved?.Invoke($"UiObservableCollection<{typeof(T).Name}> mutated off the UI thread");
        UiDispatch.Current.Post(mutate);
    }
}
