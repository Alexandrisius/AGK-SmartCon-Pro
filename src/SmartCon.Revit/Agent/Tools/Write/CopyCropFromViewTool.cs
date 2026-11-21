using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Query;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Copies the crop region of a reference view onto target views (#351): the
/// explicit 'cut it like the reference view' scenario. Extents and the crop
/// sketch live in the view frame, so the copy is valid for views of the same
/// orientation; the skill states that limit. A target whose crop is controlled
/// by its view template fails per-item with crop_controlled_by_template: Revit
/// cannot detach crop from a template, so the remedy is removing the template.
/// </summary>
public sealed class CopyCropFromViewTool : AgentWriteToolBase
{
    private const int MaxTargets = 50;

    public CopyCropFromViewTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "copy_crop_from_view",
        Description =
            "Copies the crop region (extents, active/visible flags and the crop sketch when " +
            "present) from a reference view to 1-50 target views — the 'cut it like the " +
            "reference view' case. Crop coordinates are in the view frame, so source and " +
            "targets must share orientation (plans of the same orientation, or sections " +
            "looking the same way); across orientations the rectangle is copied as-is and " +
            "may look shifted. includeActive=false copies only the extents and keeps each " +
            "target's own crop on/off state. Source is given by sourceViewId or " +
            "sourceViewName; targets by targetViewIds or targetSelect (the same select " +
            "filter as apply_view_template). Guard: a target whose crop is controlled by " +
            "its view template fails per-item with crop_controlled_by_template (Revit " +
            "cannot detach crop from a template — remove the template with " +
            "apply_view_template(templateId=0) and retry). undo_last restores previous " +
            "crop regions. Dry run by default.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""sourceViewId"":{""type"":""integer""},""sourceViewName"":{""type"":""string""},""targetViewIds"":{""type"":""array"",""minItems"":1,""maxItems"":50,""items"":{""type"":""integer""}},""targetSelect"":{""type"":""object"",""properties"":{""type"":{""type"":""string""},""viewType"":{""type"":""string""},""levelId"":{""type"":""integer""},""folder"":{""type"":""string""},""nameContains"":{""type"":""string""},""templateId"":{""type"":""integer""}},""additionalProperties"":false},""includeActive"":{""type"":""boolean"",""default"":true},""dryRun"":{""type"":""boolean"",""default"":true},""atomic"":{""type"":""boolean"",""default"":false}},""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var request = AgentJson.Deserialize<CopyCropRequest>(args);
        var hasName = !string.IsNullOrWhiteSpace(request?.SourceViewName);

        if ((request?.SourceViewId is > 0) == hasName)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "Pass exactly one of sourceViewId or sourceViewName.";
            return;
        }

        if (!TryResolveSource(doc, request!, out var source, out var sourceCode, out var sourceReason))
        {
            outcome.Success = false;
            outcome.ErrorCode = sourceCode;
            outcome.Message = sourceReason;
            return;
        }

        var targets = ResolveTargets(doc, args, request!, out var targetsCode, out var targetsReason);
        if (targets is null)
        {
            outcome.Success = false;
            outcome.ErrorCode = targetsCode;
            outcome.Message = targetsReason;
            return;
        }

        if (targets.Count > MaxTargets)
        {
            outcome.Success = false;
            outcome.ErrorCode = "limit_exceeded";
            outcome.Message = "At most " + MaxTargets + " target views per call are allowed.";
            return;
        }

        var includeActive = AgentJson.GetBool(args, "includeActive", true);
        var sourceState = AgentViewWriteHelper.ReadCropState(source);
        if (!sourceState.Active)
        {
            outcome.Warnings.Add(
                "The crop of the source view '" + source.Name + "' is inactive; the stored " +
                "extents are copied and includeActive decides whether the target's crop is " +
                "switched on. [Action: enable the crop on the source first if this is not " +
                "what the user wants]");
        }

        if (args.TryGetProperty("targetSelect", out var appliedSelect)
            && appliedSelect.ValueKind == JsonValueKind.Object)
        {
            outcome.Data["selectedViewIds"] = targets;
            outcome.Data["selectorMatched"] = targets.Count;
        }

        var atomic = AgentJson.GetBool(args, "atomic", false);
        outcome.Description = "Copied crop from '" + source.Name + "' to " + targets.Count +
                              " view(s)";
        outcome.Data["sourceViewId"] = source.Id.GetValue();
        outcome.Data["sourceCrop"] = DescribeCrop(sourceState);

        foreach (var targetId in targets)
        {
            ct.ThrowIfCancellationRequested();

            var report = new WriteItemReport { Id = targetId };
            if (!AgentViewWriteHelper.TryGetView(
                    doc, targetId, out var target, out var code, out var reason))
            {
                ApplyViewTemplateTool.Fail(outcome, report, code, reason);
                if (ApplyViewTemplateTool.Abort(outcome, atomic)) return;
                continue;
            }

            if (target.Id.GetValue() == source.Id.GetValue())
            {
                report.Name = target.Name;
                report.Reason = "Source and target view are the same (no-op).";
                outcome.Batch.Skipped.Add(report);
                continue;
            }

            if (AgentViewSettingsReader.IsParameterLockedByTemplate(
                    target, BuiltInParameter.VIEWER_CROP_REGION, out var templateId, out var templateName))
            {
                report.Name = target.Name;
                report.SuggestedViewIds = templateId.HasValue
                    ? new List<long> { templateId.Value }
                    : null;
                ApplyViewTemplateTool.Fail(
                    outcome,
                    report,
                    "crop_controlled_by_template",
                    "The crop of '" + target.Name + "' is controlled by view template '" +
                    templateName + "'. Revit cannot detach crop from a template: remove the " +
                    "template from the view first (apply_view_template items[{viewId:" +
                    targetId + ", templateId:0}]) and retry copy_crop_from_view, or edit the " +
                    "crop in the template itself.");
                if (ApplyViewTemplateTool.Abort(outcome, atomic)) return;
                continue;
            }

            report.Name = target.Name;
            var before = AgentViewWriteHelper.ReadCropState(target);
            try
            {
                var merged = MergeStates(before, sourceState, includeActive);
                AgentViewWriteHelper.ApplyCropState(target, merged, outcome.Warnings);
            }
            catch (Exception ex)
            {
                ApplyViewTemplateTool.Fail(outcome, report, "crop_failed", ex.Message);
                if (ApplyViewTemplateTool.Abort(outcome, atomic)) return;
                continue;
            }

            var after = AgentViewWriteHelper.ReadCropState(target);
            var oldJson = AgentViewWriteHelper.Serialize(before);
            var newJson = AgentViewWriteHelper.Serialize(after);
            if (string.Equals(oldJson, newJson, StringComparison.Ordinal))
            {
                report.Reason = "The target already has the source crop (no-op).";
                outcome.Batch.Skipped.Add(report);
                continue;
            }

            report.OldValue = DescribeCrop(before);
            report.NewValue = DescribeCrop(after);
            outcome.Batch.Changed.Add(report);
            outcome.AffectedCount++;

            outcome.Items.Add(new ChangeItem
            {
                UniqueId = target.UniqueId,
                ElementId = target.Id.GetValue(),
                ParameterName = "crop",
                OldValue = oldJson,
                NewValue = newJson,
                Status = ChangeStatus.Applied
            });
        }
    }

    private static ViewCropState MergeStates(
        ViewCropState target,
        ViewCropState source,
        bool includeActive) =>
        new()
        {
            Active = includeActive ? source.Active : target.Active,
            Visible = includeActive ? source.Visible : target.Visible,
            ShapeSet = source.ShapeSet,
            ShapeMm = source.ShapeMm,
            MinMm = source.MinMm,
            MaxMm = source.MaxMm
        };

    private static bool TryResolveSource(
        Document doc,
        CopyCropRequest request,
        out View source,
        out string? errorCode,
        out string? reason)
    {
        source = null!;
        errorCode = null;
        reason = null;

        if (request.SourceViewId is > 0)
        {
            if (!AgentViewWriteHelper.TryGetView(
                    doc, request.SourceViewId.Value, out source, out errorCode, out reason))
            {
                return false;
            }

            return true;
        }

        var names = AgentViewWriteHelper.CollectViewNamesByName(doc);
        if (!names.TryGetValue(request.SourceViewName!.Trim(), out var foundId))
        {
            errorCode = "view_not_found";
            reason = "No view named '" + request.SourceViewName + "' exists in the document.";
            return false;
        }

        return AgentViewWriteHelper.TryGetView(doc, foundId, out source, out errorCode, out reason);
    }

    private static List<long>? ResolveTargets(
        Document doc,
        JsonElement args,
        CopyCropRequest request,
        out string? errorCode,
        out string? message)
    {
        errorCode = null;
        message = null;

        var explicitIds = request.TargetViewIds ?? new List<long>();
        var hasSelect = args.TryGetProperty("targetSelect", out var select)
                        && select.ValueKind == JsonValueKind.Object;

        if (explicitIds.Count > 0 && hasSelect)
        {
            errorCode = "invalid_arguments";
            message = "Pass targetViewIds or targetSelect, not both.";
            return null;
        }

        if (explicitIds.Count > 0)
        {
            var distinct = explicitIds
                .Where(id => id > 0)
                .Distinct()
                .ToList();
            if (distinct.Count != explicitIds.Count)
            {
                errorCode = "invalid_arguments";
                message = "targetViewIds contains duplicate or non-positive ids.";
                return null;
            }

            return distinct;
        }

        if (!hasSelect)
        {
            errorCode = "invalid_arguments";
            message = "Pass targetViewIds (1.." + MaxTargets + ") or a targetSelect filter.";
            return null;
        }

        var resolved = AgentViewSelector.Resolve(doc, select, out errorCode, out message);
        return errorCode is not null ? null : resolved.ToList();
    }

    internal static string DescribeCrop(ViewCropState state)
    {
        static string Point(double[]? values) => values is null
            ? "?"
            : string.Join(";", values.Select(value => Math.Round(value, 1)));

        return "min[" + Point(state.MinMm) + "] max[" + Point(state.MaxMm) + "] mm" +
               (state.ShapeSet ? " shape" : string.Empty) +
               " active=" + state.Active;
    }

    private sealed class CopyCropRequest
    {
        public long? SourceViewId { get; set; }

        public string? SourceViewName { get; set; }

        public List<long>? TargetViewIds { get; set; }
    }
}
