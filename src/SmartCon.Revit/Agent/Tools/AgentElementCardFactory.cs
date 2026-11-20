using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Revit.Agent.Tools;

internal static class AgentElementCardFactory
{
    private static readonly (BuiltInParameter Parameter, string Label)[] CardParameters =
    {
        (BuiltInParameter.ALL_MODEL_MARK, "Mark"),
        (BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, "Comments"),
        (BuiltInParameter.ALL_MODEL_TYPE_NAME, "Type Name"),
        (BuiltInParameter.ALL_MODEL_TYPE_MARK, "Type Mark"),
        (BuiltInParameter.ALL_MODEL_MANUFACTURER, "Manufacturer"),
        (BuiltInParameter.ALL_MODEL_MODEL, "Model"),
        (BuiltInParameter.ALL_MODEL_DESCRIPTION, "Description")
    };

    internal static ElementCard Create(Document doc, Element element)
    {
        var parameters = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (builtInParameter, label) in CardParameters)
        {
            var parameter = element.get_Parameter(builtInParameter);
            var value = ReadParameter(parameter);
            if (string.IsNullOrWhiteSpace(value))
                continue;

            // Use the localized display name, matching get_element_details and
            // set_parameters; the hardcoded English label is only a fallback.
            var name = parameter?.Definition?.Name;
            parameters[string.IsNullOrWhiteSpace(name) ? label : name!] = value;
        }

        var level = TryGetLevel(doc, element);
        var (family, typeName) = ReadFamilyAndType(element);
        var (typeId, familyId, familyUniqueId) = ReadTypeIdentity(element);

        return new ElementCard
        {
            UniqueId = element.UniqueId,
            ElementId = element.Id.GetValue(),
            TypeId = typeId,
            FamilyId = familyId,
            FamilyUniqueId = familyUniqueId,
            Category = GetCategoryName(element.Category),
            Family = family,
            Type = typeName,
            Name = element.Name,
            LevelId = level is null ? null : level.Id.GetValue(),
            LevelName = level?.Name,
            Room = ReadRoomName(element),
            // Omit the field when nothing was collected: an empty "{}" reads as
            // "the query found no parameter values" (stress test #2).
            Parameters = parameters.Count == 0 ? null : parameters
        };
    }

    /// <summary>
    /// Room name of an element. Doors and windows own no ROOM_NAME parameter and
    /// FamilyInstance.Room is null on a boundary — their rooms come from
    /// FromRoom/ToRoom (Autodesk docs), so show both when they differ.
    /// </summary>
    private static string? ReadRoomName(Element element)
    {
        var parameter = element.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();
        if (!string.IsNullOrWhiteSpace(parameter))
            return parameter;

        if (element is not FamilyInstance instance)
            return null;

        var categoryValue = instance.Category?.Id.GetValue();
        var isOpening = categoryValue == (long)(int)BuiltInCategory.OST_Doors
                        || categoryValue == (long)(int)BuiltInCategory.OST_Windows;
        if (!isOpening)
            return null;

        try
        {
            var from = instance.FromRoom;
            var to = instance.ToRoom;
            var names = new List<string>(2);
            if (from is not null && !string.IsNullOrWhiteSpace(from.Name))
                names.Add(from.Name);
            if (to is not null
                && !string.IsNullOrWhiteSpace(to.Name)
                && !string.Equals(to.Name, from?.Name, StringComparison.Ordinal))
            {
                names.Add(to.Name);
            }

            return names.Count == 0 ? null : string.Join(" / ", names);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    internal static string? GetCategoryName(Category? category)
    {
        if (category is null)
            return null;

        var id = category.Id.GetValue();
        if (id is < int.MinValue or > int.MaxValue)
            return category.Name;

        var builtInCategory = (BuiltInCategory)(int)id;
        return Enum.IsDefined(typeof(BuiltInCategory), builtInCategory)
            ? builtInCategory.ToString()
            : category.Name;
    }

    internal static string? ReadParameterValue(Parameter? parameter)
    {
        if (parameter is null || !parameter.HasValue)
            return null;

        try
        {
            var valueString = parameter.AsValueString();
            if (!string.IsNullOrWhiteSpace(valueString))
                return valueString;

            return parameter.StorageType switch
            {
                StorageType.String => parameter.AsString(),
                StorageType.Integer => parameter.AsInteger().ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                StorageType.Double => parameter.AsDouble().ToString(
                    "R", System.Globalization.CultureInfo.InvariantCulture),
                StorageType.ElementId => parameter.AsElementId().GetValue().ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                _ => null
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ReadParameter(Parameter? parameter) => ReadParameterValue(parameter);

    private static Level? TryGetLevel(Document doc, Element element)
    {
        var levelId = element.LevelId;
        if (levelId == ElementId.InvalidElementId)
            return null;

        return doc.GetElement(levelId) as Level;
    }

    private static (long? TypeId, long? FamilyId, string? FamilyUniqueId) ReadTypeIdentity(
        Element element)
    {
        var symbol = element as FamilySymbol ?? (element as FamilyInstance)?.Symbol;
        if (symbol is not null)
        {
            var family = symbol.Family;
            return (
                symbol.Id.GetValue(),
                family?.Id.GetValue(),
                family?.UniqueId);
        }

        var typeId = element.GetTypeId();
        return typeId == ElementId.InvalidElementId
            ? (null, null, null)
            : (typeId.GetValue(), null, null);
    }

    private static (string? Family, string? Type) ReadFamilyAndType(Element element)
    {
        if (element is FamilyInstance instance)
            return (instance.Symbol?.Family?.Name, instance.Symbol?.Name);

        if (element is ElementType elementType)
            return (elementType.FamilyName, elementType.Name);

        var familyName = element.get_Parameter(BuiltInParameter.ALL_MODEL_FAMILY_NAME)?.AsString();
        var typeName = element.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_NAME)?.AsString();
        return (familyName, typeName);
    }
}