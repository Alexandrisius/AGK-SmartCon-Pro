using Autodesk.Revit.DB;
using RevitFamily = Autodesk.Revit.DB.Family;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Revit.Agent.Tools.Families;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// G.4 family type operations of undo_last: a duplicated type is deleted, a
/// renamed type gets its previous name back, a loaded family is deleted, an
/// overwrite reload is honestly refused, and elements switched by
/// replace_family_type return to their previous types grouped by old type
/// (one static ChangeTypeId call per group).
/// </summary>
public sealed partial class UndoLastTool
{
    private static (bool, string?, string?) RevertFamilyElementSafe(
        Document doc,
        Element? element,
        ChangeItem item,
        string key,
        bool force)
    {
        try
        {
            return key switch
            {
                "family_type_created" => RevertCreatedType(doc, element, item, force),
                "family_type_name" => RevertTypeName(element, item, force),
                "family_loaded" => RevertLoadedFamily(doc, element, item, force),
                _ => (false, null,
                    "undo_not_supported: an overwrite reload cannot be reverted by " +
                    "undo_last; reload the previous family file with load_family.")
            };
        }
        catch (Exception ex)
        {
            return (false, "revert_failed", ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static (bool, string?, string?) RevertCreatedType(
        Document doc,
        Element? element,
        ChangeItem item,
        bool force)
    {
        if (element is null)
            return (false, null, "The type no longer exists — already removed.");

        if (element is not ElementType type)
            return (false, "unsupported_element",
                "Element " + item.ElementId + " is not a type.");

        if (!force)
        {
            var instances = AgentFamilyTypeResolver.CountInstances(doc, type.Id);
            if (instances > 0)
            {
                return (false, "undo_conflict",
                    "The type '" + type.Name + "' has " + instances +
                    " placed instance(s); deleting it would delete them too. Pass " +
                    "force=true to delete anyway.");
            }
        }

        doc.Delete(type.Id);
        return (true, null, null);
    }

    private static (bool, string?, string?) RevertTypeName(
        Element? element,
        ChangeItem item,
        bool force)
    {
        if (element is null)
            return (false, "element_not_found", "The type no longer exists in the document.");

        if (element is not ElementType type)
            return (false, "unsupported_element",
                "Element " + item.ElementId + " is not a type.");

        if (string.Equals(type.Name, item.OldValue, StringComparison.Ordinal))
            return (false, null, "The type already has the previous name (no-op).");

        if (!force && !string.Equals(type.Name, item.NewValue, StringComparison.Ordinal))
        {
            return (false, "undo_conflict",
                "The type is named '" + type.Name + "' instead of '" + item.NewValue +
                "' — it was renamed after the operation (conflict).");
        }

        if (string.IsNullOrWhiteSpace(item.OldValue))
            return (false, "invalid_change_item", "The recorded previous name is empty.");

        type.Name = item.OldValue!;
        return (true, null, null);
    }

    private static (bool, string?, string?) RevertLoadedFamily(
        Document doc,
        Element? element,
        ChangeItem item,
        bool force)
    {
        if (element is null)
            return (false, null, "The family no longer exists — already removed.");

        if (element is not RevitFamily family)
            return (false, "unsupported_element",
                "Element " + item.ElementId + " is not a family.");

        if (!force)
        {
            var instances = 0;
            foreach (var symbolId in family.GetFamilySymbolIds())
                instances += AgentFamilyTypeResolver.CountInstances(doc, symbolId);

            if (instances > 0)
            {
                return (false, "undo_conflict",
                    "The family '" + family.Name + "' has " + instances +
                    " placed instance(s); deleting it would delete them too. Pass " +
                    "force=true to delete anyway.");
            }
        }

        doc.Delete(family.Id);
        return (true, null, null);
    }

    private static void RevertElementTypes(
        Document doc,
        List<(ChangeItem Item, Element? Element)> pairs,
        WriteOutcome outcome,
        bool force)
    {
        var groups = new Dictionary<long, List<(ChangeItem Item, Element Element)>>();
        foreach (var (item, element) in pairs)
        {
            var report = CreateReport(item);
            if (element is null)
            {
                report.ErrorCode = "element_not_found";
                report.Reason = "The element no longer exists in the document.";
                outcome.Batch.Failed.Add(report);
                continue;
            }

            if (!long.TryParse(
                    item.OldValue,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var oldTypeId))
            {
                report.ErrorCode = "invalid_change_item";
                report.Reason = "The recorded previous type id is not a number.";
                outcome.Batch.Failed.Add(report);
                continue;
            }

            var currentTypeId = element.GetTypeId();
            if (currentTypeId.GetValue() == oldTypeId)
            {
                report.Reason = "The element already uses the previous type (no-op).";
                outcome.Batch.Skipped.Add(report);
                continue;
            }

            if (!force
                && (item.NewValue is null
                    || currentTypeId.GetValue().ToString(
                        System.Globalization.CultureInfo.InvariantCulture)
                    != item.NewValue))
            {
                report.ErrorCode = "undo_conflict";
                report.Reason = "The element uses type " + currentTypeId.GetValue() +
                                " instead of '" + item.NewValue +
                                "' — it was changed after the operation (conflict).";
                outcome.Batch.Failed.Add(report);
                continue;
            }

            var oldType = doc.GetElement(ElementIdCompat.Create(oldTypeId));
            if (oldType is null)
            {
                report.ErrorCode = "type_not_found";
                report.Reason = "The previous type " + oldTypeId +
                                " no longer exists in the document.";
                outcome.Batch.Failed.Add(report);
                continue;
            }

            if (!groups.TryGetValue(oldTypeId, out var group))
            {
                group = new List<(ChangeItem, Element)>();
                groups.Add(oldTypeId, group);
            }

            group.Add((item, element));
        }

        foreach (var pair in groups)
        {
            var oldTypeId = pair.Key;
            var group = pair.Value;
            var ids = new List<ElementId>(group.Count);
            foreach (var entry in group)
                ids.Add(entry.Element.Id);

            try
            {
                Element.ChangeTypeId(doc, ids, ElementIdCompat.Create(oldTypeId));
                foreach (var entry in group)
                {
                    outcome.Batch.Changed.Add(CreateReport(entry.Item));
                    outcome.AffectedCount++;
                }
            }
            catch (Exception ex)
            {
                foreach (var entry in group)
                {
                    var report = CreateReport(entry.Item);
                    report.ErrorCode = "revert_failed";
                    report.Reason = "Revit refused to restore the previous type: " + ex.Message;
                    outcome.Batch.Failed.Add(report);
                }
            }
        }
    }
}
