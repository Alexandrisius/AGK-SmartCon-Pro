using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

public sealed class SetViewVisibilityTool : AgentWriteToolBase
{
    private const int MaxItems = 200;

    public SetViewVisibilityTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "set_view_visibility",
        Description =
            "Hides or shows categories in views. items: [{viewId, visibility:{categories:" +
            "[{category, hidden}]}}], or select: {type?, levelId?, folder?, nameContains?, " +
            "templateId?, section?} with a top-level visibility — the selector resolves the view set, " +
            "so the view ids do not have to be listed by hand. Use the BuiltInCategory name " +
            "(OST_Grids) or the category display name. viewId may be a view template: a " +
            "template is a View, so its category visibility is written directly (no detach " +
            "from a view needed); with select, templates are never matched. " +
            "Worksets are not supported (the document is not " +
            "workshared). dryRun defaults to true.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""items"":{""type"":""array"",""minItems"":1,""maxItems"":200,""items"":{""type"":""object"",""properties"":{""viewId"":{""type"":""integer""},""visibility"":{""type"":""object"",""properties"":{""categories"":{""type"":""array"",""items"":{""type"":""object"",""properties"":{""category"":{""type"":""string""},""categoryId"":{""type"":""integer""},""hidden"":{""type"":""boolean"",""default"":true}},""additionalProperties"":false}},""worksets"":{""type"":""array"",""items"":{""type"":""object"",""properties"":{""name"":{""type"":""string""},""worksetId"":{""type"":""integer""},""hidden"":{""type"":""boolean"",""default"":true}},""additionalProperties"":false}}},""additionalProperties"":false}},""required"":[""viewId"",""visibility""],""additionalProperties"":false}},""select"":{""type"":""object"",""properties"":{""type"":{""type"":""string""},""viewType"":{""type"":""string""},""levelId"":{""type"":""integer""},""folder"":{""type"":""string""},""nameContains"":{""type"":""string""},""templateId"":{""type"":""integer""},""section"":{""type"":""string"",""description"":""Exact value of the project parameter 'Раздел' on the view""}},""additionalProperties"":false},""visibility"":{""type"":""object"",""properties"":{""categories"":{""type"":""array"",""items"":{""type"":""object"",""properties"":{""category"":{""type"":""string""},""categoryId"":{""type"":""integer""},""hidden"":{""type"":""boolean"",""default"":true}},""additionalProperties"":false}},""worksets"":{""type"":""array"",""items"":{""type"":""object"",""properties"":{""name"":{""type"":""string""},""worksetId"":{""type"":""integer""},""hidden"":{""type"":""boolean"",""default"":true}},""additionalProperties"":false}}},""additionalProperties"":false},""dryRun"":{""type"":""boolean"",""default"":true},""atomic"":{""type"":""boolean"",""default"":false}},""additionalProperties"":false}"
    };

    protected override PlanPreflightResult PreflightCore(
        Document document,
        JsonElement args,
        CancellationToken ct)
    {
        var basic = base.PreflightCore(document, args, ct);
        if (!basic.Ok)
            return basic;

        var request = AgentJson.Deserialize<SetViewVisibilityRequest>(args);
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
        var request = AgentJson.Deserialize<SetViewVisibilityRequest>(args);
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
        outcome.Description = "Set visibility on " + items.Count + " view(s)";

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

            if (item.Visibility is null || item.Visibility.IsEmpty)
            {
                ApplyViewTemplateTool.Fail(outcome, report, "invalid_arguments",
                    "visibility with at least one category is required for view " + item.ViewId + ".");
                if (ApplyViewTemplateTool.Abort(outcome, atomic)) return;
                continue;
            }

            report.Name = view.Name;
            var change = AgentViewSettingsWriter.ApplyVisibility(
                doc, view, item.Visibility, outcome.Warnings);
            ApplyViewTemplateTool.ApplyChange(outcome, view, report, change, "visibility", atomic);
            if (outcome.Aborted)
                return;
        }
    }

    private static List<SetViewVisibilityItem>? ExpandItems(
        Document doc,
        SetViewVisibilityRequest? request,
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

        var explicitItems = request.Items ?? new List<SetViewVisibilityItem>();
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
            message = "Pass items or a select filter with a top-level visibility.";
            return null;
        }

        if (request.Visibility is null || request.Visibility.IsEmpty)
        {
            errorCode = "invalid_arguments";
            message = "select requires a top-level visibility.";
            return null;
        }

        var viewIds = AgentViewSelector.Resolve(
            doc, request.Select!.Value, out errorCode, out message);
        if (errorCode is not null)
            return null;

        return viewIds
            .Select(viewId => new SetViewVisibilityItem { ViewId = viewId, Visibility = request.Visibility })
            .ToList();
    }

    private sealed class SetViewVisibilityRequest
    {
        public List<SetViewVisibilityItem>? Items { get; set; }

        public JsonElement? Select { get; set; }

        public ViewVisibilitySpec? Visibility { get; set; }
    }
}

internal sealed class SetViewVisibilityItem
{
    public long ViewId { get; set; }

    public ViewVisibilitySpec? Visibility { get; set; }
}
