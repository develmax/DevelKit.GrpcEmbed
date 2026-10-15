using System.Threading.Tasks.Sources;
using GrpcEmbed.AspNetCore;
using Xunit;

namespace GrpcEmbed.Tests;

public class ActionAwaiterTests
{
    [Fact]
    public async Task Supported_return_shapes_preserve_results_and_empty_values()
    {
        var value = new object();
        Assert.Same(value, await ActionDelegates.CompileAwaiter(typeof(object))(value));
        Assert.Same(value, await ActionDelegates.CompileAwaiter(typeof(Task<object>))(Task.FromResult(value)));
        Assert.Same(value, await ActionDelegates.CompileAwaiter(typeof(ValueTask<object>))(new ValueTask<object>(value)));
        Assert.Equal(42, await ActionDelegates.CompileAwaiter(typeof(Task<int>))(Task.FromResult(42)));
        Assert.Null(await ActionDelegates.CompileAwaiter(typeof(Task))(Task.FromResult(value)));
        Assert.Null(await ActionDelegates.CompileAwaiter(typeof(ValueTask))(ValueTask.CompletedTask));
        Assert.Null(await ActionDelegates.CompileAwaiter(typeof(void))(null));
        Assert.Null(await ActionDelegates.CompileAwaiter(typeof(Task<object>))(null));
    }

    [Fact]
    public async Task Pending_faulted_and_cancelled_tasks_preserve_original_outcomes()
    {
        var awaiter = ActionDelegates.CompileAwaiter(typeof(Task<object>));
        var source = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = awaiter(source.Task);
        Assert.False(pending.IsCompleted);
        var value = new object();
        source.SetResult(value);
        Assert.Same(value, await pending);
        var failure = new InvalidOperationException("synthetic");
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await awaiter(Task.FromException<object>(failure))));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await awaiter(Task.FromCanceled<object>(cancellation.Token)));
    }

    [Fact]
    public async Task ValueTask_source_is_consumed_once_without_AsTask_conversion()
    {
        var source = new Source();
        var awaiter = ActionDelegates.CompileAwaiter(typeof(ValueTask<object>));
        var pending = awaiter(new ValueTask<object>(source, source.Version));
        Assert.False(pending.IsCompleted);
        var value = new object();
        source.Complete(value);
        Assert.Same(value, await pending);
        Assert.Equal(1, source.Reads);
        var failure = new InvalidOperationException("synthetic");
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await awaiter(new ValueTask<object>(Task.FromException<object>(failure)))));
    }

    [Fact]
    public async Task Completed_reference_task_does_not_allocate_a_result_task_per_call()
    {
        var awaiter = ActionDelegates.CompileAwaiter(typeof(Task<object>));
        var expected = new object();
        var task = Task.FromResult(expected);
        for (var i = 0; i < 1000; i++) _ = await awaiter(task);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++)
        {
            var pending = awaiter(task);
            if (!pending.IsCompletedSuccessfully || !ReferenceEquals(expected, await pending))
                throw new InvalidOperationException("Incorrect completed task result.");
        }
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1024);
    }

    private sealed class Source : IValueTaskSource<object>
    {
        private ManualResetValueTaskSourceCore<object> _core;
        internal short Version => _core.Version;
        internal int Reads;
        internal void Complete(object value) => _core.SetResult(value);
        public object GetResult(short token) { Reads++; return _core.GetResult(token); }
        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _core.OnCompleted(continuation, state, token, flags);
    }
}
