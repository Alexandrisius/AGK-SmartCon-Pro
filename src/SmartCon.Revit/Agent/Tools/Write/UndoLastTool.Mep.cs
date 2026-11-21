using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Undo of the MEP system type change item: the name and abbreviation
/// properties recorded in the change item snapshot are restored.
/// </summary>
public sealed partial class UndoLastTool
{
    private static void RecordMepSystemTypeRevert(
        Document doc,
        WriteOutcome outcome,
        ChangeItem item,
        bool force)
    {
        var report = CreateReport(item);
        var element = ResolveElement(doc, item);
        if (element is null)
        {
            report.ErrorCode = "element_not_found";
            report.Reason = "The system type no longer exists in the document.";
            outcome.Batch.Failed.Add(report);
            return;
        }

        if (element is not MEPSystemType type)
        {
            report.ErrorCode = "unsupported_element";
            report.Reason = "Element " + item.ElementId + " is not an MEP system type.";
            outcome.Batch.Failed.Add(report);
            return;
        }

        var (reverted, revertError, revertReason) = RevertMepSystemType(type, item, force);
        if (reverted)
        {
            outcome.Batch.Changed.Add(report);
            outcome.AffectedCount++;
        }
        else if (revertError is null)
        {
            report.Reason = revertReason;
            outcome.Batch.Skipped.Add(report);
        }
        else
        {
            report.ErrorCode = revertError;
            report.Reason = revertReason;
            outcome.Batch.Failed.Add(report);
        }
    }

    private static (bool, string?, string?) RevertMepSystemType(
        MEPSystemType type,
        ChangeItem item,
        bool force)
    {
        var oldValues = DeserializeMepSystemTypeValues(item.OldValue);
        var newValues = DeserializeMepSystemTypeValues(item.NewValue);
        if (oldValues is null || newValues is null)
            return (false, "invalid_change_item",
                "The recorded system type values are not a JSON {name, abbreviation} object.");

        var currentName = type.Name ?? string.Empty;
        var currentAbbreviation = type.Abbreviation ?? string.Empty;
        if (string.Equals(currentName, oldValues.Name, StringComparison.Ordinal)
            && string.Equals(currentAbbreviation, oldValues.Abbreviation, StringComparison.Ordinal))
            return (false, null, "The system type already has the previous values (no-op).");

        if (!force
            && (currentName != newValues.Name || currentAbbreviation != newValues.Abbreviation))
        {
            return (false, "undo_conflict",
                "The system type was changed after the operation (conflict).");
        }

        try
        {
            if (currentName != oldValues.Name)
                type.Name = oldValues.Name;

            if (currentAbbreviation != oldValues.Abbreviation)
                type.Abbreviation = oldValues.Abbreviation;

            return (true, null, null);
        }
        catch (Exception ex)
        {
            return (false, "revert_failed", ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static SetMepSystemTypeTool.MepSystemTypeRecord? DeserializeMepSystemTypeValues(
        string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<SetMepSystemTypeTool.MepSystemTypeRecord>(json!);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
