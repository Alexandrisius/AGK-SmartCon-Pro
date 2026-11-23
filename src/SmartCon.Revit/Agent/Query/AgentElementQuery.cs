using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Revit.Agent.Tools;

namespace SmartCon.Revit.Agent.Query;

internal sealed class AgentQueryViewException : Exception
{
    internal AgentQueryViewException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

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
        var categoryIds = AgentCategoryResolver.Resolve(_doc, spec.Categories, unknownCategories);
        if (unknownCategories.Count > 0)
            return new AgentQueryResult(Array.Empty<Element>(), 0, unknownCategories);

        FilteredElementCollector collector;
        try
        {
            collector = spec.ViewId is { } viewId
                ? new FilteredElementCollector(_doc, ElementIdCompat.Create(viewId))
                : new FilteredElementCollector(_doc);
        }
        catch (ArgumentException ex)
        {
            // Sheets, templates and other non-graphical views cannot be iterated.
            throw new AgentQueryViewException(
                "viewId " + spec.ViewId + " cannot be used for element iteration: " + ex.Message, ex);
        }

        collector = collector.WhereElementIsNotElementType();

        if (categoryIds.Count > 0)
            collector = collector.WherePasses(new ElementMulticategoryFilter(categoryIds.ToList()));

        var conditions = spec.Conditions
            .Select((condition, index) => new AgentConditionTracker(index, condition))
            .ToList();

        var kept = new List<Element>();
        var total = 0;
        var scanned = 0;
        var candidates = 0;
        Element? firstCandidate = null;

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

            candidates++;
            if (candidates == 1)
                firstCandidate = element;
            if (!MatchesConditions(element, spec, conditions))
                continue;

