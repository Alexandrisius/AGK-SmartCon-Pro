using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Tools;

namespace SmartCon.Revit.Agent.Classification;

/// <summary>
/// Proposes a classification code for every selected element type from the
/// table loaded in the document - the equivalent of a designer opening the
/// «Код по классификатору» picker for each type. Read-only: it returns the
/// proposals, the uncertain ones and a READY set_parameters argument; the write
/// itself goes through the normal gated path. The snapshot is copied off the
/// Revit thread in one hop, the semantic matching awaits off it (I-05).
/// </summary>
public sealed partial class ClassifyTypesTool : IAgentTool
{
    public const string ToolName = "classify_types";

    private const string DecisionUnavailableHint =
        "The decision model is not available, so codes cannot be proposed automatically. " +
        "Tell the user the small decision model is not configured; the codes can still be " +
        "picked manually - list_classification_entries shows the table, set_parameters " +
        "writes the chosen code";

    private const string TableNotLoadedMessage =
        "No classification table is loaded in this document, so there are no codes to read. " +
        "Tell the user that the classifier is not loaded (Revit: Manage > Classification " +
        "Settings; older versions: Manage > Additional Settings > Assembly Code). If the " +
        "user names a classifier file, load it with load_classification_table(path); never " +
        "pick codes from memory or by analogy.";

    private readonly IRevitContext _revitContext;
    private readonly AgentClassificationMatcher _matcher;

    public ClassifyTypesTool(IRevitContext revitContext, AgentSemanticRanker ranker)
    {
        _revitContext = revitContext ?? throw new ArgumentNullException(nameof(revitContext));
        _matcher = new AgentClassificationMatcher(
            ranker ?? throw new ArgumentNullException(nameof(ranker)));
        Definition = BuildDefinition();
    }

    public AgentToolDefinition Definition { get; }

    public async Task<ToolResult> ExecuteAsync(
        AgentToolContext context,
        JsonElement args,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var expectedDocumentId = context.DocumentId;
        var snapshot = await context.Revit
            .RaiseAsync(_ => CollectOnRevitThread(expectedDocumentId, args, ct), ct)
            .ConfigureAwait(false);
        return await RunAsync(snapshot, ct).ConfigureAwait(false);
    }

    /// <summary>Test seam: the same pipeline without marshaling.</summary>
    internal async Task<ToolResult> ExecuteForTestsAsync(
        Document doc,
        JsonElement args,
        CancellationToken ct)
    {
        var snapshot = Collect(doc, args, ct);
        return await RunAsync(snapshot, ct).ConfigureAwait(false);
    }

    private async Task<ToolResult> RunAsync(ClassifySnapshot snapshot, CancellationToken ct)
    {
        if (snapshot.Failure is not null)
            return snapshot.Failure;

        var options = new ClassifyOptions(
            snapshot.Args.MinScore,
            snapshot.Args.CandidateDepth,
            snapshot.Args.Instructions);

        // The family budget is applied before the matching: a deterministic
        // prefix keeps the cost bounded and the payload honest.
        var groups = AgentClassificationTexts.GroupTypes(snapshot.Types).ToList();
        var truncated = snapshot.Truncated;
        var familiesSkipped = 0;
        if (groups.Count > snapshot.Args.MaxFamilies)
        {
            familiesSkipped = groups.Count - snapshot.Args.MaxFamilies;
            groups = groups.Take(snapshot.Args.MaxFamilies).ToList();
            truncated = true;
        }

        // Progress logging without a long-lived scope: the matching awaits the
        // decision channel for minutes (C15), so only short state lines.
        SmartConLogger.Debug(
            "classify_types snapshot: " + snapshot.Types.Count + " type(s), " + groups.Count +
            " famil" + (groups.Count == 1 ? "y" : "ies") + ", table " +
            snapshot.Table.Entries.Count + " row(s)" +
            (truncated ? ", truncated (familiesSkipped=" + familiesSkipped + ")" : string.Empty));

        var result = await _matcher
            .MatchAsync(groups, snapshot.Table, options, ct)
            .ConfigureAwait(false);
        if (result.Error is not null)
            return DecisionFailed(result);

        SmartConLogger.Debug(
            "classify_types done: proposed " + CountOf(result, ClassifyTypeStatus.Proposed) +
            ", uncertain " + (CountOf(result, ClassifyTypeStatus.LowConfidence) +
                              CountOf(result, ClassifyTypeStatus.NoConfidence) +
                              CountOf(result, ClassifyTypeStatus.NoCandidates)) +
            ", decisionCalls " + result.DecisionCalls +
            (result.Incomplete ? ", INCOMPLETE" : string.Empty));

        var warnings = result.Warnings.Concat(snapshot.Warnings).ToList();
        var payload = BuildPayload(snapshot, groups, result, truncated, familiesSkipped);
        var message = payload.Message;
        return ToolResult.Ok(
                payload.Data,
                message,
                affectedCount: payload.AffectedCount,
                warnings: warnings.Count == 0 ? null : warnings)
            .WithDocumentVersion(snapshot.DocumentVersion);
    }

