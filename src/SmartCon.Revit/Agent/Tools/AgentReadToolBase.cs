using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools;

public abstract class AgentReadToolBase : IAgentTool
{
    private readonly IRevitContext _revitContext;

    protected AgentReadToolBase(IRevitContext revitContext)
    {
        _revitContext = revitContext ?? throw new ArgumentNullException(nameof(revitContext));
    }

    public abstract AgentToolDefinition Definition { get; }

    public Task<ToolResult> ExecuteAsync(
        AgentToolContext context,
        JsonElement args,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var expectedDocumentId = context.DocumentId;
        return context.Revit.RaiseAsync(
            _ => ExecuteOnRevitThread(expectedDocumentId, args, ct),
            ct);
    }

    protected abstract ToolResult Execute(
        Document doc,
        JsonElement args,
        CancellationToken ct);

    internal ToolResult ExecuteForTests(Document doc, JsonElement args, CancellationToken ct) =>
        Execute(doc, args, ct);

    private ToolResult ExecuteOnRevitThread(
        string expectedDocumentId,
        JsonElement args,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var document = _revitContext.TryGetDocument();
        if (document is null)
        {
            return ToolResult.Fail(
                "no_active_document",
                "No Revit project document is open. Open a project and try again.");
        }

        if (!string.IsNullOrEmpty(expectedDocumentId)
            && !string.Equals(
                AgentDocumentIdentity.GetDocumentId(document),
                expectedDocumentId,
                StringComparison.Ordinal))
        {
            return ToolResult.Fail(
                "document_changed",
                "The active document changed while the request was running. " +
                "Start a new request in the current document.");
        }

        try
        {
            var version = AgentDocumentState.Capture(document);
            var result = Execute(document, args, ct);
            if (!result.Success
                && result.ErrorCode is not null
                && result.ErrorCode.EndsWith("_not_found", StringComparison.Ordinal))
            {
                result = result.WithMessageSuffix(AgentDocumentState.NotFoundHint(version));
            }

            return result.WithDocumentVersion(version);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SmartConLogger.Error(
                "Agent tool '" + Definition.Name + "' failed: " +
                ex.GetType().Name + ": " + ex.Message);
            return ToolResult.Fail(
                "tool_failed",
                ex.GetType().Name + ": " + ex.Message);
        }
    }
}