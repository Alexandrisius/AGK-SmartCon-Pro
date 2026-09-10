using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Logging;
using SmartCon.Revit.Compatibility;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Resolves type-name collisions between the default template and the source
/// project when staging a mini-project (Issue #104). Without this,
/// <c>CopyElements</c> auto-renames colliding source types
/// ("Стена 1" → "Стена 2") even with
/// <c>DuplicateTypeAction.UseDestinationTypes</c>, and the catalog stores the
/// type under a foreign name.
/// </summary>
/// <remarks>
/// The template type is RENAMED out of the way and intentionally LEFT in the
/// mini-project: deleting it is impossible when it is the last type of its
/// system family (Revit remaps the copied wall type's family to "Basic Wall"
/// on copy, so the renamed one stays alone in its original family), and the
/// refusal surfaces as a modal error dialog in a real UI session (batch
/// import freeze) or a silent <c>Commit() == RolledBack</c> in a headless
/// session. The leftover is harmless: catalog type lists are built from
/// placed instances (the temp type has none) and the synchronizer matches
/// types by name (the temp name never matches).
/// </remarks>
public static class TemplateCollisionResolver
{
    private const string TempPrefix = "zzSmartConTemplate_";

    /// <summary>
    /// Rename the template's types whose names collide with the source type
    /// names. Returns the ids of the renamed types (informational — they are
    /// left in the mini-project on purpose, see class remarks).
    /// </summary>
    public static IReadOnlyList<ElementId> RenameConflictingTemplateTypes(
        ITransactionService tx,
        Document doc,
        BuiltInCategory category,
        IReadOnlyCollection<string> sourceTypeNames)
    {
        var renamed = new List<ElementId>();
        if (sourceTypeNames.Count == 0) return renamed;

        var sourceNames = new HashSet<string>(sourceTypeNames, StringComparer.OrdinalIgnoreCase);
        tx.RunInTransaction(doc, "Rename template types", txDoc =>
        {
            foreach (var type in CollectCategoryTypes(txDoc, category))
            {
                if (!sourceNames.Contains(type.Name)) continue;
                try
                {
                    type.Name = TempPrefix + type.Name;
                    renamed.Add(type.Id);
                }
                catch (Exception ex)
                {
                    SmartConLogger.Warn(
                        $"TemplateCollisionResolver: cannot rename template type '{type.Name}': " +
                        $"{ex.Message}. [Action: одноимённый тип источника будет переименован " +
                        "при копировании — переименуйте тип в проекте]");
                }
            }
        });

        if (renamed.Count > 0)
        {
            SmartConLogger.Debug(
                $"TemplateCollisionResolver: {renamed.Count} template types renamed for {category}.");
        }
        return renamed;
    }

    private static List<ElementType> CollectCategoryTypes(Document doc, BuiltInCategory category)
    {
        try
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(ElementType))
                .OfCategoryId(ElementIdCompat.Create((int)category))
                .Cast<ElementType>()
                .ToList();
        }
        catch
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(ElementType))
                .Cast<ElementType>()
                .Where(t => t.Category is not null &&
                       CategoryCompat.GetBuiltInCategory(t.Category) == category)
                .ToList();
        }
    }
}
