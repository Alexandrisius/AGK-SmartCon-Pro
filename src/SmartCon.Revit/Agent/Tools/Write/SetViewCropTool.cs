using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

public sealed class SetViewCropTool : AgentWriteToolBase
{
    private const int MaxItems = 200;

    public SetViewCropTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "set_view_crop",
        Description =
            "Sets the crop region of views. items: [{viewId, crop:{active, visible, minMm, " +
            "maxMm, copyFromViewId}}], or select: {type?, levelId?, folder?, nameContains?, " +
            "templateId?} with a top-level crop — the selector resolves the view set, so the " +
            "view ids do not have to be listed by hand. minMm/maxMm are [x,y] (optional third " +
            "value = z) in millimetres in the view frame. copyFromViewId copies the crop region " +
            "of another view (the usual 'take the crop from the reference view' case). dryRun " +
            "defaults to true.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""items"":{""type"":""array"",""minItems"":1,""maxItems"":200,""items"":{""type"":""object"",""properties"":{""viewId"":{""type"":""integer""},""crop"":{""type"":""object"",""properties"":{""active"":{""type"":""boolean""},""visible"":{""type"":""boolean""},""minMm"":{""type"":""array"",""items"":{""type"":""number""}},""maxMm"":{""type"":""array"",""items"":{""type"":""number""}},""copyFromViewId"":{""type"":""integer""}},""additionalProperties"":false}},""required"":[""viewId"",""crop""],""additionalProperties"":false}},""select"":{""type"":""object"",""properties"":{""type"":{""type"":""string""},""viewType"":{""type"":""string""},""levelId"":{""type"":""integer""},""folder"":{""type"":""string""},""nameContains"":{""type"":""string""},""templateId"":{""type"":""integer""}},""additionalProperties"":false},""crop"":{""type"":""object"",""properties"":{""active"":{""type"":""boolean""},""visible"":{""type"":""boolean""},""minMm"":{""type"":""array"",""items"":{""type"":""number""}},""maxMm"":{""type"":""array"",""items"":{""type"":""number""}},""copyFromViewId"":{""type"":""integer""}},""additionalProperties"":false},""dryRun"":{""type"":""boolean"",""default"":true},""atomic"":{""type"":""boolean"",""default"":false}},""additionalProperties"":false}"
    };

    protected override PlanPreflightResult PreflightCore(
        Document document,
        JsonElement args,
        CancellationToken ct)
    {
        var basic = base.PreflightCore(document, args, ct);
        if (!basic.Ok)
            return basic;

        var request = AgentJson.Deserialize<SetViewCropRequest>(args);
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
        var request = AgentJson.Deserialize<SetViewCropRequest>(args);
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
        outcome.Description = "Set crop region on " + items.Count + " view(s)";

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

            if (item.Crop is null)
            {
                ApplyViewTemplateTool.Fail(outcome, report, "invalid_arguments",
                    "crop is required for view " + item.ViewId + ".");
                if (ApplyViewTemplateTool.Abort(outcome, atomic)) return;
                continue;
            }

            if ((item.Crop.MinMm is null) != (item.Crop.MaxMm is null))
            {
                ApplyViewTemplateTool.Fail(outcome, report, "invalid_arguments",
                    "Pass both minMm and maxMm, or neither.");
                if (ApplyViewTemplateTool.Abort(outcome, atomic)) return;
                continue;
            }

            report.Name = view.Name;
            var change = AgentViewSettingsWriter.ApplyCrop(
                doc, view, item.Crop, outcome.Warnings);
            ApplyViewTemplateTool.ApplyChange(outcome, view, report, change, "crop", atomic);
            if (outcome.Aborted)
                return;
        }
    }

    private static List<SetViewCropItem>? ExpandItems(
        Document doc,
        SetViewCropRequest? request,
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

        var explicitItems = request.Items ?? new List<SetViewCropItem>();
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
            message = "Pass items or a select filter with a top-level crop.";
            return null;
        }

        if (request.Crop is null || request.Crop.IsEmpty)
        {
            errorCode = "invalid_arguments";
            message = "select requires a top-level crop.";
            return null;
        }

        var viewIds = AgentViewSelector.Resolve(
            doc, request.Select!.Value, out errorCode, out message);
        if (errorCode is not null)
            return null;

        return viewIds
            .Select(viewId => new SetViewCropItem { ViewId = viewId, Crop = request.Crop })
            .ToList();
    }

    private sealed class SetViewCropRequest
    {
        public List<SetViewCropItem>? Items { get; set; }

        public JsonElement? Select { get; set; }

        public ViewCropSpec? Crop { get; set; }
    }
}

internal sealed class SetViewCropItem
{
    public long ViewId { get; set; }

    public ViewCropSpec? Crop { get; set; }
}
