using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

public sealed class SetViewRangeTool : AgentWriteToolBase
{
    private const int MaxItems = 200;

    public SetViewRangeTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "set_view_range",
        Description =
            "Sets the view range of plan views. items: [{viewId, viewRange:{top, cutPlane, " +
            "bottom, viewDepth}}], or select: {type?, levelId?, folder?, nameContains?, " +
            "templateId?, section?} with a top-level viewRange — the selector resolves the view set, " +
            "so the view ids do not have to be listed by hand. Each plane is {levelId, " +
            "offsetMm}. Only the planes you pass are changed; offsets are in millimetres " +
            "from the level. Use copy_view_settings to copy a view range from a reference " +
            "view. dryRun defaults to true.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""items"":{""type"":""array"",""minItems"":1,""maxItems"":200,""items"":{""type"":""object"",""properties"":{""viewId"":{""type"":""integer""},""viewRange"":{""type"":""object"",""properties"":{""top"":{""$ref"":""#/definitions/plane""},""cutPlane"":{""$ref"":""#/definitions/plane""},""bottom"":{""$ref"":""#/definitions/plane""},""viewDepth"":{""$ref"":""#/definitions/plane""}},""additionalProperties"":false}},""required"":[""viewId"",""viewRange""],""additionalProperties"":false}},""select"":{""type"":""object"",""properties"":{""type"":{""type"":""string""},""viewType"":{""type"":""string""},""levelId"":{""type"":""integer""},""folder"":{""type"":""string""},""nameContains"":{""type"":""string""},""templateId"":{""type"":""integer""},""section"":{""type"":""string"",""description"":""Exact value of the project parameter 'Раздел' on the view""}},""additionalProperties"":false},""viewRange"":{""type"":""object"",""properties"":{""top"":{""$ref"":""#/definitions/plane""},""cutPlane"":{""$ref"":""#/definitions/plane""},""bottom"":{""$ref"":""#/definitions/plane""},""viewDepth"":{""$ref"":""#/definitions/plane""}},""additionalProperties"":false},""dryRun"":{""type"":""boolean"",""default"":true},""atomic"":{""type"":""boolean"",""default"":false}},""definitions"":{""plane"":{""type"":""object"",""properties"":{""levelId"":{""type"":""integer""},""offsetMm"":{""type"":""number""}},""additionalProperties"":false}},""additionalProperties"":false}"
    };

    protected override PlanPreflightResult PreflightCore(
        Document document,
        JsonElement args,
        CancellationToken ct)
    {
        var basic = base.PreflightCore(document, args, ct);
        if (!basic.Ok)
            return basic;

        var request = AgentJson.Deserialize<SetViewRangeRequest>(args);
        var items = ExpandItems(document, request, out var itemsError, out var itemsMessage);
        if (items is null)
        {
            // A select that matched nothing during a plan preflight is reported as
            // unresolved_id: the plan engine defers such a step to apply when it
            // depends on an earlier write that creates the matching views.
            var code = request?.Select is not null
                       && string.Equals(itemsError, "no_items", StringComparison.Ordinal)
                ? "unresolved_id"
                : itemsError;
            return PlanPreflightResult.Fail(
                code ?? "invalid_arguments",
                itemsMessage ?? "Invalid items.");
        }

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            if (document.GetElement(ElementIdCompat.Create(item.ViewId)) is not View)
                return PlanPreflightResult.Fail(
                    "unresolved_id", "View " + item.ViewId + " does not exist.");

            if (!ValidateLevels(document, item.ViewRange, out var levelError))
                return PlanPreflightResult.Fail("level_not_found", levelError!);
        }

        return PlanPreflightResult.Pass();
    }

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var request = AgentJson.Deserialize<SetViewRangeRequest>(args);
        var items = ExpandItems(doc, request, out var itemsError, out var itemsMessage);
        if (items is null)
        {
            outcome.Success = false;
            outcome.ErrorCode = itemsError ?? "invalid_arguments";
            outcome.Message = itemsMessage ?? "Invalid items.";
            return;
        }

        if (items.Count > MaxItems)
        {
            outcome.Success = false;
            outcome.ErrorCode = "limit_exceeded";
            outcome.Message = "At most " + MaxItems + " items per call are allowed; split the list.";
            return;
        }

        if (request?.Select is not null)
        {
            outcome.Data["selectedViewIds"] = items.Select(item => item.ViewId).ToList();
            outcome.Data["selectorMatched"] = items.Count;
        }

        var atomic = AgentJson.GetBool(args, "atomic", false);
        outcome.Description = "Set view range on " + items.Count + " view(s)";

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            var report = new WriteItemReport { Id = item.ViewId };
            if (!AgentViewWriteHelper.TryGetView(
                    doc, item.ViewId, out var view, out var code, out var reason))
            {
                ApplyViewTemplateTool.Fail(outcome, report, code, reason);
                if (ApplyViewTemplateTool.Abort(outcome, atomic)) return;
                continue;
            }

            if (!ValidateLevels(doc, item.ViewRange, out var levelError))
            {
                ApplyViewTemplateTool.Fail(outcome, report, "level_not_found", levelError);
                if (ApplyViewTemplateTool.Abort(outcome, atomic)) return;
                continue;
            }

            report.Name = view.Name;
            var change = AgentViewSettingsWriter.ApplyViewRange(view, item.ViewRange);
            ApplyViewTemplateTool.ApplyChange(outcome, view, report, change, "view_range", atomic);
            if (outcome.Aborted)
                return;
        }
    }

    private static bool ValidateLevels(
        Document doc,
        ViewRangeSpec? spec,
        out string? error)
    {
        error = null;
        if (spec is null)
            return true;

        foreach (var plane in new[] { spec.Top, spec.CutPlane, spec.Bottom, spec.ViewDepth })
        {
            if (plane?.LevelId is null)
                continue;

            if (!AgentViewWriteHelper.TryGetLevel(doc, plane.LevelId.Value, out _, out var reason))
            {
                error = reason;
                return false;
            }
        }

        return true;
    }

    private static List<SetViewRangeItem>? ExpandItems(
        Document doc,
        SetViewRangeRequest? request,
        out string? errorCode,
        out string? message)
    {
        errorCode = null;
        message = null;

        if (request is null)
        {
            errorCode = "invalid_arguments";
            message = "Arguments are required.";
            return null;
        }

        var explicitItems = request.Items ?? new List<SetViewRangeItem>();
        var hasSelect = request.Select.HasValue
                        && request.Select.Value.ValueKind == JsonValueKind.Object;

        if (explicitItems.Count > 0 && hasSelect)
        {
            errorCode = "invalid_arguments";
            message = "Pass items or select, not both.";
            return null;
        }

        if (explicitItems.Count > 0)
            return explicitItems;

        if (!hasSelect)
        {
            errorCode = "no_items";
            message = "Pass items or a select filter with a top-level viewRange.";
            return null;
        }

        if (request.ViewRange is null || request.ViewRange.IsEmpty)
        {
            errorCode = "invalid_arguments";
            message = "select requires a top-level viewRange.";
            return null;
        }

        var viewIds = AgentViewSelector.Resolve(
            doc, request.Select!.Value, out errorCode, out message);
        if (errorCode is not null)
            return null;

        return viewIds
            .Select(viewId => new SetViewRangeItem { ViewId = viewId, ViewRange = request.ViewRange })
            .ToList();
    }

    private sealed class SetViewRangeRequest
    {
        public List<SetViewRangeItem>? Items { get; set; }

        public JsonElement? Select { get; set; }

        public ViewRangeSpec? ViewRange { get; set; }
    }
}

internal sealed class SetViewRangeItem
{
    public long ViewId { get; set; }

    public ViewRangeSpec? ViewRange { get; set; }
}
