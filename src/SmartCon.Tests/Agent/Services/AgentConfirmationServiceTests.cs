using Xunit;
using SmartCon.Agent.Services;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Tests.Agent.Services;

public sealed class AgentConfirmationServiceTests
{
    [Fact]
    public async Task NoSubscriber_RejectsImmediately()
    {
        var service = new AgentConfirmationService();

        var result = await service.RequestAsync(Request(), CancellationToken.None);

        Assert.Equal(AgentConfirmationResult.Rejected, result);
    }

    [Fact]
    public async Task Approve_CompletesTheRequest()
    {
        var service = new AgentConfirmationService();
        service.Requested += _ => service.Resolve(AgentConfirmationResult.Approved);

        var result = await service.RequestAsync(Request(), CancellationToken.None);

        Assert.Equal(AgentConfirmationResult.Approved, result);
    }

    [Fact]
    public async Task Reject_CompletesTheRequest()
    {
        var service = new AgentConfirmationService();
        service.Requested += _ => service.Resolve(AgentConfirmationResult.Rejected);

        var result = await service.RequestAsync(Request(), CancellationToken.None);

        Assert.Equal(AgentConfirmationResult.Rejected, result);
    }

    [Fact]
    public async Task Cancellation_CompletesWithCancelled()
    {
        var service = new AgentConfirmationService();
        var tcs = new TaskCompletionSource<bool>();
        service.Requested += _ => tcs.TrySetResult(true);

        using var cts = new CancellationTokenSource();
        var pending = service.RequestAsync(Request(), cts.Token);
        await tcs.Task;
        cts.Cancel();

        var result = await pending;
        Assert.Equal(AgentConfirmationResult.Cancelled, result);
    }

    [Fact]
    public async Task Pending_IsClearedAfterCompletion()
    {
        var service = new AgentConfirmationService();
        service.Requested += _ => service.Resolve(AgentConfirmationResult.Approved);

        await service.RequestAsync(Request(), CancellationToken.None);

        Assert.Null(service.Pending);
    }

    private static AgentConfirmationRequest Request() => new()
    {
        ToolName = "rename_views",
        Operation = "rename_views",
        Summary = "test",
        Digest = "abc123",
        ArgumentsJson = "{}",
        Timeout = TimeSpan.FromSeconds(5)
    };
}
