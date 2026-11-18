using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Interfaces.Agent;

public interface IAgentWorkingStateStore
{
    Task<WorkingState?> LoadAsync(Guid conversationId, CancellationToken ct);

    Task SaveAsync(Guid conversationId, WorkingState state, CancellationToken ct);
}