            total++;
            if (kept.Count < keepLimit)
                kept.Add(element);
        }

        foreach (var condition in conditions)
        {
            if (condition.IsUnknown(candidates))
            {
                condition.Suggestions = AgentQueryFields.SuggestParameterNames(
                    _doc,
                    firstCandidate!,
                    condition.Parameter);
            }
        }

        return new AgentQueryResult(
            kept,
            total,
            unknownCategories,
            candidates,
            conditions,
            firstCandidateId: firstCandidate?.Id.GetValue());
    }

    private static bool MatchesLevel(Element element, IReadOnlyList<long> levelIds)
    {
        if (levelIds.Count == 0)
            return true;

        if (!AgentQueryFields.TryGetLevelId(element, out var levelId))
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

        if (element is not FamilyInstance instance)
            return false;

        // FamilyInstance.Room must not be used for doors and windows: they sit on
        // the boundary, so it is null for most of them (Autodesk docs). FromRoom
        // and ToRoom are the documented properties for openings.
        var categoryValue = instance.Category?.Id.GetValue();
        var isOpening = categoryValue == (long)(int)BuiltInCategory.OST_Doors
                        || categoryValue == (long)(int)BuiltInCategory.OST_Windows;
        if (isOpening)
        {
            try
            {
                return instance.FromRoom is { } from && from.Id.GetValue() == roomId.Value
                       || instance.ToRoom is { } to && to.Id.GetValue() == roomId.Value;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        return instance.Room is { } room && room.Id.GetValue() == roomId.Value;
    }

    private bool MatchesConditions(
        Element element,
        QuerySpec spec,
        List<AgentConditionTracker> conditions)
    {
        if (conditions.Count == 0)
            return true;

        var requireAll = spec.Logic == QueryLogic.And;
        var decided = false;
        var decision = false;
        foreach (var condition in conditions)
        {
            // A parameter that exists nowhere must be reported even when an
            // earlier condition already decided the element: the lookup is kept
            // until the parameter has been seen on at least one candidate.
            if (decided && !condition.NeedsResolution)
                continue;

            if (TryResolve(element, condition, spec.IncludeTypeParameters, out var target))
                condition.MarkResolved();

            if (decided)
                continue;

            var matches = target.Matches(_doc, condition.Condition);
            if (requireAll == !matches)
            {
                decided = true;
                decision = matches;
            }
        }

        return decided ? decision : requireAll;
    }

    private bool TryResolve(
        Element element,
        AgentConditionTracker condition,
        bool includeTypeParameters,
        out AgentConditionTarget target)
    {
        if (condition.Field.Length > 0)
        {
            target = AgentConditionTarget.OfField(
                AgentQueryFields.Read(_doc, element, condition.Field));
            return true;
        }

        var parameter = AgentParameterResolver.Find(
            _doc,
            element,
            condition.Condition.Parameter,
            includeTypeParameters);
        target = AgentConditionTarget.OfParameter(parameter);
        return parameter is not null;
    }
}

internal sealed class AgentQueryResult
{
    internal AgentQueryResult(
        IReadOnlyList<Element> elements,
        int total,
        IReadOnlyList<string> unknownCategories,
        int candidates = 0,
        IReadOnlyList<AgentConditionTracker>? conditions = null,
        long? firstCandidateId = null)
    {
        Elements = elements;
        Total = total;
        UnknownCategories = unknownCategories;
        Candidates = candidates;
        Conditions = conditions ?? Array.Empty<AgentConditionTracker>();
        FirstCandidateId = firstCandidateId;
    }

    internal IReadOnlyList<Element> Elements { get; }

    internal int Total { get; }

    internal IReadOnlyList<string> UnknownCategories { get; }

    /// <summary>Elements that passed the category/level/type/room filters.</summary>
    internal int Candidates { get; }

    internal IReadOnlyList<AgentConditionTracker> Conditions { get; }

    /// <summary>
    /// Id (I-05: value only) of the first scanned candidate, for post-query
    /// diagnostics that need a representative element.
    /// </summary>
    internal long? FirstCandidateId { get; }

    /// <summary>
    /// The typed failure for the first condition whose parameter exists on no
    /// scanned candidate: such a condition makes every result look like "there
    /// are no such elements". Null when every condition resolved at least once
    /// (or is an exists/notExists check, where absence is the answer).
    /// </summary>
    internal ToolResult? UnknownParameterFailure()
    {
        var unknown = Conditions.FirstOrDefault(condition => condition.IsUnknown(Candidates));
        if (unknown is null)
            return null;

        var others = Conditions
            .Where(condition => condition != unknown && condition.IsUnknown(Candidates))
            .Select(condition => "#" + condition.Index + " '" + condition.Parameter + "'")
            .ToList();

        var didYouMean = unknown.Suggestions.Count == 0
            ? string.Empty
            : " Did you mean " + string.Join(
                " or ", unknown.Suggestions.Select(name => "'" + name + "'")) + "?";

        var message =
            "Condition #" + unknown.Index + ": parameter '" + unknown.Parameter +
            "' was not found on any of the " + Candidates +
            " scanned element(s) — an empty result would be misleading." + didYouMean +
            " For element names use parameter:\"" + QuerySpec.NameField + "\" (also: " +
            string.Join(
                ", ",
                QuerySpec.StructuralFields.Where(field => field != QuerySpec.NameField)) +
            "). Use get_element_details(parameterNameContains=...) to find the exact name." +
            (others.Count == 0
                ? string.Empty
                : " Condition" + (others.Count > 1 ? "s " : " ") + string.Join(", ", others) +
                  " not found on any scanned element either.");

        var data = JsonSerializer.SerializeToElement(new
        {
            index = unknown.Index,
            parameter = unknown.Parameter,
            suggestions = unknown.Suggestions,
            scanned = Candidates
        });
        return ToolResult.Fail("unknown_parameter", message, data: data);
    }
}

/// <summary>
/// One condition as the scan sees it: where its parameter resolves on an element
/// and on how many candidates it existed at all.
/// </summary>
internal sealed class AgentConditionTracker
{
    internal AgentConditionTracker(int index, QueryCondition condition)
    {
        Index = index;
        Condition = condition;
        Parameter = condition.Parameter.Trim();
        QuerySpec.TryGetStructuralField(condition.Parameter, out var field);
        Field = field;

        // exists / notExists answer the question directly when the parameter is
        // absent everywhere; for the other operators absence is a broken query.
        Tracks = condition.Op != QueryOperator.Exists && condition.Op != QueryOperator.NotExists;
    }

    internal int Index { get; }

    internal QueryCondition Condition { get; }

    internal string Parameter { get; }

    /// <summary>Canonical structural field, or empty when the condition names a Revit parameter.</summary>
    internal string Field { get; }

    internal bool Tracks { get; }

    internal int Resolved { get; private set; }

    internal IReadOnlyList<string> Suggestions { get; set; } = Array.Empty<string>();

    internal bool NeedsResolution => Tracks && Resolved == 0;

    internal bool IsUnknown(int candidates) => Tracks && candidates > 0 && Resolved == 0;

    internal void MarkResolved() => Resolved++;
}

/// <summary>
/// What a condition's parameter resolved to on one element: the Revit parameter,
/// or the texts of a structural field (name, family, ...).
/// </summary>
internal readonly struct AgentConditionTarget
{
    private readonly Parameter? _parameter;

    private readonly IReadOnlyList<string>? _texts;

    private AgentConditionTarget(Parameter? parameter, IReadOnlyList<string>? texts)
    {
        _parameter = parameter;
        _texts = texts;
    }

    internal static AgentConditionTarget OfParameter(Parameter? parameter) =>
        new(parameter, null);

    internal static AgentConditionTarget OfField(IReadOnlyList<string> texts) =>
        new(null, texts);

    internal bool Matches(Document doc, QueryCondition condition) =>
        _texts is not null
            ? AgentQueryFields.Matches(_texts, condition)
            : _parameter is not null && AgentParameterValue.Matches(doc, _parameter, condition);
}