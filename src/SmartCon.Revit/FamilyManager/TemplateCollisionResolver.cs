using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Logging;
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
/// Deleting template types upfront is NOT possible: Revit forbids deleting
/// the last remaining type of a system family ("Last type in system family
/// cannot be deleted"). So the template type is first RENAMED out of the
/// way, the source types are copied with their names intact, and only then
/// the renamed template types are deleted (after the copy they are no longer
/// the last type of their family).
/// </remarks>
public static class TemplateCollisionResolver
{
    private const string TempPrefix = "zzSmartConTemplate_";

    /// <summary>
    /// Rename the template's types whose names collide with the source type
    /// names. Returns the ids of the renamed types — pass them to
    /// <see cref="DeleteTemporaryTypes"/> after the copy transaction.
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

    /// <summary>
    /// Delete the previously renamed template types. After the source types
    /// are copied, these are no longer the last types of their system
    /// families, so deletion usually succeeds.
    /// </summary>
    /// <remarks>
    /// The deletion is verified post-commit because Revit can refuse it
    /// SILENTLY: when the renamed template type is the last type of its
    /// original system family (Revit remaps the wall family of the copied
    /// type to "Basic Wall" on copy, leaving the renamed one alone in its
    /// family), <c>Transaction.Commit()</c> returns
    /// <c>TransactionStatus.RolledBack</c> without throwing.
    /// A leftover temp-prefixed type is harmless: catalog type lists are
    /// built from placed instances (the temp type has none) and the
    /// synchronizer matches types by name (the temp name never matches).
    /// </remarks>
    public static void DeleteTemporaryTypes(
        ITransactionService tx, Document doc, IReadOnlyList<ElementId> temporaryTypeIds)
    {
        if (temporaryTypeIds.Count == 0) return;

        tx.RunInTransaction(doc, "Delete renamed template types", txDoc =>
        {
            foreach (var id in temporaryTypeIds)
            {
                try
                {
                    txDoc.Delete(id);
                }
                catch (Exception ex)
                {
                    SmartConLogger.Debug(
                        $"TemplateCollisionResolver: delete of {id} threw: {ex.Message}");
                }
            }
        });

        // Post-verify: a silent RolledBack leaves the element alive.
        foreach (var id in temporaryTypeIds)
        {
            var leftover = doc.GetElement(id);
            if (leftover is not null)
            {
                SmartConLogger.Warn(
                    $"TemplateCollisionResolver: template type '{leftover.Name}' could not be " +
                    "deleted (Revit forbids deleting the last type of a system family and " +
                    "silently rolled the transaction back). " +
                    "[Action: мини-проект содержит лишний тип с префиксом '" + TempPrefix +
                    "' — он не попадёт в каталог и не участвует в синхронизации; " +
                    "при желании удалите его вручную в мини-проекте]");
            }
        }
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
