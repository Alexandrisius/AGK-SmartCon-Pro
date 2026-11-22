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

    /// <summary>
    /// Resolves the document this call reads (case 9 v2 multi-model routing).
    /// Default: the active document; when args carry an optional
    /// <c>documentId</c> — the background document registered by
    /// open_model_background. The session document's own id routes back to the
    /// active document (normal path, session guard included). Returns null when
    /// routing fails; the base class then shapes the typed error
    /// (<c>model_not_open</c> for background misses, no_active_document
    /// otherwise). Override to change resolution for a specific tool.
    /// </summary>
    protected virtual Document? ResolveDocument(Document? activeDocument, JsonElement args)
    {
        var routedId = AgentJson.GetString(args, "documentId");
        if (string.IsNullOrEmpty(routedId))
            return activeDocument;

        if (AgentBackgroundDocuments.TryResolve(routedId!, out var backgroundDocument))
            return backgroundDocument;

        if (activeDocument is not null
            && string.Equals(
                AgentDocumentIdentity.GetDocumentId(activeDocument),
                routedId,
                StringComparison.Ordinal))
        {
            return activeDocument;
        }

        return null;
    }

    private ToolResult ExecuteOnRevitThread(
        string expectedDocumentId,
        JsonElement args,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var activeDocument = _revitContext.TryGetDocument();
        var routedToBackground = IsBackgroundRouting(activeDocument, args);
        var document = ResolveDocument(activeDocument, args);

        if (document is null)
        {
            if (routedToBackground)
            {
                var routedId = AgentJson.GetString(args, "documentId");
                return ToolResult.Fail(
                    "model_not_open",
                    "No background model is open with documentId '" + routedId + "'. " +
                    "Call open_model_background(path=...) first and pass the documentId " +
                    "it returns.");
            }

            return ToolResult.Fail(
                "no_active_document",
                "No Revit project document is open. Open a project and try again.");
        }

        // A background donor never changes the user's session, so the
        // document_changed guard applies to the active document only.
        if (!routedToBackground
            && !string.IsNullOrEmpty(expectedDocumentId)
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

            // In-band reminder (live case 9 v2 #466): while a background model is
            // open, every successful read of the ACTIVE document repeats how to
            // query the donor - models otherwise keep forgetting documentId.
            if (result.Success && !routedToBackground)
            {
                var open = AgentBackgroundDocuments.ListAll();
                if (open.Count > 0)
                {
                    var first = open[0];
                    var reminder = open.Count == 1
                        ? " NOTE: this is the ACTIVE document. A background model is open (" +
                          System.IO.Path.GetFileNameWithoutExtension(first.PathName ?? "?") +
                          "). To read IT instead, repeat this call with documentId='" +
                          first.DocumentId + "'."
                        : " " + open.Count + " background models open: " +
                          string.Join("; ", open.Select(pair =>
                              System.IO.Path.GetFileNameWithoutExtension(pair.PathName ?? "?") +
                              " -> documentId='" + pair.DocumentId + "'")) +
                          ". Pass one to read THAT model.";
                    result = result.WithMessageSuffix(reminder);
                }
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

    private static bool IsBackgroundRouting(Document? activeDocument, JsonElement args)
    {
        var routedId = AgentJson.GetString(args, "documentId");
        if (string.IsNullOrEmpty(routedId))
            return false;

        if (activeDocument is null)
            return true;

        // The session document's own id is not background routing: it keeps
        // the normal active-document path with the document_changed guard.
        return !string.Equals(
            AgentDocumentIdentity.GetDocumentId(activeDocument),
            routedId,
            StringComparison.Ordinal);
    }
}