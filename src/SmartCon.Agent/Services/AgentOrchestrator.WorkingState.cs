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
    /// Appends the per-conversation session baseline once, before the first turn
    /// context of a conversation, and again when the active document changes.
    /// The stored history is checked by document id, so a restart never creates
    /// a duplicate baseline and a document switch never leaves the old one as
    /// the only session context.
    /// </summary>
    private async Task EnsureSessionContextAsync(
        Guid conversationId,
        AgentProjectContext projectContext,
        CancellationToken ct)
    {
        if (_sessionContext.SessionContextWritten
            && _sessionContext.SessionContextConversationId == conversationId
            && string.Equals(
                _sessionContext.SessionContextDocumentId,
                projectContext.DocumentId,
                StringComparison.Ordinal))
        {
            return;
        }

        var documentMarker = "document_id: " +
                             AgentTextSanitizer.Sanitize(projectContext.DocumentId);
        var history = await _conversationStore
            .GetMessagesAsync(conversationId, ct)
            .ConfigureAwait(false);
        var lastSessionContext = history.LastOrDefault(message =>
            message.Role == AgentRole.System
            && (message.Content ?? string.Empty).StartsWith(
                AgentSystemPrompt.SessionContextHeader, StringComparison.Ordinal));
        var matchesDocument = lastSessionContext is not null
#if NETFRAMEWORK
            && (lastSessionContext.Content ?? string.Empty)
                .IndexOf(documentMarker, StringComparison.Ordinal) >= 0;
#else
            && (lastSessionContext.Content ?? string.Empty)
                .Contains(documentMarker, StringComparison.Ordinal);
#endif

        if (!matchesDocument)
        {
            await _conversationStore
                .AppendMessageAsync(
                    conversationId,
                    AgentMessage.System(AgentSystemPrompt.BuildSessionContext(projectContext)),
                    ct)
                .ConfigureAwait(false);
            SmartConLogger.Info(
                "Agent prompt: session context written for document " +
                AgentTextSanitizer.Sanitize(projectContext.DocumentId));
        }

        _sessionContext.SessionContextConversationId = conversationId;
        _sessionContext.SessionContextDocumentId = projectContext.DocumentId;
        _sessionContext.SessionContextWritten = true;
    }
}
