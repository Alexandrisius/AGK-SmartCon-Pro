using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Query;
using SmartCon.Revit.Compatibility;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Assigns a scope box to views or clears it (#351). The scope box of a view is
/// the VIEWER_VOLUME_OF_INTEREST_CROP parameter (ElementId); -1 means none.
/// Scope boxes cannot be created through the API, so the box is referenced by
/// scopeBoxId (list_scope_boxes) or scopeBoxName; clear=true detaches it. When
/// a scope box drives the view, its crop follows the box — writing the crop
/// has no effect (see the view_settings skill).
/// </summary>
public sealed class AssignScopeBoxTool : AgentWriteToolBase
{
    private const int MaxViews = 200;

    public AssignScopeBoxTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "assign_scope_box",
        Description =
            "Assigns a scope box to views (or clears it): writes the Scope Box parameter " +
            "(VIEWER_VOLUME_OF_INTEREST_CROP) of every target view. Pass exactly one of " +
            "scopeBoxId (from list_scope_boxes), scopeBoxName, or clear=true (removes the " +
            "scope box from the views). Views are given by viewIds or viewSelect (the same " +
            "select filter as apply_view_template). While a scope box is assigned, it drives " +
            "the view crop — set_view_crop on such a view has no effect. Guards: " +
            "scope_box_not_found, unsupported_view_type (the view has no Scope Box " +
            "parameter — legends, schedules, sheets), view_controlled_by_template. " +
            "undo_last restores the previous assignment. Dry run by default.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""viewIds"":{""type"":""array"",""minItems"":1,""maxItems"":200,""items"":{""type"":""integer""}},""viewSelect"":{""type"":""object"",""properties"":{""type"":{""type"":""string""},""viewType"":{""type"":""string""},""levelId"":{""type"":""integer""},""folder"":{""type"":""string""},""nameContains"":{""type"":""string""},""templateId"":{""type"":""integer""}},""additionalProperties"":false},""scopeBoxId"":{""type"":""integer""},""scopeBoxName"":{""type"":""string""},""clear"":{""type"":""boolean"",""description"":""true removes the scope box from the views""},""dryRun"":{""type"":""boolean"",""default"":true},""atomic"":{""type"":""boolean"",""default"":false}},""additionalProperties"":false}"
    };

    protected override PlanPreflightResult PreflightCore(
        Document document,
        JsonElement args,
        CancellationToken ct)
    {
        var basic = base.PreflightCore(document, args, ct);
        if (!basic.Ok)
            return basic;

        var (mode, scopeBoxId) = ResolveMode(args);
        if (mode == Mode.Invalid)
            return PlanPreflightResult.Fail("invalid_arguments", ModeErrorMessage());

        if (mode == Mode.ById
            && !IsScopeBox(document, ElementIdCompat.Create(scopeBoxId)))
        {
            return PlanPreflightResult.Fail(
                "scope_box_not_found",
                "Element " + scopeBoxId + " is not a scope box (list_scope_boxes).");
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
        var (mode, scopeBoxId) = ResolveMode(args);
        if (mode == Mode.Invalid)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = ModeErrorMessage();
            return;
        }

        var scopeBoxName = AgentJson.GetString(args, "scopeBoxName");
        if (mode == Mode.ByName)
        {
            scopeBoxId = FindByName(doc, scopeBoxName!, out var notFound);
            if (notFound)
            {
                outcome.Success = false;
                outcome.ErrorCode = "scope_box_not_found";
                outcome.Message = "No scope box named '" + scopeBoxName + "' exists in the " +
                                  "document (list_scope_boxes shows the available names).";
                return;
            }
        }
        else if (mode == Mode.ById && !IsScopeBox(doc, ElementIdCompat.Create(scopeBoxId)))
        {
            outcome.Success = false;
            outcome.ErrorCode = "scope_box_not_found";
            outcome.Message = "Element " + scopeBoxId + " is not a scope box (list_scope_boxes).";
            return;
        }

        var target = mode == Mode.Clear
            ? ElementId.InvalidElementId
            : ElementIdCompat.Create(scopeBoxId);
        var targetName = mode == Mode.Clear
            ? null
            : doc.GetElement(target)?.Name;

        var viewIds = ResolveViews(doc, args, out var viewsCode, out var viewsMessage);
        if (viewIds is null)
        {
            outcome.Success = false;
            outcome.ErrorCode = viewsCode;
            outcome.Message = viewsMessage;
            return;
        }

        if (args.TryGetProperty("viewSelect", out var select)
            && select.ValueKind == JsonValueKind.Object)
        {
            outcome.Data["selectedViewIds"] = viewIds;
            outcome.Data["selectorMatched"] = viewIds.Count;
        }

        var atomic = AgentJson.GetBool(args, "atomic", false);
        outcome.Description = (mode == Mode.Clear ? "Cleared scope box on " :
            "Assigned scope box '" + targetName + "' to ") + viewIds.Count + " view(s)";

        foreach (var viewId in viewIds)
        {
            ct.ThrowIfCancellationRequested();

            var report = new WriteItemReport { Id = viewId };
            if (!AgentViewWriteHelper.TryGetView(
                    doc, viewId, out var view, out var code, out var reason))
            {
                ApplyViewTemplateTool.Fail(outcome, report, code, reason);
                if (ApplyViewTemplateTool.Abort(outcome, atomic)) return;
                continue;
            }

            if (view.IsTemplate)
            {
                ApplyViewTemplateTool.Fail(outcome, report, "view_is_template",
                    "View '" + view.Name + "' is a view template; scope boxes apply to views.");
                if (ApplyViewTemplateTool.Abort(outcome, atomic)) return;
                continue;
            }

            var parameter = view.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
            if (parameter is null)
            {
                ApplyViewTemplateTool.Fail(outcome, report, "unsupported_view_type",
                    "View '" + view.Name + "' (" + view.ViewType + ") has no Scope Box parameter.");
                if (ApplyViewTemplateTool.Abort(outcome, atomic)) return;
                continue;
            }

            if (AgentViewSettingsReader.IsParameterLockedByTemplate(
                    view, BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP,
                    out var templateId, out var templateName))
            {
                report.Name = view.Name;
                report.SuggestedViewIds = templateId.HasValue
                    ? new List<long> { templateId.Value }
                    : null;
                ApplyViewTemplateTool.Fail(outcome, report, "view_controlled_by_template",
                    "The Scope Box of '" + view.Name + "' is controlled by view template '" +
                    templateName + "'. Write it in the template or remove the template from " +
                    "the view (apply_view_template items[{viewId:" + viewId +
                    ", templateId:0}]) first.");
                if (ApplyViewTemplateTool.Abort(outcome, atomic)) return;
                continue;
            }

            report.Name = view.Name;
            var current = parameter.AsElementId();
            var oldValue = DescribeId(current);
            var newValue = DescribeId(target);

            if (current is not null && current.GetValue() == target.GetValue())
            {
                report.Reason = mode == Mode.Clear
                    ? "The view has no scope box (no-op)."
                    : "The view already uses scope box '" + targetName + "' (no-op).";
                outcome.Batch.Skipped.Add(report);
                continue;
            }

            if (dryRun)
            {
                report.OldValue = oldValue;
                report.NewValue = newValue;
                report.Reason = "Dry run: the scope box would become '" + newValue + "'.";
                outcome.Batch.Changed.Add(report);
                continue;
            }

            try
            {
                if (!parameter.Set(target))
                {
                    ApplyViewTemplateTool.Fail(outcome, report, "scope_box_not_applied",
                        "Revit refused the scope box '" + newValue + "' on '" + view.Name + "'.");
                    if (ApplyViewTemplateTool.Abort(outcome, atomic)) return;
                    continue;
                }
            }
            catch (Exception ex)
            {
                ApplyViewTemplateTool.Fail(outcome, report, "scope_box_not_applied",
                    ex.GetType().Name + ": " + ex.Message);
                if (ApplyViewTemplateTool.Abort(outcome, atomic)) return;
                continue;
            }

            var appliedId = view.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP)
                ?.AsElementId();
            if (appliedId is null || appliedId.GetValue() != target.GetValue())
            {
                ApplyViewTemplateTool.Fail(outcome, report, "scope_box_not_applied",
                    "The scope box of '" + view.Name + "' did not change after the write " +
                    "(Revit silently ignored it).");
                if (ApplyViewTemplateTool.Abort(outcome, atomic)) return;
                continue;
            }

            report.OldValue = oldValue;
            report.NewValue = newValue;
            outcome.Batch.Changed.Add(report);
            outcome.AffectedCount++;

            outcome.Items.Add(new ChangeItem
            {
                UniqueId = view.UniqueId,
                ElementId = view.Id.GetValue(),
                ParameterName = "scope_box",
                OldValue = oldValue,
                NewValue = newValue,
                Status = ChangeStatus.Applied
            });
        }
    }

    private enum Mode
    {
        Invalid,
        ById,
        ByName,
        Clear
    }

    private static (Mode Mode, long ScopeBoxId) ResolveMode(JsonElement args)
    {
        long id = 0;
        var hasId = args.TryGetProperty("scopeBoxId", out var idNode)
                    && idNode.ValueKind == JsonValueKind.Number
                    && idNode.TryGetInt64(out id) && id > 0;
        var hasName = args.TryGetProperty("scopeBoxName", out var nameNode)
                      && nameNode.ValueKind == JsonValueKind.String
                      && !string.IsNullOrWhiteSpace(nameNode.GetString());
        var hasClear = args.TryGetProperty("clear", out var clearNode)
                       && clearNode.ValueKind == JsonValueKind.True;

        var flags = (hasId ? 1 : 0) + (hasName ? 1 : 0) + (hasClear ? 1 : 0);
        if (flags != 1)
            return (Mode.Invalid, 0);

        if (hasId)
            return (Mode.ById, id);
        if (hasClear)
            return (Mode.Clear, 0);
        return (Mode.ByName, 0);
    }

    private static string ModeErrorMessage() =>
        "Pass exactly one of scopeBoxId, scopeBoxName or clear=true.";

    private static long FindByName(Document doc, string name, out bool notFound)
    {
        notFound = false;
        foreach (var element in new FilteredElementCollector(doc)
                     .OfCategory(BuiltInCategory.OST_VolumeOfInterest)
                     .WhereElementIsNotElementType())
        {
            if (string.Equals(element.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
                return element.Id.GetValue();
        }

        notFound = true;
        return 0;
    }

    private static bool IsScopeBox(Document doc, ElementId id) =>
        doc.GetElement(id) is { } element
        && element.Category is not null
        && CategoryCompat.GetBuiltInCategory(element.Category)
            == BuiltInCategory.OST_VolumeOfInterest;

    private static List<long>? ResolveViews(
        Document doc,
        JsonElement args,
        out string? errorCode,
        out string? message)
    {
        errorCode = null;
        message = null;

        var viewIds = AgentJson.GetLongArray(args, "viewIds");
        var hasSelect = args.TryGetProperty("viewSelect", out var select)
                        && select.ValueKind == JsonValueKind.Object;

        if (viewIds.Count > 0 && hasSelect)
        {
            errorCode = "invalid_arguments";
            message = "Pass viewIds or viewSelect, not both.";
            return null;
        }

        if (viewIds.Count > 0)
            return viewIds.Distinct().ToList();

        if (!hasSelect)
        {
            errorCode = "invalid_arguments";
            message = "Pass viewIds (1.." + MaxViews + ") or a viewSelect filter.";
            return null;
        }

        var resolved = AgentViewSelector.Resolve(doc, select, out errorCode, out message);
        return errorCode is not null ? null : resolved.ToList();
    }

    internal static string DescribeId(ElementId? id) =>
        id is null || id == ElementId.InvalidElementId
            ? string.Empty
            : id.GetValue().ToString();
}
