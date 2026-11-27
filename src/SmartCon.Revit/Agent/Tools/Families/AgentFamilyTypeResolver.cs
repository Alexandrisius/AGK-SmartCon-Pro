using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;

namespace SmartCon.Revit.Agent.Tools.Families;

/// <summary>
/// Shared resolution and name guards for the project-side type tools
/// (G.4/G.4a): duplicate/rename/replace work on ElementType elements of the
/// project document — FamilySymbol for loadable families and system types
/// (WallType, FloorType, DuctType, ...) resolved by category.
/// </summary>
internal static class AgentFamilyTypeResolver
{
    private static readonly char[] ForbiddenNameChars =
        { '{', '}', '[', ']', '|', ';', '<', '>', '?', '~', ':', '\\' };

    /// <summary>
    /// Resolves any ElementType (#441): by id, as a loadable family type
    /// (familyName + typeName) or as a system type (typeName + categoryId or
    /// categoryName — e.g. WallType 'Generic 200' in category Walls).
    /// </summary>
    internal static ElementType? ResolveElementType(
        Document doc,
        long? typeId,
        string? familyName,
        string? typeName,
        long? categoryId,
        string? categoryName,
        out string? errorCode,
        out string? message)
    {
        errorCode = null;
        message = null;

        if (typeId.HasValue && typeId.Value > 0)
        {
            if (doc.GetElement(ElementIdCompat.Create(typeId.Value)) is ElementType byId)
                return byId;

            errorCode = "type_not_found";
            message = "Element " + typeId.Value + " is not a type (ElementType). " +
                      "Use list_family_types or list_system_types to see the type ids.";
            return null;
        }

        if (!string.IsNullOrWhiteSpace(familyName))
            return ResolveType(doc, null, familyName, typeName, out errorCode, out message);

        if (string.IsNullOrWhiteSpace(typeName))
        {
            errorCode = "invalid_arguments";
            message = "Pass familyTypeId, or familyName + typeName, or typeName + " +
                      "categoryName/categoryId (system types such as wall types).";
            return null;
        }

        var category = ResolveCategory(doc, categoryId, categoryName, out var categoryError);
        if (category is null)
        {
            errorCode = "category_not_found";
            message = "System type resolution needs a category: " + categoryError +
                      " Pass typeName + categoryName (e.g. 'Walls' / 'OST_Walls').";
            return null;
        }

        var systemType = FindSystemTypeByName(doc, category.Id, typeName!);
        if (systemType is not null)
            return systemType;

        errorCode = "type_not_found";
        // A bare "use list_system_types" was a dead end when that tool was not in
        // the caller's set (dogfood v16, Ops-3): show the names right here.
        var available = AvailableSystemTypeNames(doc, category.Id, 12);
        message = "Category '" + category.Name + "' has no system type named '" + typeName + "'." +
                  (available.Count > 0
                      ? " Available: " + string.Join(", ", available) +
                        (available.Count >= 12 ? " (first 12)" : string.Empty) + "."
                      : string.Empty) +
                  " [Action: pass one of these names (or a type id) as typeName]";
        return null;
    }

    internal static List<string> AvailableSystemTypeNames(
        Document doc,
        ElementId categoryId,
        int limit)
    {
        var names = new List<string>();
        foreach (var element in new FilteredElementCollector(doc)
                     .WhereElementIsElementType()
                     .OfClass(typeof(ElementType)))
        {
            if (element is not ElementType type || type is FamilySymbol)
                continue;

            if (type.Category is null || type.Category.Id != categoryId)
                continue;

            if (!names.Contains(type.Name, StringComparer.OrdinalIgnoreCase))
                names.Add(type.Name);
            if (names.Count >= limit)
                break;
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    internal static Category? ResolveCategory(
        Document doc,
        long? categoryId,
        string? categoryName,
        out string? error)
    {
        error = null;
        if (categoryId.HasValue && categoryId.Value != 0)
        {
            var byId = Category.GetCategory(doc, ElementIdCompat.Create(categoryId.Value));
            if (byId is not null)
                return byId;

            error = "category id " + categoryId.Value + " does not exist.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(categoryName))
        {
            error = "No category passed.";
            return null;
        }

        var name = categoryName!.Trim();
        if (Enum.TryParse<BuiltInCategory>(name, ignoreCase: true, out var builtIn))
        {
            var byBuiltIn = Category.GetCategory(doc, new ElementId(builtIn));
            if (byBuiltIn is not null)
                return byBuiltIn;
        }

        foreach (Category category in doc.Settings.Categories)
        {
            if (string.Equals(category.Name, name, StringComparison.OrdinalIgnoreCase))
                return category;
        }

        error = "Category '" + categoryName + "' was not found (try 'OST_Walls' or the localized name).";
        return null;
    }

    /// <summary>
    /// A system type of a category: an ElementType that is not a FamilySymbol
    /// (WallType, FloorType, RoofType, PipeType, DuctType, ...). Category ids
    /// of sub-categories are positive element ids, so the match goes through
    /// Element.Category instead of a BuiltInCategory cast.
    /// </summary>
    internal static ElementType? FindSystemTypeByName(
        Document doc,
        ElementId categoryId,
        string name)
    {
        foreach (var element in new FilteredElementCollector(doc)
                     .WhereElementIsElementType()
                     .OfClass(typeof(ElementType)))
        {
            if (element is not ElementType type || type is FamilySymbol)
                continue;

            if (type.Category is null || type.Category.Id != categoryId)
                continue;

            if (string.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase))
                return type;
        }

        return null;
    }

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

    /// <summary>
    /// Human owner of a type for reports: the family for loadable types, the
    /// category for system ones ("family 'KR_SHAR'" / "category 'Стены'").
    /// </summary>
    internal static string DescribeOwner(ElementType type)
    {
        if (type is FamilySymbol symbol && !string.IsNullOrEmpty(symbol.FamilyName))
            return "family '" + symbol.FamilyName + "'";

        return type.Category is { } category
            ? "category '" + category.Name + "'"
            : "the document";
    }

    /// <summary>
    /// Name-taken guard for any ElementType: family type names are unique
    /// inside the family, system type names — inside the category.
    /// </summary>
    internal static bool IsTypeNameTaken(Document doc, ElementType source, string name)
    {
        if (source is FamilySymbol symbol && symbol.Family is not null)
            return FindTypeByName(doc, symbol.Family, name) is not null;

        if (source.Category is not null)
            return FindSystemTypeByName(doc, source.Category.Id, name) is not null;

        return false;
    }

    /// <summary>
    /// Elements that use the type. System types are used by walls, ducts and
    /// other non-FamilyInstance elements, so the count walks every element
    /// (#441: the FamilyInstance-only count made undo delete a wall type
    /// that still had walls on it).
    /// </summary>
    internal static int CountInstances(Document doc, ElementId typeId)
    {
        var count = 0;
        foreach (var element in new FilteredElementCollector(doc)
                     .WhereElementIsNotElementType())
        {
            if (element.GetTypeId() == typeId)
                count++;
        }

        return count;
    }
}
