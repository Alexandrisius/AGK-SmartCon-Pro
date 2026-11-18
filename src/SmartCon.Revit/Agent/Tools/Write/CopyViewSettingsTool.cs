using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

public sealed class CopyViewSettingsTool : AgentWriteToolBase
{
    private const int MaxTargets = 200;

    public CopyViewSettingsTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "copy_view_settings",
        Description =
            "Copies settings from a reference view to target views. sections: any of " +
            "\"crop\", \"viewRange\", \"visibility\", \"template\", \"all\"; default is [\"crop\"]. " +
            "Crop copy works across views of the same orientation (the usual 'take the crop " +
            "region from the reference view' case). The template section copies the source " +
            "view's template onto the targets. Names are never copied: if the user asks to make " +
            "target views 'like the reference view' and their names should follow the same " +
            "pattern, include rename_views (or configure_views newName) in the same dry-run plan " +
            "and tell the user the rename is a separate change. dryRun defaults to true.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""sourceViewId"":{""type"":""integer""},""targetViewIds"":{""type"":""array"",""minItems"":1,""maxItems"":200,""items"":{""type"":""integer""}},""sections"":{""type"":""array"",""items"":{""type"":""string"",""enum"":[""crop"",""viewRange"",""visibility"",""template"",""all""]}},""overwriteTemplate"":{""type"":""boolean"",""default"":false},""dryRun"":{""type"":""boolean"",""default"":true},""atomic"":{""type"":""boolean"",""default"":false}},""required"":[""sourceViewId"",""targetViewIds""],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var request = AgentJson.Deserialize<CopyViewSettingsRequest>(args);
        if (request is null || request.SourceViewId <= 0)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "sourceViewId is required.";
            return;
        }

        var targets = request.TargetViewIds ?? new List<long>();
        if (targets.Count == 0)
        {
            outcome.Success = false;
            outcome.ErrorCode = "no_items";
            outcome.Message = "targetViewIds is empty; pass at least one target view id.";
            return;
        }

        if (targets.Count > MaxTargets)
        {
            outcome.Success = false;
            outcome.ErrorCode = "limit_exceeded";
            outcome.Message = "At most " + MaxTargets + " target views per call are allowed.";
            return;
        }

        if (!AgentViewWriteHelper.TryGetView(
                doc, request.SourceViewId, out var source, out var sourceCode, out var sourceReason))
        {
            outcome.Success = false;
            outcome.ErrorCode = sourceCode;
            outcome.Message = sourceReason;
            return;
        }

        var sections = ResolveSections(request.Sections);
        if (sections.Count == 0)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "sections must contain at least one of: crop, viewRange, " +
                              "visibility, template, all.";
            return;
        }

        var atomic = AgentJson.GetBool(args, "atomic", false);
        var overwriteTemplate = request.OverwriteTemplate;
        outcome.Description = "Copied settings from '" + source.Name + "' to " +
                              targets.Count + " view(s)";

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

            report.Name = target.Name;
            var applied = new List<(string Key, AgentViewSettingsWriter.Change Change)>();
            var skipReasons = new List<string>();
            var sectionReports = new List<SectionReport>();
            AgentViewSettingsWriter.Change? failure = null;

            foreach (var section in sections)
            {
                var change = ApplySection(doc, source, target, section, overwriteTemplate, outcome);
                if (change.ErrorCode is not null)
                {
                    sectionReports.Add(new SectionReport
                    {
                        Section = section,
                        Status = "failed",
                        Reason = change.Reason
                    });
                    failure = change;
                    break;
                }

                if (change.Changed)
                {
                    applied.Add((KeyFor(section), change));
                    sectionReports.Add(new SectionReport
                    {
                        Section = section,
                        Status = "copied"
                    });
                }
                else
                {
                    var skipReason = string.IsNullOrEmpty(change.Reason)
                        ? "nothing to change"
                        : change.Reason;
                    skipReasons.Add(section + ": " + skipReason);
                    sectionReports.Add(new SectionReport
                    {
                        Section = section,
                        Status = "skipped",
                        Reason = skipReason
                    });
                }
            }

            report.Sections = sectionReports;

            if (failure is not null)
            {
                // atomic=false: sections copied before the failure are already in the
                // model — record them so undo_last can revert them.
                if (applied.Count > 0)
                {
                    AddChangeItems(outcome, target, applied);
                    outcome.AffectedCount++;
                    report.Detail = "partial: copied " +
                                    string.Join(", ", applied.Select(entry => entry.Key));
                    outcome.Warnings.Add(
                        "Partial copy to view '" + report.Name + "': " +
                        string.Join(", ", applied.Select(entry => entry.Key)) +
                        " copied; the remaining section failed — review failed[] and use undo_last if needed.");
                }

                ApplyViewTemplateTool.Fail(outcome, report, failure.ErrorCode!, failure.Reason);
                if (ApplyViewTemplateTool.Abort(outcome, atomic)) return;
                continue;
            }

            if (applied.Count == 0)
            {
                report.Reason = skipReasons.Count > 0
                    ? string.Join("; ", skipReasons)
                    : "Nothing to copy: the target already matches the source.";
                outcome.Batch.Skipped.Add(report);
                continue;
            }

            report.Detail = "copied: " + string.Join(", ", applied.Select(entry => entry.Key)) +
                            (skipReasons.Count == 0
                                ? string.Empty
                                : "; skipped: " + string.Join("; ", skipReasons));
            report.OldValue = string.Join(" | ", applied.Select(entry => entry.Change.OldValue));
            report.NewValue = string.Join(" | ", applied.Select(entry => entry.Change.NewValue));
            outcome.Batch.Changed.Add(report);
            outcome.AffectedCount++;

            AddChangeItems(outcome, target, applied);
        }
    }

    private static void AddChangeItems(
        WriteOutcome outcome,
        View target,
        IReadOnlyList<(string Key, AgentViewSettingsWriter.Change Change)> applied)
    {
        foreach (var entry in applied)
        {
            outcome.Items.Add(new ChangeItem
            {
                UniqueId = target.UniqueId,
                ElementId = target.Id.GetValue(),
                ParameterName = entry.Key,
                OldValue = entry.Change.OldValue,
                NewValue = entry.Change.NewValue,
                Status = ChangeStatus.Applied
            });
        }
    }

    private static AgentViewSettingsWriter.Change ApplySection(
        Document doc,
        View source,
        View target,
        string section,
        bool overwriteTemplate,
        WriteOutcome outcome)
    {
        switch (section)
        {
            case "crop":
                return AgentViewSettingsWriter.ApplyCrop(
                    doc,
                    target,
                    new ViewCropSpec { CopyFromViewId = source.Id.GetValue() },
                    outcome.Warnings);

            case "viewRange":
            {
                var spec = AgentViewWriteHelper.ReadViewRange(source);
                if (spec is null)
                {
                    return AgentViewSettingsWriter.Change.Error(
                        "view_range_unavailable",
                        "The source view '" + source.Name + "' has no view range (it is not a plan view).");
                }

                return AgentViewSettingsWriter.ApplyViewRange(target, spec);
            }

            case "visibility":
                return AgentViewSettingsWriter.MirrorVisibility(
                    doc, source, target, outcome.Warnings);

            case "template":
            {
                var templateId = source.ViewTemplateId;
                if (templateId == ElementId.InvalidElementId)
                    return AgentViewSettingsWriter.Change.Skip("The source view has no template.");

                return AgentViewSettingsWriter.ApplyTemplate(
                    doc, target, templateId.GetValue(), overwriteTemplate);
            }

            default:
                return AgentViewSettingsWriter.Change.Error(
                    "invalid_arguments",
                    "Unknown section '" + section + "'.");
        }
    }

    private static List<string> ResolveSections(IReadOnlyList<string>? requested)
    {
        if (requested is null || requested.Count == 0)
            return new List<string> { "crop" };

        var result = new List<string>();
        foreach (var raw in requested)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            if (string.Equals(raw, "all", StringComparison.OrdinalIgnoreCase))
                return new List<string> { "template", "viewRange", "crop", "visibility" };

            var canonical = raw.Trim();
            if (string.Equals(canonical, "viewrange", StringComparison.OrdinalIgnoreCase))
                canonical = "viewRange";
            else if (string.Equals(canonical, "crop", StringComparison.OrdinalIgnoreCase))
                canonical = "crop";
            else if (string.Equals(canonical, "visibility", StringComparison.OrdinalIgnoreCase))
                canonical = "visibility";
            else if (string.Equals(canonical, "template", StringComparison.OrdinalIgnoreCase))
                canonical = "template";
            else
                continue;

            if (!result.Contains(canonical))
                result.Add(canonical);
        }

        return result;
    }

    private static string KeyFor(string section) => section switch
    {
        "crop" => "crop",
        "viewRange" => "view_range",
        "visibility" => "visibility",
        _ => "view_template"
    };

    private sealed class CopyViewSettingsRequest
    {
        public long SourceViewId { get; set; }

        public List<long>? TargetViewIds { get; set; }

        public List<string>? Sections { get; set; }

        public bool OverwriteTemplate { get; set; }
    }
}
