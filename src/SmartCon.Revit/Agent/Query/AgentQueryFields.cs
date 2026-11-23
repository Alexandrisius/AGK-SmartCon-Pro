using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent;
using SmartCon.Revit.Agent.Tools;

namespace SmartCon.Revit.Agent.Query;

/// <summary>
/// Structural fields a condition's <c>parameter</c> may name instead of a Revit
/// parameter (<see cref="QuerySpec.StructuralFields"/>): the texts an element
/// carries for each of them and how a condition matches those texts. Reserved
/// words of the query language — the engine resolves them before any parameter
/// lookup. Also the shared parameter-name suggestion for a condition whose
/// parameter exists on no element.
/// </summary>
internal static class AgentQueryFields
{
    private static readonly string[] NoTexts = Array.Empty<string>();

    /// <summary>
    /// Texts the element carries for a canonical structural field; empty when it
    /// has no value for it (no family, no level).
    /// </summary>
    internal static IReadOnlyList<string> Read(Document doc, Element element, string field)
    {
        switch (field)
        {
            case QuerySpec.NameField:
                // Room.Name glues the room name and number into one string
                // (Autodesk Developer Guide); a condition on "name" must see the
                // parts the user knows: the ROOM_NAME parameter and the number,
                // plus the glued form for compatibility.
                if (element is Autodesk.Revit.DB.Architecture.Room room)
                {
                    return Texts(
                        StringOf(room, BuiltInParameter.ROOM_NAME) ?? room.Name,
                        StringOf(room, BuiltInParameter.ROOM_NUMBER),
                        room.Name);
                }

                // "Name contains X" means the element, its family or its type: a
                // family instance matches on any of the three.
                if (element is FamilyInstance instance)
                {
                    return Texts(
                        element.Name,
                        instance.Symbol?.Family?.Name,
                        instance.Symbol?.Name);
                }

                return Texts(element.Name);
            case QuerySpec.FamilyField:
                return Texts(ReadFamily(doc, element));
            case QuerySpec.TypeField:
                return Texts(ReadType(doc, element));
            case QuerySpec.CategoryField:
                return Texts(
                    AgentElementCardFactory.GetCategoryName(element.Category),
                    element.Category?.Name);
            case QuerySpec.LevelField:
                return Texts(ReadLevelName(doc, element));
            default:
                // A structural field without a reader must not look like "no value".
                throw new InvalidOperationException(
                    "Structural field '" + field + "' has no reader in " + nameof(AgentQueryFields) + ".");
        }
    }

    /// <summary>
    /// Family of the element: the loaded family of a family instance, the system
    /// family (Basic Wall, Pipe Types) of any other typed element. The instance
    /// parameters ALL_MODEL_FAMILY_NAME / ALL_MODEL_TYPE_NAME carry no value on
    /// system-family instances, so the type is the source.
    /// </summary>
    internal static string? ReadFamily(Document doc, Element element)
    {
        if (element is FamilyInstance instance)
            return instance.Symbol?.Family?.Name;

        var typeId = element.GetTypeId();
        return typeId != ElementId.InvalidElementId && doc.GetElement(typeId) is ElementType type
            ? type.FamilyName
            : null;
    }

    /// <summary>Type name of the element (the symbol name of a family instance).</summary>
    internal static string? ReadType(Document doc, Element element)
    {
        if (element is FamilyInstance instance)
            return instance.Symbol?.Name;

        var typeId = element.GetTypeId();
        return typeId != ElementId.InvalidElementId ? doc.GetElement(typeId)?.Name : null;
    }

    internal static string? ReadLevelName(Document doc, Element element)
    {
        return TryGetLevelId(element, out var levelId) && doc.GetElement(levelId) is Level level
            ? level.Name
            : null;
    }

    /// <summary>
    /// Level of the element for both the levelIds filter and the structural
    /// field: Element.LevelId, then the LEVEL_PARAM fallback for classes whose
    /// LevelId is not set.
    /// </summary>
    internal static bool TryGetLevelId(Element element, out ElementId levelId)
    {
        levelId = element.LevelId;
        if (levelId == ElementId.InvalidElementId)
        {
            var parameter = element.get_Parameter(BuiltInParameter.LEVEL_PARAM);
            if (parameter is { StorageType: StorageType.ElementId } && parameter.HasValue)
                levelId = parameter.AsElementId();
        }

        return levelId != ElementId.InvalidElementId;
    }

    internal static bool Matches(IReadOnlyList<string> texts, QueryCondition condition)
    {
        switch (condition.Op)
        {
            case QueryOperator.Exists:
                return texts.Count > 0;
            case QueryOperator.NotExists:
                return texts.Count == 0;
        }

        if (texts.Count == 0 || condition.Value is not { } value)
            return false;

        return condition.Op switch
        {
            // A negation holds only when no text matches: a family instance
            // whose type is called X is still named X even if its family is not.
            QueryOperator.Neq => !Any(texts, QueryOperator.Eq, value),
            QueryOperator.NotIn => !Any(texts, QueryOperator.In, value),
            _ => Any(texts, condition.Op, value)
        };
    }

    /// <summary>
    /// Closest names for a condition parameter that resolved on no element: the
    /// structural fields plus the parameter names of the element and of its type,
    /// nearest first.
    /// </summary>
    internal static IReadOnlyList<string> SuggestParameterNames(
        Document doc,
        Element element,
        string requested)
    {
        var names = new List<string>(QuerySpec.StructuralFieldNames);

        Collect(element);
        var typeId = element.GetTypeId();
        if (typeId != ElementId.InvalidElementId && doc.GetElement(typeId) is Element type)
            Collect(type);

        return QuerySpec.SuggestParameterNames(requested, names);

        void Collect(Element source)
        {
            foreach (Parameter parameter in source.Parameters)
            {
                var name = parameter.Definition?.Name;
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name!);
            }
        }
    }

    private static bool Any(IReadOnlyList<string> texts, QueryOperator op, JsonElement value)
    {
        foreach (var text in texts)
        {
            if (AgentParameterValue.MatchesString(text, op, value))
                return true;
        }

        return false;
    }

    private static string? StringOf(Element element, BuiltInParameter parameter)
    {
        try
        {
            var value = element.get_Parameter(parameter)?.AsString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> Texts(params string?[] values)
    {
        List<string>? texts = null;
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            texts ??= new List<string>(values.Length);
            if (!Contains(texts, value!))
                texts.Add(value!);
        }

        return texts is { Count: > 0 } ? texts : NoTexts;

        static bool Contains(List<string> texts, string value)
        {
            foreach (var text in texts)
            {
                if (string.Equals(text, value, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
