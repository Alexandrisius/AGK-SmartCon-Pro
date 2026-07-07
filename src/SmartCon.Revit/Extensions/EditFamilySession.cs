using Autodesk.Revit.DB;
using SmartCon.Core.Logging;

namespace SmartCon.Revit.Extensions;

/// <summary>
/// Helper that encapsulates the EditFamily boilerplate pattern:
/// validates doc.IsModifiable, opens familyDoc, executes action, closes familyDoc.
/// Reduces duplication across RevitLookupTableService, RevitDynamicSizeResolver, etc.
/// </summary>
internal static class EditFamilySession
{
    /// <summary>
    /// Open a family document for read-only analysis and execute <paramref name="action"/>.
    /// The family document is always closed (without saving) in the finally block.
    /// Returns <c>default</c> if the family cannot be opened or doc is modifiable.
    /// </summary>
    public static T? Run<T>(Document doc, FamilyInstance instance, Func<Document, T> action)
    {
        if (doc.IsModifiable)
        {
            using var _scope = SmartConLogger.BeginScope("Lookup", ("Method", "EditFamilySession.Run"), ("Reason", "DocIsModifiable"));
            SmartConLogger.Debug("doc.IsModifiable=true → EditFamily forbidden → return default");
            return default;
        }

        var family = instance.Symbol?.Family;
        if (family is null)
        {
            using var _scope = SmartConLogger.BeginScope("Lookup", ("Method", "EditFamilySession.Run"), ("Reason", "FamilyNull"));
            SmartConLogger.Debug("family=null → return default");
            return default;
        }

        Document? familyDoc = null;
        try
        {
            familyDoc = doc.EditFamily(family);
            if (familyDoc is null)
            {
                using var _scope = SmartConLogger.BeginScope("Lookup", ("Method", "EditFamilySession.Run"), ("Reason", "EditFamilyNull"));
                SmartConLogger.Debug("EditFamily returned null → return default");
                return default;
            }

            return action(familyDoc);
        }
        catch (Exception ex)
        {
            using var _scope = SmartConLogger.BeginScope("EditFamilySession", ("FamilyName", family.Name));
            SmartConLogger.Debug($"EditFamily exception: {ex.GetType().Name}: {ex.Message}");
            SmartConLogger.Warn($"Failed for '{family.Name}': {ex.Message}");
            return default;
        }
        finally
        {
            familyDoc?.Close(false);
        }
    }

    /// <summary>
    /// Void overload — executes an action that doesn't return a value.
    /// </summary>
    public static bool Run(Document doc, FamilyInstance instance, Action<Document> action)
    {
        var result = Run<bool>(doc, instance, familyDoc =>
        {
            action(familyDoc);
            return true;
        });
        return result;
    }
}
