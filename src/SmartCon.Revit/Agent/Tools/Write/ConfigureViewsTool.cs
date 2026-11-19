using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

public sealed class ConfigureViewsTool : AgentWriteToolBase
{
    private const int MaxItems = 100;

    public ConfigureViewsTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "configure_views",
        Description =
            "Applies several view settings in one call. items: [{viewId, newName?, templateId?, " +
            "overwriteTemplate?, viewRange?, crop?, visibility?}] — every field except viewId is " +
            "optional and only the given sections are applied. Alternatively select: {type?, " +
            "levelId?, folder?, nameContains?, templateId?} plus settings: {templateId?, " +
            "overwriteTemplate?, viewRange?, crop?, visibility?} applies one settings block to " +
            "every matched view (newName is not allowed with select). Order per view: name, " +
            "template, view range, crop, visibility. A template does not carry the reference " +
            "view's name: when the user asks to make views 'like a reference view', include " +
            "newName in the same plan if the naming pattern should be reproduced. dryRun " +
            "defaults to true; atomic=true rolls the whole batch back on the first failure.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""items"":{""type"":""array"",""maxItems"":100,""items"":{""type"":""object"",""properties"":{""viewId"":{""type"":""integer""},""newName"":{""type"":""string""},""templateId"":{""type"":""integer""},""overwriteTemplate"":{""type"":""boolean"",""default"":false},""viewRange"":{""type"":""object""},""crop"":{""type"":""object""},""visibility"":{""type"":""object""}},""required"":[""viewId""],""additionalProperties"":false}},""select"":{""type"":""object"",""properties"":{""type"":{""type"":""string""},""viewType"":{""type"":""string""},""levelId"":{""type"":""integer""},""folder"":{""type"":""string""},""nameContains"":{""type"":""string""},""templateId"":{""type"":""integer""}},""additionalProperties"":false},""settings"":{""type"":""object"",""properties"":{""templateId"":{""type"":""integer""},""overwriteTemplate"":{""type"":""boolean"",""default"":false},""viewRange"":{""type"":""object""},""crop"":{""type"":""object""},""visibility"":{""type"":""object""}},""additionalProperties"":false},""dryRun"":{""type"":""boolean"",""default"":true},""atomic"":{""type"":""boolean"",""default"":false}},""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var request = AgentJson.Deserialize<ConfigureViewsRequest>(args);
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
            outcome.Message = "At most " + MaxItems + " views per call are allowed; split the list.";
            return;
        }

        if (request?.Select is not null)
        {
            outcome.Data["selectedViewIds"] = items.Select(item => item.ViewId).ToList();
            outcome.Data["selectorMatched"] = items.Count;
        }

        var atomic = AgentJson.GetBool(args, "atomic", false);
        outcome.Description = "Configured " + items.Count + " view(s)";

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

            report.Name = view.Name;
            var applied = new List<(string Key, AgentViewSettingsWriter.Change Change)>();
            AgentViewSettingsWriter.Change? failure = null;
            string? failedSection = null;

            if (!string.IsNullOrWhiteSpace(item.NewName))
            {
                var change = view.IsTemplate
                    ? AgentViewSettingsWriter.Change.Error(
                        "view_is_template",
                        "View " + item.ViewId + " is a view template; rename templates explicitly out of scope.")
                    : view.ViewType is ViewType.DrawingSheet or ViewType.ProjectBrowser
                        or ViewType.SystemBrowser or ViewType.Internal
                        ? AgentViewSettingsWriter.Change.Error(
                            "unsupported_view_type",
                            "View " + item.ViewId + " is a " + view.ViewType +
                            " and cannot be renamed here.")
                        : AgentViewSettingsWriter.Rename(view, item.NewName);

                if (change.ErrorCode is not null)
                {
                    failure = change;
                    failedSection = "view_name";
                }
                else if (change.Changed)
                    applied.Add(("view_name", change));
            }

            if (failure is null && item.TemplateId.HasValue)
            {
                var change = AgentViewSettingsWriter.ApplyTemplate(
                    doc, view, item.TemplateId.Value, item.OverwriteTemplate);
                if (change.ErrorCode is not null)
                {
                    failure = change;
                    failedSection = "view_template";
                }
                else if (change.Changed)
                    applied.Add(("view_template", change));
            }

            if (failure is null && item.ViewRange is { IsEmpty: false })
            {
                var change = AgentViewSettingsWriter.ApplyViewRange(view, item.ViewRange);
                if (change.ErrorCode is not null)
                {
                    failure = change;
                    failedSection = "view_range";
                }
                else if (change.Changed)
                    applied.Add(("view_range", change));
            }

            if (failure is null && item.Crop is { IsEmpty: false })
            {
                var change = AgentViewSettingsWriter.ApplyCrop(
                    doc, view, item.Crop, outcome.Warnings);
                if (change.ErrorCode is not null)
                {
                    failure = change;
                    failedSection = "crop";
                }
                else if (change.Changed)
                    applied.Add(("crop", change));
            }

            if (failure is null && item.Visibility is { IsEmpty: false })
            {
                var change = AgentViewSettingsWriter.ApplyVisibility(
                    doc, view, item.Visibility, outcome.Warnings);
                if (change.ErrorCode is not null)
                {
                    failure = change;
                    failedSection = "visibility";
                }
                else if (change.Changed)
                    applied.Add(("visibility", change));
            }

            if (failure is not null)
            {
                // atomic=false: the sections applied before the failure are already in
                // the model — they must be recorded so undo_last can revert them.
                if (applied.Count > 0)
                {
                    var appliedReport = new WriteItemReport
                    {
                        Id = report.Id,
                        Name = report.Name,
                        Detail = "partial: applied " +
                                 string.Join(", ", applied.Select(entry => entry.Key)),
                        OldValue = string.Join(" | ", applied.Select(entry => entry.Change.OldValue)),
                        NewValue = string.Join(" | ", applied.Select(entry => entry.Change.NewValue)),
                        Sections = BuildSections(applied, failedSection, failure)
                    };

                    AddChangeItems(outcome, view, applied);
                    outcome.Batch.Changed.Add(appliedReport);
                    outcome.AffectedCount++;
                    report.Detail = "partial: applied " + string.Join(", ", applied.Select(entry => entry.Key));
                    outcome.Warnings.Add(
                        "Partial apply on view '" + report.Name + "': " +
                        string.Join(", ", applied.Select(entry => entry.Key)) +
                        " applied; the remaining section failed — review failed[] and use undo_last if needed.");
                }

                ApplyViewTemplateTool.Fail(outcome, report, failure.ErrorCode!, failure.Reason);
                if (ApplyViewTemplateTool.Abort(outcome, atomic)) return;
                continue;
            }

            if (applied.Count == 0)
            {
                report.Reason = "Nothing to change: the view already matches the requested settings.";
                outcome.Batch.Skipped.Add(report);
                continue;
            }

            report.Detail = "sections: " + string.Join(", ", applied.Select(entry => entry.Key));
            report.Sections = BuildSections(applied, failedSection: null, failure: null);
            report.OldValue = string.Join(" | ", applied.Select(entry => entry.Change.OldValue));
            report.NewValue = string.Join(" | ", applied.Select(entry => entry.Change.NewValue));
            outcome.Batch.Changed.Add(report);
            outcome.AffectedCount++;

            AddChangeItems(outcome, view, applied);
        }
    }

    private static List<SectionReport> BuildSections(
        IReadOnlyList<(string Key, AgentViewSettingsWriter.Change Change)> applied,
        string? failedSection,
        AgentViewSettingsWriter.Change? failure)
    {
        var sections = new List<SectionReport>(applied.Count + 1);
        foreach (var entry in applied)
        {
            sections.Add(new SectionReport
            {
                Section = entry.Key,
                Status = "applied"
            });
        }

        if (failedSection is not null)
        {
            sections.Add(new SectionReport
            {
                Section = failedSection,
                Status = "failed",
                Reason = failure?.Reason
            });
        }

        return sections;
    }

    private static void AddChangeItems(
        WriteOutcome outcome,
        View view,
        IReadOnlyList<(string Key, AgentViewSettingsWriter.Change Change)> applied)
    {
        foreach (var entry in applied)
        {
            outcome.Items.Add(new ChangeItem
            {
                UniqueId = view.UniqueId,
                ElementId = view.Id.GetValue(),
                ParameterName = entry.Key,
                OldValue = entry.Change.OldValue,
                NewValue = entry.Change.NewValue,
                Status = ChangeStatus.Applied
            });
        }
    }

    private static List<ConfigureViewItem>? ExpandItems(
        Document doc,
        ConfigureViewsRequest? request,
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

        var explicitItems = request.Items ?? new List<ConfigureViewItem>();
        var hasSelect = request.Select.HasValue
                        && request.Select.Value.ValueKind == JsonValueKind.Object;

        if (explicitItems.Count > 0 && hasSelect)
        {
            errorCode = "invalid_arguments";
            message = "Pass items or select+settings, not both.";
            return null;
        }

        if (explicitItems.Count > 0)
            return explicitItems;

        if (!hasSelect)
        {
            errorCode = "invalid_arguments";
            message = "Pass items or select+settings.";
            return null;
        }

        var settings = request.Settings;
        if (settings is null)
        {
            errorCode = "invalid_arguments";
            message = "select requires a settings object with the sections to apply.";
            return null;
        }

        var viewIds = AgentViewSelector.Resolve(
            doc, request.Select!.Value, out errorCode, out message);
        if (errorCode is not null)
            return null;

        return viewIds
            .Select(viewId => new ConfigureViewItem
            {
                ViewId = viewId,
                TemplateId = settings.TemplateId,
                OverwriteTemplate = settings.OverwriteTemplate,
                ViewRange = settings.ViewRange,
                Crop = settings.Crop,
                Visibility = settings.Visibility
            })
            .ToList();
    }

    private sealed class ConfigureViewsRequest
    {
        public List<ConfigureViewItem>? Items { get; set; }

        public JsonElement? Select { get; set; }

        public ConfigureViewsSettings? Settings { get; set; }
    }

    private sealed class ConfigureViewsSettings
    {
        public long? TemplateId { get; set; }

        public bool OverwriteTemplate { get; set; }

        public ViewRangeSpec? ViewRange { get; set; }

        public ViewCropSpec? Crop { get; set; }

        public ViewVisibilitySpec? Visibility { get; set; }
    }
}
