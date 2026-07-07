using SmartCon.Core.Threading;
using Xunit;

namespace SmartCon.Tests.Core.Threading;

public sealed class AsyncBridgeTests
{
    [Fact]
    public void RunSync_Generic_ReturnsValue()
    {
        var result = AsyncBridge.RunSync(() => Task.FromResult(42));
        Assert.Equal(42, result);
    }

    [Fact]
    public void RunSync_NonGeneric_Completes()
    {
        var ran = false;
        AsyncBridge.RunSync(() => Task.Run(() => ran = true));
        Assert.True(ran);
    }

    [Fact]
    public void RunSync_AsyncTask_ExecutesOnDifferentThread()
    {
        var callerThread = Environment.CurrentManagedThreadId;
        int? taskThread = null;

        var result = AsyncBridge.RunSync(() => Task.Run(async () =>
        {
            await Task.Yield();
            taskThread = Environment.CurrentManagedThreadId;
            return taskThread;
        }));

        Assert.NotNull(result);
        Assert.NotEqual(callerThread, result.Value);
    }

    [Fact]
    public void RunSync_PropagatesException()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AsyncBridge.RunSync(() => Task.FromException<int>(new InvalidOperationException("boom"))));

        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public void RunSync_NullTaskFactory_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AsyncBridge.RunSync<int>(null!));
        Assert.Throws<ArgumentNullException>(() => AsyncBridge.RunSync((Func<Task>)null!));
    }
}
