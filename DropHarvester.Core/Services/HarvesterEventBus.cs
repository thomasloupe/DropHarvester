using DropHarvester.Models.Events;

namespace DropHarvester.Services;

/// <summary>
/// Simple in-process pub/sub for <see cref="HarvesterEvent"/>s. The orchestrator publishes; the UI,
/// notifications, stats and webhook services subscribe. Handlers run on the publishing thread -
/// UI subscribers marshal to the main thread themselves.
/// </summary>
public interface IHarvesterEventBus
{
    event Action<HarvesterEvent>? Event;

    /// <summary>Deliver an event to all current subscribers.</summary>
    /// <param name="e">Event to publish.</param>
    void Publish(HarvesterEvent e);
}

/// <summary>In-process implementation of <see cref="IHarvesterEventBus"/>.</summary>
public sealed class HarvesterEventBus : IHarvesterEventBus
{
    public event Action<HarvesterEvent>? Event;

    /// <summary>Invokes each subscriber in turn, isolating faults so one bad handler can't block the rest.</summary>
    /// <param name="e">Event to publish.</param>
    public void Publish(HarvesterEvent e)
    {
        // A misbehaving subscriber must not break event delivery to the others.
        foreach (var handler in (Event?.GetInvocationList() ?? Array.Empty<Delegate>()).Cast<Action<HarvesterEvent>>())
        {
            try { handler(e); }
            catch { }
        }
    }
}
