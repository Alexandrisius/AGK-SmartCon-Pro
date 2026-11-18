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
            "overwriteTemplate?}]. A view that already has a different template is skipped " +
            "unless overwriteTemplate=true. The template must belong to the same view family. " +
            "dryRun defaults to true.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""items"":{""type"":""array"",""minItems"":1,""maxItems"":200,""items"":{""type"":""object"",""properties"":{""viewId"":{""type"":""integer""},""templateId"":{""type"":""integer""},""overwriteTemplate"":{""type"":""boolean"",""default"":false}},""required"":[""viewId"",""templateId""],""additionalProperties"":false}},""dryRun"":{""type"":""boolean"",""default"":true},""atomic"":{""type"":""boolean"",""default"":false}},""required"":[""items""],""additionalProperties"":false}"
    };

    protected override PlanPreflightResult PreflightCore(
        Document doc,
        JsonElement args,
        CancellationToken ct)
    {
        var basic = base.PreflightCore(doc, args, ct);
        if (!basic.Ok)
            return basic;

        var request = AgentJson.Deserialize<ApplyTemplateRequest>(args);
        if (request?.Items is null || request.Items.Count == 0)
            return PlanPreflightResult.Fail("no_items", "The items array is empty.");

        foreach (var item in request.Items)
        {
            ct.ThrowIfCancellationRequested();

            if (doc.GetElement(ElementIdCompat.Create(item.ViewId)) is not View view)
                return PlanPreflightResult.Fail(
                    "unresolved_id", "View " + item.ViewId + " does not exist.");

            if (doc.GetElement(ElementIdCompat.Create(item.TemplateId)) is not View template
                || !template.IsTemplate)
            {
                return PlanPreflightResult.Fail(
                    "invalid_template",
                    "Element " + item.TemplateId + " is not a view template.");
            }

            if (!AgentViewWriteHelper.AreFamiliesCompatible(doc, view, template))
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
        var items = request?.Items;
        if (items is null || items.Count == 0)
        {
            outcome.Success = false;
            outcome.ErrorCode = "no_items";
            outcome.Message = "The items array is empty; pass at least one {viewId,templateId} pair.";
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
            if (dryRun)
                AddTemplateDelta(doc, view, item.TemplateId, outcome);

            var change = AgentViewSettingsWriter.ApplyTemplate(
                doc, view, item.TemplateId, item.OverwriteTemplate);

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
                overwritten = after.ControlledParameters
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

    private sealed class ApplyTemplateRequest
    {
        public List<ApplyTemplateItem>? Items { get; set; }
    }
}

internal sealed class ApplyTemplateItem
{
    public long ViewId { get; set; }

    public long TemplateId { get; set; }

    public bool OverwriteTemplate { get; set; }
}
