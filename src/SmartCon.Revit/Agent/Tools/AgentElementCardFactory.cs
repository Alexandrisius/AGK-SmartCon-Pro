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
            if (!string.IsNullOrWhiteSpace(value))
                parameters[label] = value;
        }

        var level = TryGetLevel(doc, element);
        var (family, typeName) = ReadFamilyAndType(element);

        return new ElementCard
        {
            UniqueId = element.UniqueId,
            ElementId = element.Id.GetValue(),
            Category = GetCategoryName(element.Category),
            Family = family,
            Type = typeName,
            Name = element.Name,
            LevelId = level is null ? null : level.Id.GetValue(),
            LevelName = level?.Name,
            Room = element.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString(),
            Parameters = parameters
        };
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