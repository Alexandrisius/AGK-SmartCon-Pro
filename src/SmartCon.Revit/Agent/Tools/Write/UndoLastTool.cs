using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Core.Threading;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Reverts the last applied change set of the active document (mvp-plan §9.4).
/// Only changes recorded by SmartCon are touched; items whose current value no
/// longer matches the recorded new value are reported as conflicts and skipped.
/// </summary>
public sealed class UndoLastTool : AgentWriteToolBase
{
    public UndoLastTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "undo_last",
        Description =
            "Reverts the last change set that SmartCon applied to the active document. " +
            "Items changed by somebody else after the operation are reported as conflicts " +
            "and skipped (pass force=true to restore them anyway). dryRun defaults to true " +
            "and shows what would be restored.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""changeSetId"":{""type"":""string""},""force"":{""type"":""boolean"",""default"":false},""dryRun"":{""type"":""boolean"",""default"":true}},""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var documentId = AgentDocumentIdentity.GetDocumentId(doc);
        var force = AgentJson.GetBool(args, "force", false);

        ChangeSetDetails? details;
        var requestedId = AgentJson.GetString(args, "changeSetId");
        if (!string.IsNullOrWhiteSpace(requestedId) && Guid.TryParse(requestedId, out var parsedId))
        {
            details = AsyncBridge.RunSync(() => ChangeSets.GetAsync(parsedId, ct));
            if (details is null)
            {
                outcome.Success = false;
                outcome.ErrorCode = "change_set_not_found";
                outcome.Message = "Change set " + requestedId + " was not found.";
                return;
            }
        }
        else
        {
            details = AsyncBridge.RunSync(() => ChangeSets.GetLastAppliedAsync(documentId, ct));
            if (details is null)
            {
                outcome.Success = false;
                outcome.ErrorCode = "nothing_to_undo";
                outcome.Message = "There is no applied change set for the active document.";
                return;
            }
        }

        if (!string.Equals(details.Header.DocumentId, documentId, StringComparison.Ordinal))
        {
            outcome.Success = false;
            outcome.ErrorCode = "document_mismatch";
            outcome.Message = "Change set " + details.Header.Id.ToString("N") +
                              " belongs to a different document.";
            return;
        }

        outcome.Description = "Undo of change set " + details.Header.Id.ToString("N") +
                              " (" + details.Header.Operation + ")";
        outcome.SuppressChangeSet = true;
        outcome.RollBackChangeSetId = details.Header.Id;

        for (var index = details.Items.Count - 1; index >= 0; index--)
        {
            ct.ThrowIfCancellationRequested();

            var item = details.Items[index];
            var report = new WriteItemReport
            {
                Id = item.ElementId,
                Name = item.ParameterName,
                OldValue = item.NewValue,
                NewValue = item.OldValue
            };

            var element = ResolveElement(doc, item);
            if (element is null)
            {
                report.ErrorCode = "element_not_found";
                report.Reason = "The element no longer exists in the document.";
                outcome.Batch.Failed.Add(report);
                continue;
            }

            var (ok, errorCode, reason) = RevertSafely(doc, element, item, force);
            if (ok)
            {
                outcome.Batch.Changed.Add(report);
                outcome.AffectedCount++;
            }
            else if (errorCode is null)
            {
                report.Reason = reason;
                outcome.Batch.Skipped.Add(report);
            }
            else
            {
                report.ErrorCode = errorCode;
                report.Reason = reason;
                outcome.Batch.Failed.Add(report);
            }
        }

