using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Query;

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
            "overwriteTemplate?}] or select: {type?, levelId?, folder?, nameContains?, templateId?} " +
            "with a top-level templateId (select resolves the view set for you). templateId=0 " +
            "(or null) detaches the current template from the views. A view that " +
            "already has a different template is skipped unless overwriteTemplate=true. The " +
            "template must belong to the same view family. dryRun defaults to true.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""items"":{""type"":""array"",""maxItems"":200,""items"":{""type"":""object"",""properties"":{""viewId"":{""type"":""integer""},""templateId"":{""type"":[""integer"",""null""],""description"":""0 or null detaches the current template""},""overwriteTemplate"":{""type"":""boolean"",""default"":false}},""required"":[""viewId"",""templateId""],""additionalProperties"":false}},""select"":{""type"":""object"",""properties"":{""type"":{""type"":""string""},""viewType"":{""type"":""string""},""levelId"":{""type"":""integer""},""folder"":{""type"":""string""},""nameContains"":{""type"":""string""},""templateId"":{""type"":""integer""}},""additionalProperties"":false},""templateId"":{""type"":[""integer"",""null""],""description"":""0 or null detaches the current template""},""overwriteTemplate"":{""type"":""boolean"",""default"":false},""dryRun"":{""type"":""boolean"",""default"":true},""atomic"":{""type"":""boolean"",""default"":false}},""additionalProperties"":false}"
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
        var items = ExpandItems(document, request, out var itemsError, out var itemsMessage);
        if (items is null)
        {
            return PlanPreflightResult.Fail(
                itemsError ?? "invalid_arguments",
                itemsMessage ?? "Invalid items.");
        }

        foreach (var item in items)
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
        outcome.Description = "Applied view template to " + items.Count + " view(s)";

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
            if (dryRun && !detach)
                AddTemplateDelta(doc, view, item.TemplateId!.Value, outcome);

            var change = detach
                ? AgentViewSettingsWriter.DetachTemplate(view)
                : AgentViewSettingsWriter.ApplyTemplate(
                    doc, view, item.TemplateId!.Value, item.OverwriteTemplate);

            ApplyChange(outcome, view, report, change, "view_template", atomic);
            if (outcome.Aborted)
                return;
        }
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
        outcome.Success = false;
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
                          "was rolled back. Nothing was changed.";
        return true;
    }

    private static List<ApplyTemplateItem>? ExpandItems(
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

        if (explicitItems.Count > 0)
            return explicitItems;

        if (!hasSelect)
        {
            errorCode = "invalid_arguments";
            message = "Pass items or a select filter with a top-level templateId.";
            return null;
        }

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

        return viewIds
            .Select(viewId => new ApplyTemplateItem
            {
                ViewId = viewId,
                TemplateId = detach ? (long?)null : request.TemplateId.Value,
                OverwriteTemplate = request.OverwriteTemplate
            })
            .ToList();
    }

    private sealed class ApplyTemplateRequest
    {
        public List<ApplyTemplateItem>? Items { get; set; }

        public JsonElement? Select { get; set; }

        public long? TemplateId { get; set; }

        public bool OverwriteTemplate { get; set; }
    }
}

internal sealed class ApplyTemplateItem
{
    public long ViewId { get; set; }

    public long? TemplateId { get; set; }

    public bool OverwriteTemplate { get; set; }
}
