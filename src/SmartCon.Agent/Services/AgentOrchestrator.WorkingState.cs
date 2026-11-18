using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;

namespace SmartCon.Agent.Services;

public sealed partial class AgentOrchestrator
{
    private async Task<WorkingState> PrepareWorkingStateAsync(
        Guid conversationId,
        string userText,
        AgentProjectContext projectContext,
        CancellationToken ct)
    {
        var state = _sessionContext.WorkingState;
        if (state is null && _workingStateStore is not null)
        {
            try
            {
                state = await _workingStateStore
                    .LoadAsync(conversationId, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    "Agent working state load failed: " + ex.GetType().Name + ": " + ex.Message +
                    " [Action: check read access to agent.db; the turn starts with an empty " +
                    "working state]");
            }
        }

        state ??= new WorkingState();
        AgentWorkingStateUpdater.UpdateFromUserText(state, userText);
        AgentWorkingStateUpdater.UpdateFromProjectContext(state, projectContext);
        _sessionContext.WorkingState = state;
        return state;
    }

    private async Task SaveWorkingStateAsync(Guid conversationId, WorkingState state)
    {
        try
        {
            await _workingStateStore!
                .SaveAsync(conversationId, state, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                "Agent working state save failed: " + ex.GetType().Name + ": " + ex.Message +
                " [Action: check write access to agent.db; the turn itself is not affected]");
        }
    }

    /// <summary>
    /// Prepends the rendered turn context to the last user message of the
    /// outgoing payload only. The message stored in the conversation history
    /// stays exactly as the user sent it.
    /// </summary>
    private static void ApplyTurnContext(
        List<AgentMessage> messages,
        WorkingState state,
        AgentProjectContext projectContext)
    {
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var message = messages[index];
            if (message.Role != AgentRole.User)
                continue;

            var turnContext = AgentTurnContextBuilder.Build(state, projectContext);
            messages[index] = AgentMessage.User(
                turnContext + "\n\n" + (message.Content ?? string.Empty),
                message.Attachments);
            return;
        }
    }
}