        if (outcome.AffectedCount == 0)
        {
            outcome.RollBackChangeSetId = null;
            outcome.Success = false;
            outcome.ErrorCode = outcome.Batch.Failed.Count > 0 ? "undo_conflict" : "nothing_to_undo";
            outcome.Message = outcome.Batch.Failed.Count > 0
                ? "Nothing could be reverted: the recorded values were changed after the " +
                  "operation. Pass force=true to restore them anyway."
                : "Nothing was reverted; the model already matches the recorded previous state.";
        }
    }

    private static (bool Ok, string? ErrorCode, string? Reason) RevertSafely(
        Document doc,
        Element element,
        ChangeItem item,
        bool force)
    {
        try
        {
            return Revert(doc, element, item, force);
        }
        catch (Exception ex)
        {
            return (false, "revert_failed", ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static (bool Ok, string? ErrorCode, string? Reason) Revert(
        Document doc,
        Element element,
        ChangeItem item,
        bool force)
    {
        var key = item.ParameterName ?? string.Empty;

        if (key == "view_name")
            return RevertViewName(element as View, item, force);

        if (key == "level_name")
            return RevertLevelName(element as Level, item, force);

        if (key is "view_template" or "view_range" or "crop" or "visibility"
            or "viewRange" or "template")
        {
            if (element is not View view)
                return (false, "unsupported_element", "Element " + item.ElementId + " is not a view.");

            return key switch
            {
                "view_template" or "template" => RevertTemplate(doc, view, item, force),
                "view_range" or "viewRange" => RevertViewRange(view, item, force),
                "crop" => RevertCrop(view, item, force),
                _ => RevertVisibility(doc, view, item, force)
            };
        }

        if (key.StartsWith("instance:", StringComparison.Ordinal)
            || key.StartsWith("type:", StringComparison.Ordinal))
        {
            return RevertParameter(doc, element, item, force);
        }

        return (false, null, "Unknown change item type '" + key + "' — skipped.");
    }

    private static (bool, string?, string?) RevertViewName(View? view, ChangeItem item, bool force)
    {
        if (view is null)
            return (false, "unsupported_element", "Element " + item.ElementId + " is not a view.");

        var target = item.OldValue ?? string.Empty;
        if (!force && !string.Equals(view.Name, item.NewValue, StringComparison.Ordinal))
        {
            return (false, "undo_conflict",
                "The view is named '" + view.Name + "' instead of '" + item.NewValue +
                "' — it was changed after the operation (conflict).");
        }

        view.Name = target;
        return (true, null, null);
    }

    private static (bool, string?, string?) RevertLevelName(Level? level, ChangeItem item, bool force)
    {
        if (level is null)
            return (false, "unsupported_element", "Element " + item.ElementId + " is not a level.");

        var target = item.OldValue ?? string.Empty;
        if (!force && !string.Equals(level.Name, item.NewValue, StringComparison.Ordinal))
        {
            return (false, "undo_conflict",
                "The level is named '" + level.Name + "' instead of '" + item.NewValue +
                "' — it was changed after the operation (conflict).");
        }

        level.Name = target;
        return (true, null, null);
    }

    private static (bool, string?, string?) RevertTemplate(
        Document doc,
        View view,
        ChangeItem item,
        bool force)
    {
        var current = view.ViewTemplateId;
        var expected = item.NewValue;
        if (!force && !string.Equals(IdText(current), expected, StringComparison.Ordinal))
        {
            return (false, "undo_conflict",
                "The template of '" + view.Name + "' changed after the operation (conflict).");
        }

        if (string.IsNullOrWhiteSpace(item.OldValue))
        {
            view.ViewTemplateId = ElementId.InvalidElementId;
            return (true, null, null);
        }

        if (!long.TryParse(item.OldValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            return (false, "invalid_change_item", "The recorded template id is not a number.");

        if (doc.GetElement(ElementIdCompat.Create(id)) is not View template || !template.IsTemplate)
        {
            return (false, "template_not_found",
                "The previous template " + id + " no longer exists in the document.");
        }

        view.ViewTemplateId = ElementIdCompat.Create(id);
        return (true, null, null);
    }

    private static (bool, string?, string?) RevertViewRange(View view, ChangeItem item, bool force)
    {
        if (view is not ViewPlan plan)
            return (false, "unsupported_view_type", "View range applies to plan views only.");

        var spec = AgentViewWriteHelper.Deserialize<ViewRangeSpec>(item.OldValue);
        if (spec is null || spec.IsEmpty)
            return (false, null, "The recorded view range is empty — nothing to restore.");

        if (!force)
        {
            var current = AgentViewWriteHelper.Serialize(
                AgentViewWriteHelper.ReadViewRange(view) ?? new ViewRangeSpec());
            if (!string.Equals(current, item.NewValue, StringComparison.Ordinal))
            {
                return (false, "undo_conflict",
                    "The view range of '" + view.Name +
                    "' changed after the operation (conflict).");
            }
        }

        AgentViewWriteHelper.ApplyViewRange(plan, spec);
        return (true, null, null);
    }

    private static (bool, string?, string?) RevertCrop(View view, ChangeItem item, bool force)
    {
        var state = AgentViewWriteHelper.Deserialize<ViewCropState>(item.OldValue);
        if (state is null)
            return (false, null, "The recorded crop state is empty — nothing to restore.");

        if (!force)
        {
            var current = AgentViewWriteHelper.Serialize(AgentViewWriteHelper.ReadCropState(view));
            if (!string.Equals(current, item.NewValue, StringComparison.Ordinal))
            {
                return (false, "undo_conflict",
                    "The crop region of '" + view.Name + "' changed after the operation (conflict).");
            }
        }

        var warnings = new List<string>();
        AgentViewWriteHelper.ApplyCropState(view, state, warnings);
        return (true, null, null);
    }

    private static (bool, string?, string?) RevertVisibility(
        Document doc,
        View view,
        ChangeItem item,
        bool force)
    {
        var state = AgentViewWriteHelper.Deserialize<ViewVisibilityState>(item.OldValue);
        if (state is null)
            return (false, null, "The recorded visibility state is empty — nothing to restore.");

        if (!force)
        {
            var current = AgentViewWriteHelper.Serialize(
                AgentViewWriteHelper.ReadVisibility(doc, view));
            if (!string.Equals(current, item.NewValue, StringComparison.Ordinal))
            {
                return (false, "undo_conflict",
                    "Visibility of '" + view.Name + "' changed after the operation (conflict).");
            }
        }

        AgentViewWriteHelper.ApplyVisibilityState(doc, view, state);
        return (true, null, null);
    }

    private static (bool, string?, string?) RevertParameter(
        Document doc,
        Element element,
        ChangeItem item,
        bool force)
    {
        var key = item.ParameterName!;
        var typeScope = key.StartsWith("type:", StringComparison.Ordinal);
        var name = key.Substring(key.IndexOf(':') + 1);

        var resolution = AgentParameterWriteHelper.Resolve(
            doc,
            element,
            name,
            null,
            typeScope ? "type" : "instance");

        if (!resolution.Ok)
            return (false, resolution.ErrorCode, resolution.Reason);

        var parameter = resolution.Parameter!;
        if (!force)
        {
            var current = AgentParameterWriteHelper.RawValue(parameter);
            if (!string.Equals(current, item.NewValue, StringComparison.Ordinal))
            {
                return (false, "undo_conflict",
                    "Parameter '" + name + "' has the value '" + current +
                    "' instead of '" + item.NewValue + "' — it was changed after the " +
                    "operation (conflict).");
            }
        }

        if (!AgentParameterWriteHelper.TrySet(
                parameter, item.OldValue, "raw", out var errorCode, out var reason))
        {
            return (false, errorCode, reason);
        }

        return (true, null, null);
    }

    private static Element? ResolveElement(Document doc, ChangeItem item)
    {
        if (!string.IsNullOrEmpty(item.UniqueId))
        {
            var byUniqueId = doc.GetElement(item.UniqueId);
            if (byUniqueId is not null)
                return byUniqueId;
        }

        return item.ElementId.HasValue
            ? doc.GetElement(ElementIdCompat.Create(item.ElementId.Value))
            : null;
    }

    private static string IdText(ElementId id) => id == ElementId.InvalidElementId
        ? string.Empty
        : id.GetValue().ToString(CultureInfo.InvariantCulture);
}