    private ToolResult DecisionFailed(ClassifyMatchResult result)
    {
        var detail = string.IsNullOrEmpty(result.ErrorDetail)
            ? string.Empty
            : " " + result.ErrorDetail + ".";
        SmartConLogger.Warn(
            "classify_types: the decision layer failed (" + result.Error + detail + "). " +
            "[Action: " + AgentClassificationMatcher.DecisionHint + "]");
        return ToolResult.Fail(
            result.Error!,
            result.Error + ": the decision layer did not answer." + detail + " " +
            DecisionUnavailableHint + ".",
            warnings: result.Warnings.Count == 0 ? null : result.Warnings,
            data: AgentJson.Serialize(new
            {
                decisionUsed = false,
                reason = result.Error,
                detail = result.ErrorDetail,
                hint = DecisionUnavailableHint
            }));
    }

    private ClassifySnapshot CollectOnRevitThread(
        string expectedDocumentId,
        JsonElement args,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var activeDocument = _revitContext.TryGetDocument();
        var routedId = AgentJson.GetString(args, "documentId");
        var routedToBackground = !string.IsNullOrEmpty(routedId)
                                 && (activeDocument is null || !string.Equals(
                                         AgentDocumentIdentity.GetDocumentId(activeDocument),
                                         routedId,
                                         StringComparison.Ordinal));
        var document = ResolveDocument(activeDocument, args);
        if (document is null)
        {
            return ClassifySnapshot.Failed(routedToBackground
                ? ToolResult.Fail(
                    "model_not_open",
                    "No background model is open with documentId '" + routedId + "'. " +
                    "Call open_model_background(path=...) first and pass the documentId " +
                    "it returns.")
                : ToolResult.Fail(
                    "no_active_document",
                    "No Revit project document is open. Open a project and try again."));
        }

        if (!routedToBackground
            && !string.IsNullOrEmpty(expectedDocumentId)
            && !string.Equals(
                AgentDocumentIdentity.GetDocumentId(document),
                expectedDocumentId,
                StringComparison.Ordinal))
        {
            return ClassifySnapshot.Failed(ToolResult.Fail(
                "document_changed",
                "The active document changed while the request was running. " +
                "Start a new request in the current document."));
        }

        try
        {
            var version = AgentDocumentState.Capture(document);
            var snapshot = Collect(document, args, ct);
            snapshot.DocumentVersion = version;
            if (snapshot.Failure is not null
                && snapshot.Failure.ErrorCode is not null
                && snapshot.Failure.ErrorCode.EndsWith("_not_found", StringComparison.Ordinal))
            {
                snapshot.Failure = snapshot.Failure.WithMessageSuffix(
                    AgentDocumentState.NotFoundHint(version));
            }

            return snapshot;
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
            return ClassifySnapshot.Failed(ToolResult.Fail(
                "tool_failed",
                ex.GetType().Name + ": " + ex.Message));
        }
    }

    private static Document? ResolveDocument(Document? activeDocument, JsonElement args)
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

    private static AgentToolDefinition BuildDefinition()
    {
        const string schema = @"{""type"":""object"",""properties"":{" +
            @"""documentId"":{""type"":""string"",""description"":""Background model routing: the documentId returned by open_model_background; omit for the active document""}," +
            @"""categories"":{""type"":""array"",""items"":{""type"":""string""},""description"":""OST_ enum names or localized display names; only types of these categories""}," +
            @"""familyIds"":{""type"":""array"",""items"":{""type"":""integer""},""description"":""Only types of these (loadable) families""}," +
            @"""typeIds"":{""type"":""array"",""items"":{""type"":""integer""},""description"":""Only these element type ids""}," +
            @"""nameContains"":{""type"":""string"",""description"":""Case-insensitive substring of the family or type name""}," +
            @"""onlyEmpty"":{""type"":""boolean"",""default"":true,""description"":""true: only types whose Assembly Code is empty; false: also types that already have a code (reported as current)""}," +
            @"""maxTypes"":{""type"":""integer"",""default"":400,""maximum"":2000,""description"":""At most this many types per call""}," +
            @"""textParameters"":{""type"":""array"",""items"":{""type"":""string""},""description"":""Extra parameter names (type or instance scope) whose values join the matching text""}," +
            @"""maxFamilies"":{""type"":""integer"",""default"":150,""maximum"":600,""description"":""At most this many families per call""}," +
            @"""minScore"":{""type"":""number"",""minimum"":0,""maximum"":1,""default"":0.5,""description"":""Below this score a proposal lands in uncertain""}," +
            @"""candidateDepth"":{""type"":""integer"",""minimum"":2,""maximum"":15,""default"":6,""description"":""How many codes survive the per-family stage""}," +
            @"""instructions"":{""type"":""string"",""description"":""Extra selection context for the decision model, max 1500 chars""}},""additionalProperties"":false}";

        return new AgentToolDefinition
        {
            Name = ToolName,
            Description =
                "Proposes a classification code (Assembly Code) for element types from the " +
                "classification table LOADED IN THE DOCUMENT - one call for the whole " +
                "selection instead of matching types one by one. Selects types (categories, " +
                "familyIds, typeIds, nameContains; onlyEmpty default true), semantically " +
                "picks a leaf code per family and per type and returns assignments " +
                "(family -> code + breadcrumb), uncertain items and a READY setParameters " +
                "argument (items with elementId=TYPE id, parameter=UNIFORMAT_CODE, " +
                "scope=type) - then call set_parameters with it (dryRun first). " +
                "classification_table_not_loaded means the document has no classifier: " +
                "tell the user instead of guessing a code. Read-only: writes nothing itself.",
            Risk = AgentToolRisk.Read,
            SupportsDryRun = false,
            SupportsBulk = true,
            Category = "Read",
            Pack = "semantic",
            TimeoutMs = 300000,
            InputSchemaJson = schema
        };
    }
}
