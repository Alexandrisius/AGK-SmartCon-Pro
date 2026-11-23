using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Query;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.Revit.Agent.Tools.Write;

public sealed class ApplyViewTemplateTool : AgentWriteToolBase
{
    private const int MaxItems = 200;

    public ApplyViewTemplateTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "apply_view_template",
        Description =
            "Applies an existing view template to views. items: [{viewId, templateId, " +
            "overwriteTemplate?}] or select: {type?, levelId?, folder?, nameContains?, " +
            "templateId?, section?} with a top-level templateId (select resolves the view set " +
            "for you). select.section matches the project parameter 'Раздел' by exact value " +
            "(section_parameter_missing when no view has the parameter). exceptViewIds / " +
            "exceptNameContains remove views from the resolved set (excludedViewIds). " +
            "templateId=0 (or null) detaches the current template from the views. A view that " +
            "already has a different template is skipped unless overwriteTemplate=true. The " +
            "template must belong to the same view family. Every view that receives a " +
            "template reports skippedByTemplateInclude: sections (viewRange/visibility/crop; " +
            "visibility-model/visibility-annotation when only one V/G parameter is off) " +
            "left at the view's own values because their Include is off on the template — " +
            "summary lines in templateIncludeSkips. A template that includes V/G Overrides " +
            "Filters takes over the filter list of the view: the view's own filters are " +
            "replaced by the template's, and the result names them per view " +
            "(ownFiltersReplaced) with the way to keep them. dryRun defaults to true.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""items"":{""type"":""array"",""maxItems"":200,""items"":{""type"":""object"",""properties"":{""viewId"":{""type"":""integer""},""templateId"":{""type"":[""integer"",""null""],""description"":""0 or null detaches the current template""},""overwriteTemplate"":{""type"":""boolean"",""default"":false}},""required"":[""viewId"",""templateId""],""additionalProperties"":false}},""select"":{""type"":""object"",""properties"":{""type"":{""type"":""string""},""viewType"":{""type"":""string""},""levelId"":{""type"":""integer""},""folder"":{""type"":""string""},""nameContains"":{""type"":""string""},""templateId"":{""type"":""integer""},""section"":{""type"":""string"",""description"":""Exact value of the project parameter 'Раздел' on the view""}},""additionalProperties"":false},""templateId"":{""type"":[""integer"",""null""],""description"":""0 or null detaches the current template""},""overwriteTemplate"":{""type"":""boolean"",""default"":false},""exceptViewIds"":{""type"":""array"",""items"":{""type"":""integer""},""description"":""View ids removed from the resolved selection""},""exceptNameContains"":{""type"":""string"",""description"":""Views whose name contains this substring are removed from the selection""},""dryRun"":{""type"":""boolean"",""default"":true},""atomic"":{""type"":""boolean"",""default"":false}},""additionalProperties"":false}"
    };

    protected override PlanPreflightResult PreflightCore(
        Document document,
        JsonElement args,
        CancellationToken ct)
    {
        var basic = base.PreflightCore(document, args, ct);
        if (!basic.Ok)
            return basic;

        var request = AgentJson.Deserialize<ApplyTemplateRequest>(args);
        var expanded = ExpandItems(document, request, out var itemsError, out var itemsMessage);
        if (expanded is null)
        {
            return PlanPreflightResult.Fail(
                itemsError ?? "invalid_arguments",
                itemsMessage ?? "Invalid items.");
        }

        foreach (var item in expanded.Items)
        {
            ct.ThrowIfCancellationRequested();

            if (document.GetElement(ElementIdCompat.Create(item.ViewId)) is not View view)
                return PlanPreflightResult.Fail(
                    "unresolved_id", "View " + item.ViewId + " does not exist.");

            if (item.TemplateId is null or <= 0)
                continue;

            if (document.GetElement(ElementIdCompat.Create(item.TemplateId.Value)) is not View template
                || !template.IsTemplate)
            {
                return PlanPreflightResult.Fail(
                    "invalid_template",
                    "Element " + item.TemplateId.Value + " is not a view template (0 detaches).");
            }

            if (!AgentViewWriteHelper.AreFamiliesCompatible(document, view, template))
            {
                return PlanPreflightResult.Fail(
                    "template_not_applicable",
                    "Template '" + template.Name + "' belongs to a different view family.");
            }
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
        var request = AgentJson.Deserialize<ApplyTemplateRequest>(args);
        var expanded = ExpandItems(doc, request, out var itemsError, out var itemsMessage);
        if (expanded is null)
        {
            outcome.Success = false;
            outcome.ErrorCode = itemsError ?? "invalid_arguments";
            outcome.Message = itemsMessage ?? "Invalid items.";
            return;
        }

        var items = expanded.Items;
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

        if (expanded.ExcludedViewIds.Count > 0)
            outcome.Data["excludedViewIds"] = expanded.ExcludedViewIds;

        var atomic = AgentJson.GetBool(args, "atomic", false);
        outcome.Description = "Applied view template to " + items.Count + " view(s)";

        var includeSkips = new TemplateIncludeSkipReport(dryRun);
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            var report = new WriteItemReport { Id = item.ViewId };
            if (!AgentViewWriteHelper.TryGetView(
                    doc, item.ViewId, out var view, out var code, out var reason))
            {
                Fail(outcome, report, code, reason);
                if (Abort(outcome, atomic)) return;
                continue;
            }

            report.Name = view.Name;
            var detach = item.TemplateId is null or <= 0;
            var willApply = !detach
                && WillApplyTemplate(view, item.TemplateId!.Value, item.OverwriteTemplate);
            if (dryRun && willApply)
            {
                AddTemplateDelta(doc, view, item.TemplateId!.Value, outcome);
            }

            var change = detach
                ? AgentViewSettingsWriter.DetachTemplate(view)
                : AgentViewSettingsWriter.ApplyTemplate(
                    doc, view, item.TemplateId!.Value, item.OverwriteTemplate);

            ApplyChange(outcome, view, report, change, "view_template", atomic);
            if (outcome.Aborted)
                return;

            if (willApply && change.Changed)
                includeSkips.Record(doc, item.TemplateId!.Value, report);
        }

        includeSkips.Publish(outcome);
    }

    /// <summary>
    /// True when ApplyTemplate will really assign the template. A view that
    /// already owns the same template, or owns another one without
    /// overwriteTemplate, is returned as a skip — its delta must not create a
    /// plan lock, or a valid crop/range step is rejected as a conflict.
    /// </summary>
    private static bool WillApplyTemplate(View view, long templateId, bool overwrite)
    {
        var currentId = view.ViewTemplateId;
        if (currentId != ElementId.InvalidElementId && currentId.GetValue() == templateId)
            return false;

        return currentId == ElementId.InvalidElementId || overwrite;
    }

    /// <summary>
    /// Dry-run consequence preview (live dogfood P1-5): scale before → after,
    /// how many hidden categories the template adds, how many view parameters it
    /// controls and which ones overwrite local settings.
    /// </summary>
    private static void AddTemplateDelta(
        Document doc,
        View view,
        long templateId,
        WriteOutcome outcome)
    {
        try
        {
            if (doc.GetElement(ElementIdCompat.Create(templateId)) is not View template)
                return;

            var before = AgentViewSettingsReader.Read(doc, view);
            var after = AgentViewSettingsReader.Read(doc, template);
            var visibilityControlled =
                AgentViewSettingsReader.IsTemplateParameterControlled(
                    template, BuiltInParameter.VIS_GRAPHICS_MODEL)
                || AgentViewSettingsReader.IsTemplateParameterControlled(
                    template, BuiltInParameter.VIS_GRAPHICS_ANNOTATION);
            var lostFilters = AgentTemplateFilterImpact.FiltersLostOnAssignment(doc, view, template);
            var delta = new
            {
                viewId = view.Id.GetValue(),
                viewName = view.Name,
                templateId,
                templateName = template.Name,
                previousTemplate = before.TemplateName,
                scaleBefore = before.Scale,
                scaleAfter = after.Scale,
                hiddenCategoriesBefore = before.HiddenCategories.Count,
                hiddenCategoriesAfter = after.HiddenCategories.Count,
                hiddenCategoriesAdded = Math.Max(
                    0, after.HiddenCategories.Count - before.HiddenCategories.Count),
                controlledParameters = after.ControlledParameters.Count,
                overwritten = after.ControlledParameters,
                templateControlsFilters = AgentTemplateFilterImpact.ControlsFilters(template),
                ownFiltersReplaced = lostFilters,
                skippedByTemplateInclude = AgentViewSettingsReader
                    .ReadIncludeSkippedSections(template)
                    .ToList(),
                controls = new
                {
                    crop = AgentViewSettingsReader.IsTemplateParameterControlled(
                        template, BuiltInParameter.VIEWER_CROP_REGION),
                    visibility = visibilityControlled,
                    viewRange = AgentViewSettingsReader.IsTemplateParameterControlled(
                        template, BuiltInParameter.PLAN_VIEW_RANGE)
                }
            };

            if (outcome.Data["delta"] is not List<object> deltaList)
            {
                deltaList = new List<object>();
                outcome.Data["delta"] = deltaList;
            }

            deltaList.Add(delta);
        }
        catch (Exception)
        {
            // The delta is informational; it must never fail the dry run.
        }
    }

    internal static void ApplyChange(
        WriteOutcome outcome,
        View view,
        WriteItemReport report,
        AgentViewSettingsWriter.Change change,
        string key,
        bool atomic)
    {
        if (change.ErrorCode is not null)
        {
            if (change.SuggestedViewId.HasValue)
                report.SuggestedViewIds = new List<long> { change.SuggestedViewId.Value };
            Fail(outcome, report, change.ErrorCode, change.Reason);
            Abort(outcome, atomic);
            return;
        }

        if (!change.Changed)
        {
            report.Reason = change.Reason;
            outcome.Batch.Skipped.Add(report);
            return;
        }

        report.OldValue = change.OldValue;
        report.NewValue = change.NewValue;
        report.Detail = change.Detail;
        AgentViewSettingsWriter.PublishFilterReplacement(outcome, view, report, change);
        outcome.Batch.Changed.Add(report);
        outcome.AffectedCount++;

        var changeItem = change.ToChangeItem(view, key);
        if (changeItem is not null)
            outcome.Items.Add(changeItem);
    }

    internal static void Fail(
        WriteOutcome outcome,
        WriteItemReport report,
        string errorCode,
        string? reason)
    {
        report.ErrorCode = errorCode;
        report.Reason = reason;
        outcome.Batch.Failed.Add(report);
        // Per-item failure only. The batch result is decided by the base class:
        // all items failed -> failure, some applied -> success with failed[], so a
        // batch of 100 views with one bad id no longer reports "operation failed"
        // after changing 99 views.
        outcome.ErrorCode ??= errorCode;
    }

    internal static bool Abort(WriteOutcome outcome, bool atomic)
    {
        if (!atomic)
            return false;

        outcome.Aborted = true;
        outcome.Success = false;
        outcome.ErrorCode = "atomic_aborted";
        outcome.Message = "At least one item failed and atomic=true, so the whole batch " +
                          "was rolled back. Nothing was changed. failed: " +
                          DescribeFailedItems(outcome) +
                          ". Resend the batch WITHOUT the failed items, or drop atomic — " +
                          "repeating the same batch fails the same way.";
        return true;
    }

    private static string DescribeFailedItems(WriteOutcome outcome)
    {
        var parts = outcome.Batch.Failed
            .Take(10)
            .Select(item =>
                (item.Id?.ToString() ?? item.Name ?? "?") +
                (string.IsNullOrEmpty(item.ErrorCode) ? string.Empty : " " + item.ErrorCode))
            .ToList();
        var text = string.Join(", ", parts);
        if (outcome.Batch.Failed.Count > 10)
            text += " (×" + outcome.Batch.Failed.Count + ")";
        return text;
    }

    private sealed class ExpandedSelection
    {
        public List<ApplyTemplateItem> Items { get; } = new();

        public List<long> ExcludedViewIds { get; } = new();
    }

    private static ExpandedSelection? ExpandItems(
        Document doc,
        ApplyTemplateRequest? request,
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

        var explicitItems = request.Items ?? new List<ApplyTemplateItem>();
        var hasSelect = request.Select.HasValue
                        && request.Select.Value.ValueKind == JsonValueKind.Object;

        if (explicitItems.Count > 0 && hasSelect)
        {
            errorCode = "invalid_arguments";
            message = "Pass items or select, not both.";
            return null;
        }

        var expanded = new ExpandedSelection();
        if (explicitItems.Count > 0)
        {
            expanded.Items.AddRange(explicitItems);
        }
        else if (hasSelect)
        {
            if (!request.TemplateId.HasValue)
            {
                errorCode = "invalid_arguments";
                message = "select requires a top-level templateId (0 detaches the current template).";
                return null;
            }

            var detach = request.TemplateId.Value <= 0;

            var viewIds = AgentViewSelector.Resolve(
                doc, request.Select!.Value, out errorCode, out message);
            if (errorCode is not null)
                return null;

            expanded.Items.AddRange(viewIds.Select(viewId => new ApplyTemplateItem
            {
                ViewId = viewId,
                TemplateId = detach ? (long?)null : request.TemplateId.Value,
                OverwriteTemplate = request.OverwriteTemplate
            }));
        }
        else
        {
            errorCode = "invalid_arguments";
            message = "Pass items or a select filter with a top-level templateId.";
            return null;
        }

        ApplyExceptions(doc, expanded, request);
        if (expanded.Items.Count == 0)
        {
            errorCode = "no_items";
            message = "Every matched view was excluded by exceptViewIds/exceptNameContains; " +
                      "nothing to apply.";
            return null;
        }

        return expanded;
    }

    /// <summary>
    /// Subtracts the exception profile (exceptViewIds / exceptNameContains) from
    /// the resolved selection — simple set semantics, no guessing (#457: the
    /// model filtered exclusions by hand from read-back ids).
    /// </summary>
    private static void ApplyExceptions(
        Document doc,
        ExpandedSelection expanded,
        ApplyTemplateRequest request)
    {
        var exceptIds = request.ExceptViewIds;
        var exceptName = request.ExceptNameContains;
        var hasExceptIds = exceptIds is { Count: > 0 };
        var hasExceptName = !string.IsNullOrWhiteSpace(exceptName);
        if (!hasExceptIds && !hasExceptName)
            return;

        var excludedIds = hasExceptIds ? new HashSet<long>(exceptIds!) : null;
        for (var index = expanded.Items.Count - 1; index >= 0; index--)
        {
            var item = expanded.Items[index];
            var excluded = excludedIds is not null && excludedIds.Contains(item.ViewId);
            if (!excluded && hasExceptName
                && doc.GetElement(ElementIdCompat.Create(item.ViewId)) is View view
                && ListViewsTool.MatchesName(view, exceptName))
            {
                excluded = true;
            }

            if (!excluded)
                continue;

            expanded.Items.RemoveAt(index);
            expanded.ExcludedViewIds.Add(item.ViewId);
        }

        expanded.ExcludedViewIds.Sort();
    }

    /// <summary>
    /// Collects, per template, which Include-off sections every newly templated
    /// view keeps, and publishes the summary line(s) the model must see (#457:
    /// templates with a detached viewRange were applied and the views silently
    /// kept a default range while the tool reported success).
    /// </summary>
    private sealed class TemplateIncludeSkipReport
    {
        private readonly bool _dryRun;
        private readonly Dictionary<long, string> _templateNames = new();
        private readonly Dictionary<long, Dictionary<string, int>> _sectionCounts = new();

        public TemplateIncludeSkipReport(bool dryRun)
        {
            _dryRun = dryRun;
        }

        public void Record(Document doc, long templateId, WriteItemReport report)
        {
            if (doc.GetElement(ElementIdCompat.Create(templateId)) is not View template)
                return;

            var skipped = AgentViewSettingsReader.ReadIncludeSkippedSections(template);
            if (skipped.Count == 0)
                return;

            report.SkippedByTemplateInclude = new List<string>(skipped);
            _templateNames[templateId] = template.Name;
            if (!_sectionCounts.TryGetValue(templateId, out var sections))
            {
                sections = new Dictionary<string, int>();
                _sectionCounts[templateId] = sections;
            }

            foreach (var section in skipped)
                sections[section] = sections.TryGetValue(section, out var count)
                    ? count + 1
                    : 1;
        }

        public void Publish(WriteOutcome outcome)
        {
            if (_sectionCounts.Count == 0)
                return;

            var lines = new List<string>();
            foreach (var templateId in _sectionCounts.Keys.OrderBy(id => id))
            {
                var templateName = _templateNames[templateId];
                foreach (var pair in _sectionCounts[templateId].OrderBy(pair => pair.Key))
                {
                    lines.Add(
                        pair.Value + " view(s): " + pair.Key +
                        (_dryRun ? " would be left as-is" : " left as-is") +
                        " (Include off on template '" + templateName + "')");
                }
            }

            outcome.Data["templateIncludeSkips"] = lines;
        }
    }

    private sealed class ApplyTemplateRequest
    {
        public List<ApplyTemplateItem>? Items { get; set; }

        public JsonElement? Select { get; set; }

        public long? TemplateId { get; set; }

        public bool OverwriteTemplate { get; set; }

        public List<long>? ExceptViewIds { get; set; }

        public string? ExceptNameContains { get; set; }
    }
}

internal sealed class ApplyTemplateItem
{
    public long ViewId { get; set; }

    public long? TemplateId { get; set; }

    public bool OverwriteTemplate { get; set; }
}
