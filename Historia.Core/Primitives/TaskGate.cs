namespace Historia.Primitives;

/// <summary>
/// Helper for awaiting shared, coalesced tasks with per-caller cancellation tokens.
/// </summary>
internal static class TaskGate
{
    public static async Task WaitWithCancellationAsync(Task task, CancellationToken ct)
    {
        if (!ct.CanBeCanceled)
        {
            await task.ConfigureAwait(false);
            return;
        }

        try
        {
            await task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // If this caller was cancelled before the shared coalesced task completed,
            // ensure any later fault on the shared task is observed so it does not trigger
            // an UnobservedTaskException if no other caller is awaiting it.
            _ = task.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
    }
}
