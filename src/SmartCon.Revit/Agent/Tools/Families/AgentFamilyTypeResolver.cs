using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;

namespace SmartCon.Revit.Agent.Tools.Families;

/// <summary>
/// Shared resolution and name guards for the project-side family type tools
/// (G.4): duplicate/rename/replace work on FamilySymbol elements of the
/// project document, not on the family document.
/// </summary>
internal static class AgentFamilyTypeResolver
{
    private static readonly char[] ForbiddenNameChars =
        { '{', '}', '[', ']', '|', ';', '<', '>', '?', '~', ':', '\\' };

    internal static FamilySymbol? ResolveType(
        Document doc,
        long? familyTypeId,
        string? familyName,
        string? typeName,
        out string? errorCode,
        out string? message)
    {
        errorCode = null;
        message = null;

        if (familyTypeId.HasValue && familyTypeId.Value > 0)
        {
            if (doc.GetElement(ElementIdCompat.Create(familyTypeId.Value)) is FamilySymbol byId)
                return byId;

            errorCode = "type_not_found";
            message = "Element " + familyTypeId.Value + " is not a family type (FamilySymbol). " +
                      "Use list_family_types to see the type ids of a family.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(familyName) || string.IsNullOrWhiteSpace(typeName))
        {
            errorCode = "invalid_arguments";
            message = "Pass familyTypeId, or familyName + typeName.";
            return null;
        }

        var family = AgentFamilyReader.ResolveFamily(
            doc, null, familyName, null, out var familyError, out var familyMessage);
        if (family is null)
        {
            errorCode = familyError;
            message = familyMessage;
            return null;
        }

        foreach (var symbolId in family.GetFamilySymbolIds())
        {
            if (doc.GetElement(symbolId) is FamilySymbol symbol
                && string.Equals(symbol.Name, typeName!.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return symbol;
            }
        }

        errorCode = "type_not_found";
        message = "Family '" + family.Name + "' has no type named '" + typeName + "'. Use " +
                  "list_family_types to see its types.";
        return null;
    }

    internal static bool IsNameForbidden(string name, out char found)
    {
        found = '\0';
        foreach (var character in name)
        {
            if (Array.IndexOf(ForbiddenNameChars, character) < 0)
                continue;

            found = character;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Family type names are unique inside one family: the duplicate/rename
    /// guard is a name search over the family symbols, so the typed
    /// <c>type_name_taken</c> is returned before Revit throws.
    /// </summary>
    internal static FamilySymbol? FindTypeByName(
        Document doc,
        Autodesk.Revit.DB.Family family,
        string name)
    {
        foreach (var symbolId in family.GetFamilySymbolIds())
        {
            if (doc.GetElement(symbolId) is FamilySymbol symbol
                && string.Equals(symbol.Name, name, StringComparison.Ordinal))
            {
                return symbol;
            }
        }

        return null;
    }

    internal static int CountInstances(Document doc, ElementId typeId)
    {
        var count = 0;
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)))
        {
            if (element is FamilyInstance instance && instance.GetTypeId() == typeId)
                count++;
        }

        return count;
    }
}
