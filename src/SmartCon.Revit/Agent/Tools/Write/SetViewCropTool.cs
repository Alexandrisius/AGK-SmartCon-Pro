using System.Text.Json;
using Autodesk.Revit.DB;
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
            "maxMm, copyFromViewId}}]. minMm/maxMm are [x,y] (optional third value = z) in " +
            "millimetres in the view frame. copyFromViewId copies the crop region of another " +
            "view (the usual 'take the crop from the reference view' case). dryRun defaults to true.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""items"":{""type"":""array"",""minItems"":1,""maxItems"":200,""items"":{""type"":""object"",""properties"":{""viewId"":{""type"":""integer""},""crop"":{""type"":""object"",""properties"":{""active"":{""type"":""boolean""},""visible"":{""type"":""boolean""},""minMm"":{""type"":""array"",""items"":{""type"":""number""}},""maxMm"":{""type"":""array"",""items"":{""type"":""number""}},""copyFromViewId"":{""type"":""integer""}},""additionalProperties"":false}},""required"":[""viewId"",""crop""],""additionalProperties"":false}},""dryRun"":{""type"":""boolean"",""default"":true},""atomic"":{""type"":""boolean"",""default"":false}},""required"":[""items""],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var request = AgentJson.Deserialize<SetViewCropRequest>(args);
        var items = request?.Items;
        if (items is null || items.Count == 0)
        {
            outcome.Success = false;
            outcome.ErrorCode = "no_items";
            outcome.Message = "The items array is empty; pass at least one {viewId,crop} pair.";
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

    private sealed class SetViewCropRequest
    {
        public List<SetViewCropItem>? Items { get; set; }
    }
}

internal sealed class SetViewCropItem
{
    public long ViewId { get; set; }

    public ViewCropSpec? Crop { get; set; }
}
