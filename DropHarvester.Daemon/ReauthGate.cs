namespace DropHarvester.Daemon;

/// <summary>
/// One-shot signal that lets the event sink tell the worker the Twitch session expired and harvesting
/// must pause for a fresh device-code login. The worker awaits <see cref="WaitAsync"/>; the sink
/// calls <see cref="Request"/> on a LoginExpired event; the worker <see cref="Reset"/>s before the
/// next cycle.
/// </summary>
public sealed class ReauthGate
{
    volatile TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Signals that re-authentication is required, completing the current wait.</summary>
    public void Request() => _tcs.TrySetResult();

    /// <summary>Arms a fresh signal for the next harvesting cycle.</summary>
    public void Reset() => _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when re-auth is requested; throws <see cref="OperationCanceledException"/>
    /// when the host is shutting down.</summary>
    /// <param name="ct">Token that cancels the wait on host shutdown.</param>
    public async Task WaitAsync(CancellationToken ct)
    {
        var tcs = _tcs;
        await using var reg = ct.Register(static s => ((TaskCompletionSource)s!).TrySetCanceled(), tcs);
        await tcs.Task.ConfigureAwait(false);
    }
}
