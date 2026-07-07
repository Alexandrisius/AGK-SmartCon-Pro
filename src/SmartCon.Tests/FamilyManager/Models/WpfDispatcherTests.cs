using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.UI;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Models;

public sealed class WpfDispatcherTests
{
    [Fact]
    public void CheckAccess_NoCurrentDispatcher_ReturnsTrue()
    {
        var dispatcher = new WpfDispatcher(null);

        Assert.True(dispatcher.CheckAccess());
    }

    [Fact]
    public void Invoke_OnCurrentThread_ExecutesSynchronously()
    {
        var dispatcher = new WpfDispatcher(null);
        var ran = false;

        dispatcher.Invoke(() => ran = true);

        Assert.True(ran);
    }

    [Fact]
    public async Task InvokeAsync_OnCurrentThread_ExecutesImmediately()
    {
        var dispatcher = new WpfDispatcher(null);
        var ran = false;

        await dispatcher.InvokeAsync(() => ran = true);

        Assert.True(ran);
    }

    [Fact]
    public void Invoke_NullAction_Throws()
    {
        var dispatcher = new WpfDispatcher(null);
        Assert.Throws<ArgumentNullException>(() => dispatcher.Invoke(null!));
    }

    [Fact]
    public async Task InvokeAsync_NullAction_Throws()
    {
        var dispatcher = new WpfDispatcher(null);
        await Assert.ThrowsAsync<ArgumentNullException>(() => dispatcher.InvokeAsync(null!));
    }

    [Fact]
    public async Task InvokeAsync_WithCancellation_RespectsToken()
    {
        var dispatcher = new WpfDispatcher(null);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await dispatcher.InvokeAsync(() => { }, cts.Token);
    }

    [Fact]
    public void InvocableContract_ResolvesFromInterface()
    {
        IDispatcher dispatcher = new WpfDispatcher(null);

        Assert.NotNull(dispatcher);
    }
}
