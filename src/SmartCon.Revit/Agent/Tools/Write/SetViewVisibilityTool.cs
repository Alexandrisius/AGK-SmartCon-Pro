using System.Text.Json;
using Autodesk.Revit.DB;
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
            "[{category, hidden}]}}]. Use the BuiltInCategory name (OST_Grids) or the " +
            "category display name. Worksets are not supported (the document is not " +
            "workshared). dryRun defaults to true.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""items"":{""type"":""array"",""minItems"":1,""maxItems"":200,""items"":{""type"":""object"",""properties"":{""viewId"":{""type"":""integer""},""visibility"":{""type"":""object"",""properties"":{""categories"":{""type"":""array"",""items"":{""type"":""object"",""properties"":{""category"":{""type"":""string""},""categoryId"":{""type"":""integer""},""hidden"":{""type"":""boolean"",""default"":true}},""additionalProperties"":false}},""worksets"":{""type"":""array"",""items"":{""type"":""object"",""properties"":{""name"":{""type"":""string""},""worksetId"":{""type"":""integer""},""hidden"":{""type"":""boolean"",""default"":true}},""additionalProperties"":false}},""additionalProperties"":false}},""required"":[""viewId"",""visibility""],""additionalProperties"":false}},""dryRun"":{""type"":""boolean"",""default"":true},""atomic"":{""type"":""boolean"",""default"":false}},""required"":[""items""],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var request = AgentJson.Deserialize<SetViewVisibilityRequest>(args);
        var items = request?.Items;
        if (items is null || items.Count == 0)
        {
            outcome.Success = false;
            outcome.ErrorCode = "no_items";
            outcome.Message = "The items array is empty; pass at least one {viewId,visibility} pair.";
            return;
        }

        if (items.Count > MaxItems)
        {
            outcome.Success = false;
            outcome.ErrorCode = "limit_exceeded";
            outcome.Message = "At most " + MaxItems + " items per call are allowed; split the list.";
            return;
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

    private sealed class SetViewVisibilityRequest
    {
        public List<SetViewVisibilityItem>? Items { get; set; }
    }
}

internal sealed class SetViewVisibilityItem
{
    public long ViewId { get; set; }

    public ViewVisibilitySpec? Visibility { get; set; }
}
