namespace Historia.Tests;

[Trait("tier", "unit")]
public class TaskGateTests
{
    [Fact]
    public async Task Uncancellable_token_awaits_the_task()
    {
        var task = Task.FromResult(42);
        await TaskGate.WaitWithCancellationAsync(task, CancellationToken.None);
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Cancellation_before_completion_throws_OperationCanceled()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TaskGate.WaitWithCancellationAsync(tcs.Task, cts.Token));
        Assert.False(tcs.Task.IsCompleted);

        tcs.SetResult();
        await tcs.Task;
    }

    [Fact]
    public async Task Later_fault_on_a_cancelled_shared_task_is_observed()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TaskGate.WaitWithCancellationAsync(tcs.Task, cts.Token));

        var expectedEx = new InvalidOperationException("boom");
        tcs.SetException(expectedEx);

        // Allow any synchronous/asynchronous faulted continuation attached to the task to run
        await Task.Yield();

        Assert.True(tcs.Task.IsFaulted);
        Assert.Same(expectedEx, tcs.Task.Exception?.InnerException);
    }
}
