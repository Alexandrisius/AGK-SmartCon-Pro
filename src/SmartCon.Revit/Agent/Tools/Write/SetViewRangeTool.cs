using System.Text.Json;
using Autodesk.Revit.DB;
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
            "bottom, viewDepth}}], each plane is {levelId, offsetMm}. Only the planes you " +
            "pass are changed; offsets are in millimetres from the level. Use copy_view_settings " +
            "to copy a view range from a reference view. dryRun defaults to true.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""items"":{""type"":""array"",""minItems"":1,""maxItems"":200,""items"":{""type"":""object"",""properties"":{""viewId"":{""type"":""integer""},""viewRange"":{""type"":""object"",""properties"":{""top"":{""$ref"":""#/definitions/plane""},""cutPlane"":{""$ref"":""#/definitions/plane""},""bottom"":{""$ref"":""#/definitions/plane""},""viewDepth"":{""$ref"":""#/definitions/plane""}},""additionalProperties"":false}},""required"":[""viewId"",""viewRange""],""additionalProperties"":false}},""dryRun"":{""type"":""boolean"",""default"":true},""atomic"":{""type"":""boolean"",""default"":false}},""required"":[""items""],""definitions"":{""plane"":{""type"":""object"",""properties"":{""levelId"":{""type"":""integer""},""offsetMm"":{""type"":""number""}},""additionalProperties"":false}},""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var request = AgentJson.Deserialize<SetViewRangeRequest>(args);
        var items = request?.Items;
        if (items is null || items.Count == 0)
        {
            outcome.Success = false;
            outcome.ErrorCode = "no_items";
            outcome.Message = "The items array is empty; pass at least one {viewId,viewRange} pair.";
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

    private sealed class SetViewRangeRequest
    {
        public List<SetViewRangeItem>? Items { get; set; }
    }
}

internal sealed class SetViewRangeItem
{
    public long ViewId { get; set; }

    public ViewRangeSpec? ViewRange { get; set; }
}
