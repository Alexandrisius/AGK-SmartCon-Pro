using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools;

public abstract class AgentReadToolBase : IAgentTool
{
    /// <summary>
    /// Description of the <c>documentId</c> property, written once for every
    /// routed schema.
    /// </summary>
    internal const string DocumentIdDescription =
        "Background model routing: the documentId returned by open_model_background; " +
        "omit for the active document";

    /// <summary>
    /// The <c>documentId</c> property a routed read tool declares first in the
    /// <c>properties</c> of its schema (separator comma included), with the one
    /// shared description. A routed schema MUST declare it: with
    /// <c>additionalProperties:false</c> an undeclared argument never reaches
    /// the tool, and the read silently hits the active document.
    /// </summary>
    internal const string DocumentIdPropertyJson =
        "\"documentId\":{\"type\":\"string\",\"description\":\"" + DocumentIdDescription + "\"},";

    /// <summary>
    /// The same property as the LAST member of <c>properties</c> (no separator
    /// comma). A comma left before the closing brace is invalid JSON and takes
    /// the whole tool catalog down with it (night run 2 R0).
    /// </summary>
    internal const string DocumentIdPropertyJsonLast =
        "\"documentId\":{\"type\":\"string\",\"description\":\"" + DocumentIdDescription + "\"}";

    /// <summary>Key of the routing fact in the payload, see <see cref="WithRoutingFact"/>.</summary>
    internal const string RoutingFactKey = "document";

    internal const string ScopeActive = "active";

    internal const string ScopeBackground = "background";

    private readonly IRevitContext _revitContext;

    protected AgentReadToolBase(IRevitContext revitContext)
    {
        _revitContext = revitContext ?? throw new ArgumentNullException(nameof(revitContext));
    }

    /// <summary>
    /// True when the tool reads the document resolved by this class and can be
    /// aimed at a background model (open_model_background) through the optional
    /// <c>documentId</c> argument; such a tool declares
    /// <see cref="DocumentIdPropertyJson"/> in its schema (guarded by the
    /// routing coverage test). False for session/UI tools whose subject is not
    /// "the document as data" - the user's selection, the active view, the
    /// change-set history, the session's shared parameter file: they never
    /// route, never get the background-model reminder and their schemas must
    /// NOT declare documentId (the call then fails as invalid_arguments
    /// instead of silently reading another document).
    /// </summary>
    public virtual bool SupportsDocumentRouting => true;

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
    /// Default: the active document; when a routing-capable call carries an
    /// optional <c>documentId</c> - the background document registered by
    /// open_model_background. The session document's own id routes back to the
    /// active document (normal path, session guard included). Returns null when
    /// routing fails; the base class then shapes the typed error
    /// (<c>model_not_open</c> for background misses, no_active_document
    /// otherwise). Override to change resolution for a specific tool.
    /// </summary>
    protected virtual Document? ResolveDocument(Document? activeDocument, JsonElement args)
    {
        var routedId = RequestedDocumentId(args);
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
        var requestedId = RequestedDocumentId(args);
        var routedToBackground = IsBackgroundRouting(activeDocument, requestedId);
        var document = ResolveDocument(activeDocument, args);

        if (document is null)
        {
            if (routedToBackground)
            {
                return ToolResult.Fail(
                    "model_not_open",
                    "No background model is open with documentId '" + requestedId + "'. " +
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

            if (SupportsDocumentRouting)
            {
                result = WithRoutingFact(
                    result, document, routedToBackground, requestedId is not null);
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

    /// <summary>
    /// The documentId of this call; null when the tool does not route (its
    /// schema declares no documentId, so a stray value is never honoured).
    /// </summary>
    private string? RequestedDocumentId(JsonElement args) =>
        SupportsDocumentRouting ? AgentJson.GetString(args, "documentId") : null;

    /// <summary>
    /// States the routing FACT in the answer (night run 2, C9-02): the payload
    /// carries <c>document: {scope, documentId}</c> of the document that was
    /// really read, and a successful call WITHOUT documentId repeats in its
    /// message that the active document was read while a background model is
    /// open. Only while a background model is open or the call carried a
    /// documentId - the plain single-document mode adds nothing.
    /// </summary>
    private ToolResult WithRoutingFact(
        ToolResult result,
        Document document,
        bool routedToBackground,
        bool documentIdRequested)
    {
        var open = AgentBackgroundDocuments.ListAll();
        if (open.Count == 0 && !documentIdRequested)
            return result;

        var suffix = string.Empty;
        if (!documentIdRequested && open.Count > 0 && result.Success)
        {
            suffix = open.Count == 1
                ? " NOTE: documentId was not present in this call, so the ACTIVE document " +
                  "was read; to read the background model (" +
                  System.IO.Path.GetFileNameWithoutExtension(open[0].PathName ?? "?") +
                  ") repeat with documentId='" + open[0].DocumentId + "'."
                : " NOTE: documentId was not present in this call, so the ACTIVE document " +
                  "was read. " + open.Count + " background models are open: " +
                  string.Join("; ", open.Select(pair =>
                      System.IO.Path.GetFileNameWithoutExtension(pair.PathName ?? "?") +
                      " -> documentId='" + pair.DocumentId + "'")) +
                  ". Pass one to read THAT model.";
        }

        JsonElement? merged = null;
        if (result.Data is JsonElement payload && payload.ValueKind == JsonValueKind.Object
            && AgentJson.TryAddProperty(
                payload,
                RoutingFactKey,
                new
                {
                    scope = routedToBackground ? ScopeBackground : ScopeActive,
                    documentId = AgentDocumentIdentity.GetDocumentId(document)
                },
                out var added))
        {
            merged = added;
        }

        if (suffix.Length == 0 && merged is null)
            return result;

        return new ToolResult
        {
            Success = result.Success,
            Data = merged ?? result.Data,
            Message = result.Message + suffix,
            Warnings = result.Warnings,
            Skills = result.Skills,
            ErrorCode = result.ErrorCode,
            AffectedCount = result.AffectedCount,
            DocumentVersion = result.DocumentVersion,
            Repairs = result.Repairs
        };
    }

    private static bool IsBackgroundRouting(Document? activeDocument, string? routedId)
    {
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
