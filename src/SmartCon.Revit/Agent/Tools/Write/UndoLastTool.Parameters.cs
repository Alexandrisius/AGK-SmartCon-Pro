using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Revit.Agent.Tools.Project;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Undo of project parameter bindings (add_project_shared_parameter): a
/// binding that did not exist before is removed with ParameterBindings.Remove,
/// an extended binding is re-bound to the recorded previous category set. A
/// definition created in the shared parameter file is an external file change
/// and stays there.
/// </summary>
public sealed partial class UndoLastTool
{
    private static (bool Ok, string? ErrorCode, string? Reason) RevertProjectParameterBinding(
        Document doc,
        ChangeItem item,
        bool force)
    {
        var recorded = AgentProjectParameterReader.TryParseSnapshot(item.NewValue);
        if (recorded is null || string.IsNullOrEmpty(recorded.Name))
            return (false, "invalid_change_item",
                "The recorded binding state of the project parameter is missing or malformed.");

        if (!AgentProjectParameterReader.TryGetMapping(
                doc,
                item.ElementId ?? 0,
                recorded.Name,
                out var definition,
                out var binding))
            return (false, null,
                "The parameter '" + recorded.Name +
                "' is no longer bound in the document — already restored.");

        var currentType = binding is InstanceBinding ? "instance" : "type";
        var currentIds = new HashSet<long>();
        foreach (Category category in binding!.Categories)
            currentIds.Add(category.Id.GetValue());

        var old = AgentProjectParameterReader.TryParseSnapshot(item.OldValue);
        if (old is not null
            && string.Equals(currentType, old.BindingType, StringComparison.Ordinal)
            && currentIds.SetEquals(old.CategoryIds))
            return (false, null, "The binding already matches the recorded previous state.");

        if (!force
            && (!string.Equals(currentType, recorded.BindingType, StringComparison.Ordinal)
                || !currentIds.SetEquals(recorded.CategoryIds)))
            return (false, "undo_conflict",
                "The binding of '" + recorded.Name +
                "' changed after the operation (conflict). Pass force=true to restore it anyway.");

        try
        {
            if (old is null)
                return doc.ParameterBindings.Remove(definition!)
                    ? (true, null, null)
                    : (false, "revert_failed",
                        "Revit refused to remove the parameter binding of '" + recorded.Name +
                        "'.");

            var app = doc.Application;
            var categorySet = app.Create.NewCategorySet();
            foreach (var categoryId in old.CategoryIds)
            {
                Category? category;
                try
                {
                    category = Category.GetCategory(doc, ElementIdCompat.Create(categoryId));
                }
                catch (Exception)
                {
                    category = null;
                }

                if (category is null)
                    return (false, "invalid_change_item",
                        "The recorded category " + categoryId + " no longer exists in the " +
                        "document.");

                categorySet.Insert(category);
            }

            var restored = old.BindingType == "type"
                ? app.Create.NewTypeBinding(categorySet)
                : (Autodesk.Revit.DB.Binding)app.Create.NewInstanceBinding(categorySet);
            return doc.ParameterBindings.ReInsert(definition!, restored)
                ? (true, null, null)
                : (false, "revert_failed",
                    "Revit refused to restore the previous category set of '" + recorded.Name +
                    "'.");
        }
        catch (Exception ex)
        {
            return (false, "revert_failed", ex.GetType().Name + ": " + ex.Message);
        }
    }
}
