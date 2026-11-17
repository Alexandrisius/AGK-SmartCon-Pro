using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Revit.Agent.Query;

internal sealed class AgentElementQuery
{
    private readonly Document _doc;

    internal AgentElementQuery(Document doc)
    {
        _doc = doc ?? throw new ArgumentNullException(nameof(doc));
    }

    internal AgentQueryResult Execute(
        QuerySpec spec,
        int keepLimit,
        CancellationToken ct)
    {
        var unknownCategories = new List<string>();
        var categoryIds = ResolveCategories(spec.Categories, unknownCategories);
        if (unknownCategories.Count > 0)
            return new AgentQueryResult(Array.Empty<Element>(), 0, unknownCategories);

        var collector = spec.ViewId is { } viewId
            ? new FilteredElementCollector(_doc, ElementIdCompat.Create(viewId))
            : new FilteredElementCollector(_doc);
        collector = collector.WhereElementIsNotElementType();

        if (categoryIds.Count > 0)
            collector = collector.WherePasses(new ElementMulticategoryFilter(categoryIds));

        var kept = new List<Element>();
        var total = 0;
        var scanned = 0;

        foreach (var element in collector)
        {
            if (++scanned % 500 == 0)
                ct.ThrowIfCancellationRequested();

            if (!MatchesLevel(element, spec.LevelIds))
                continue;
            if (!MatchesType(element, spec.TypeIds))
                continue;
            if (!MatchesRoom(element, spec.RoomId))
                continue;
            if (!MatchesConditions(element, spec))
                continue;

            total++;
            if (kept.Count < keepLimit)
                kept.Add(element);
        }

        return new AgentQueryResult(kept, total, unknownCategories);
    }

    private static bool MatchesLevel(Element element, IReadOnlyList<long> levelIds)
    {
        if (levelIds.Count == 0)
            return true;

        var levelId = element.LevelId;
        if (levelId == ElementId.InvalidElementId)
        {
            var parameter = element.get_Parameter(BuiltInParameter.LEVEL_PARAM);
            if (parameter is { StorageType: StorageType.ElementId } && parameter.HasValue)
                levelId = parameter.AsElementId();
        }

        if (levelId == ElementId.InvalidElementId)
            return false;

        var value = levelId.GetValue();
        for (var i = 0; i < levelIds.Count; i++)
        {
            if (levelIds[i] == value)
                return true;
        }

        return false;
    }

    private static bool MatchesType(Element element, IReadOnlyList<long> typeIds)
    {
        if (typeIds.Count == 0)
            return true;

        var typeId = element.GetTypeId();
        if (typeId == ElementId.InvalidElementId)
            return false;

        var value = typeId.GetValue();
        for (var i = 0; i < typeIds.Count; i++)
        {
            if (typeIds[i] == value)
                return true;
        }

        return false;
    }

    private static bool MatchesRoom(Element element, long? roomId)
    {
        if (!roomId.HasValue)
            return true;

        return element is FamilyInstance instance
               && instance.Room is { } room
               && room.Id.GetValue() == roomId.Value;
    }

    private bool MatchesConditions(Element element, QuerySpec spec)
    {
        if (spec.Conditions.Count == 0)
            return true;

        var requireAll = spec.Logic == QueryLogic.And;
        foreach (var condition in spec.Conditions)
        {
            var parameter = AgentParameterResolver.Find(
                _doc,
                element,
                condition.Parameter,
                spec.IncludeTypeParameters);

            var matches = parameter is not null
                          && AgentParameterValue.Matches(_doc, parameter, condition);

            if (requireAll && !matches)
                return false;
            if (!requireAll && matches)
                return true;
        }

        return requireAll;
    }

    private List<ElementId> ResolveCategories(
        IReadOnlyList<string> names,
        List<string> unknown)
    {
        var ids = new List<ElementId>(names.Count);
        foreach (var name in names)
        {
            if (Enum.TryParse<BuiltInCategory>(name, ignoreCase: true, out var builtIn))
            {
                ids.Add(new ElementId(builtIn));
                continue;
            }

            Category? match = null;
            foreach (Category category in _doc.Settings.Categories)
            {
                if (string.Equals(category.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    match = category;
                    break;
                }
            }

            if (match is null)
            {
                unknown.Add(name);
                continue;
            }

            ids.Add(match.Id);
        }

        return ids;
    }
}

internal sealed class AgentQueryResult
{
    internal AgentQueryResult(
        IReadOnlyList<Element> elements,
        int total,
        IReadOnlyList<string> unknownCategories)
    {
        Elements = elements;
        Total = total;
        UnknownCategories = unknownCategories;
    }

    internal IReadOnlyList<Element> Elements { get; }

    internal int Total { get; }

    internal IReadOnlyList<string> UnknownCategories { get; }
}