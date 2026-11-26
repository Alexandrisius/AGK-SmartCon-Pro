using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Revit.Agent.Query;

namespace SmartCon.Revit.Agent.Jobs.Actions;

/// <summary>
/// Selection half of set_parameters: category resolution, element matching and
/// the system-name / sub-component selectors.
/// </summary>
public sealed partial class SetParametersJobAction
{
    private const int MaxSubComponentDepth = 3;

    // MEPSystem.Elements returns only the terminal elements and excludes the
    // base equipment; PipingNetwork/DuctNetwork hold the pipes/ducts+fittings
    // (revitapidocs: Elements, PipingNetwork, DuctNetwork). The union of the
    // three is the full physical system.
    private static bool TrySelectSystemMembers(
        Document document,
        ParametersSelectSpec select,
        CancellationToken cancellationToken,
        out HashSet<long> memberIds,
        out string? error)
    {
        memberIds = new HashSet<long>();
        error = null;
        var systemName = select.SystemName!.Trim();
        foreach (var element in new FilteredElementCollector(document)
                     .OfClass(typeof(MEPSystem))
                     .WhereElementIsNotElementType())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (element is not MEPSystem system)
                continue;
            if (!string.Equals(system.Name ?? string.Empty, systemName, StringComparison.Ordinal))
                continue;
            if (!MatchesSystemDomain(system, select.SystemDomain))
                continue;
            AddSystemMemberIds(system, memberIds);
        }

        if (memberIds.Count > 0)
            return true;
        error = "No MEP system named '" + systemName + "'" +
                (string.IsNullOrWhiteSpace(select.SystemDomain)
                    ? string.Empty
                    : " in domain '" + select.SystemDomain + "'") +
                " was found in the document. [Action: call list_mep_systems to read the exact system names]";
        return false;
    }

    private static bool MatchesSystemDomain(MEPSystem system, string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return true;
        var actual = system switch
        {
            PipingSystem => "piping",
            MechanicalSystem => "mechanical",
            ElectricalSystem => "electrical",
            _ => "other"
        };
        return string.Equals(actual, domain, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddSystemMemberIds(MEPSystem system, HashSet<long> memberIds)
    {
        // ElementSet iterates ELEMENTS, not ids (proven by an integration-test
        // InvalidCastException: "Unable to cast 'Pipe' to 'ElementId'").
        if (system.Elements is not null)
            foreach (var member in system.Elements)
                if (member is Element element)
                    memberIds.Add(element.Id.GetValue());
        if (system.BaseEquipment is { } equipment)
            memberIds.Add(equipment.Id.GetValue());
        if (system is PipingSystem piping && piping.PipingNetwork is not null)
            foreach (var member in piping.PipingNetwork)
                if (member is Element element)
                    memberIds.Add(element.Id.GetValue());
        if (system is MechanicalSystem mechanical && mechanical.DuctNetwork is not null)
            foreach (var member in mechanical.DuctNetwork)
                if (member is Element element)
                    memberIds.Add(element.Id.GetValue());
    }

    /// <summary>
    /// Recursively adds nested sub-components (shared nested family instances)
    /// of every selected element. GetSubComponentIds lives on FamilyInstance and
    /// walks one level only, so nested-in-nested needs the frontier loop.
    /// </summary>
    private static void ExpandWithSubComponents(
        Document document,
        List<Element> elements,
        int maxCount,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<long>();
        foreach (var element in elements)
            seen.Add(element.Id.GetValue());
        var added = 0;
        var frontier = new List<ElementId>();
        foreach (var element in elements)
            frontier.Add(element.Id);
        for (var depth = 0; depth < MaxSubComponentDepth && frontier.Count > 0; depth++)
        {
            var next = new List<ElementId>();
            foreach (var parentId in frontier)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (document.GetElement(parentId) is not FamilyInstance parent)
                    continue;
                foreach (var subId in parent.GetSubComponentIds())
                {
                    if (subId is null || !seen.Add(subId.GetValue()))
                        continue;
                    var sub = document.GetElement(subId);
                    if (sub is null || sub is ElementType)
                        continue;
                    if (elements.Count >= maxCount)
                    {
                        warnings.Add("includeSubComponents stopped at the selection cap; " +
                                     "narrow the selector or raise limit");
                        if (added > 0)
                            warnings.Add("includeSubComponents added " + added + " nested sub-component(s) so far");
                        return;
                    }

                    elements.Add(sub);
                    added++;
                    next.Add(subId);
                }
            }

            frontier = next;
        }

        if (added > 0)
            warnings.Add("includeSubComponents added " + added + " nested sub-component(s)");
    }

    private static List<Element> Select(
        JobActionContext context,
        ParametersSelectSpec select,
        List<ElementId> categoryIds,
        HashSet<long>? systemMemberIds,
        int maxCount,
        List<string> warnings)
    {
        var document = context.Document;
        var levels = new HashSet<long>(select.LevelIds ?? new List<long>());
        var categories = new HashSet<long>(categoryIds.ConvertAll(id => id.GetValue()));
        var result = new List<Element>();
        if (select.ElementIds is { Count: > 0 })
        {
            var skipped = 0;
            var outsideSystem = 0;
            foreach (var id in new HashSet<long>(select.ElementIds))
            {
                if (systemMemberIds is not null && !systemMemberIds.Contains(id))
                {
                    outsideSystem++;
                    continue;
                }

                var element = document.GetElement(ElementIdCompat.Create(id));
                if (element is null || element is ElementType ||
                    !Matches(document, element, select, levels, categories))
                {
                    skipped++;
                    continue;
                }
                result.Add(element);
                if (result.Count >= maxCount)
                    break;
            }
            if (skipped > 0)
                warnings.Add(skipped + " explicit element id(s) are missing or point to a type; skipped");
            if (outsideSystem > 0)
                warnings.Add(outsideSystem + " explicit element id(s) are not members of system '" +
                             select.SystemName!.Trim() + "'; skipped");
            return result;
        }
        if (systemMemberIds is not null)
        {
            var scanned = 0;
            foreach (var id in systemMemberIds)
            {
                if (++scanned % 500 == 0)
                    context.CancellationToken.ThrowIfCancellationRequested();
                var element = document.GetElement(ElementIdCompat.Create(id));
                if (element is null || element is ElementType ||
                    !Matches(document, element, select, levels, categories))
                    continue;
                result.Add(element);
                if (result.Count >= maxCount)
                    break;
            }
            return result;
        }
        FilteredElementCollector collector = new FilteredElementCollector(document);
        collector = collector.WhereElementIsNotElementType();
        if (categories.Count > 0)
            collector = collector.WherePasses(new ElementMulticategoryFilter(categoryIds));
        if (!string.IsNullOrWhiteSpace(select.FamilyName) || select.FamilyNames is { Count: > 0 })
            collector = collector.OfClass(typeof(FamilyInstance));
        var scannedAll = 0;
        foreach (var element in collector)
        {
            if (++scannedAll % 500 == 0)
                context.CancellationToken.ThrowIfCancellationRequested();
            if (!Matches(document, element, select, levels, categories))
                continue;
            result.Add(element);
            if (result.Count >= maxCount)
                break;
        }
        return result;
    }

    private static bool Matches(
        Document document,
        Element element,
        ParametersSelectSpec select,
        HashSet<long> levelIds,
        HashSet<long> categoryIds)
    {
        var categoryId = element.Category?.Id;
        if (categoryIds.Count > 0 && (categoryId is null || !categoryIds.Contains(categoryId.GetValue())))
            return false;
        if (levelIds.Count > 0 && !MatchesLevel(element, levelIds))
            return false;
        if (select.TypeIds is { Count: > 0 })
        {
            var typeId = element.GetTypeId();
            if (typeId == ElementId.InvalidElementId || !select.TypeIds.Contains(typeId.GetValue()))
                return false;
        }

        var (familyName, typeName) = ReadFamilyAndType(document, element);
        if (!string.IsNullOrWhiteSpace(select.FamilyName) &&
            !string.Equals(familyName, select.FamilyName, StringComparison.OrdinalIgnoreCase))
            return false;
        if (select.FamilyNames is { Count: > 0 } &&
            (familyName is null || !select.FamilyNames.Contains(familyName, StringComparer.OrdinalIgnoreCase)))
            return false;
        if (!string.IsNullOrWhiteSpace(select.TypeName) &&
            !string.Equals(typeName, select.TypeName, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(select.NameContains) &&
            (element.Name ?? string.Empty).IndexOf(select.NameContains!, StringComparison.OrdinalIgnoreCase) < 0 &&
            (typeName ?? string.Empty).IndexOf(select.NameContains!, StringComparison.OrdinalIgnoreCase) < 0)
            return false;
        return MatchesConditions(document, element, select);
    }

    private static bool MatchesConditions(
        Document document,
        Element element,
        ParametersSelectSpec select)
    {
        if (select.Conditions is not { Count: > 0 })
            return true;

        var requireAll = select.Logic != QueryLogic.Or;
        foreach (var condition in select.Conditions)
        {
            var parameter = AgentParameterResolver.Find(
                document,
                element,
                condition.Parameter,
                select.IncludeTypeParameters ?? false);
            var matches = parameter is not null &&
                          AgentParameterValue.Matches(document, parameter, condition);

            if (requireAll && !matches)
                return false;
            if (!requireAll && matches)
                return true;
        }

        return requireAll;
    }

    private static bool MatchesLevel(Element element, HashSet<long> levelIds)
    {
        var levelId = element.LevelId;
        if (levelId == ElementId.InvalidElementId)
        {
            var parameter = element.get_Parameter(BuiltInParameter.LEVEL_PARAM);
            if (parameter is { StorageType: StorageType.ElementId } && parameter.HasValue)
                levelId = parameter.AsElementId();
        }
        return levelId != ElementId.InvalidElementId && levelIds.Contains(levelId.GetValue());
    }

    private static (string? Family, string? Type) ReadFamilyAndType(Document document, Element element)
    {
        if (element is FamilyInstance instance)
            return (instance.Symbol?.Family?.Name, instance.Symbol?.Name);
        var typeId = element.GetTypeId();
        return typeId == ElementId.InvalidElementId
            ? (null, null)
            : (null, document.GetElement(typeId)?.Name);
    }

    /// <summary>categoryNames plus its plural alias categories, without duplicates (S.46, #531).</summary>
    private static IEnumerable<string> EnumerateCategoryNames(ParametersSelectSpec select)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in new[] { select.CategoryNames, select.Categories })
        foreach (var name in list ?? new List<string>())
        {
            if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
                yield return name;
        }
    }

    private static bool TryResolveCategories(
        Document document,
        ParametersSelectSpec select,
        out List<ElementId> categoryIds,
        out string? error)
    {
        var resolved = new List<ElementId>();
        categoryIds = resolved;
        error = null;
        var seen = new HashSet<long>();
        var unknown = new List<string>();
        void Add(ElementId id)
        {
            if (seen.Add(id.GetValue()))
                resolved.Add(id);
        }
        try
        {
            foreach (var id in select.CategoryIds ?? new List<long>())
                Add(ElementIdCompat.Create(id));
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
            return false;
        }
        foreach (var name in EnumerateCategoryNames(select))
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;
            if (Enum.TryParse<BuiltInCategory>(name, true, out var builtIn))
            {
                Add(new ElementId(builtIn));
                continue;
            }
            var match = document.Settings.Categories
                .Cast<Category>()
                .FirstOrDefault(category => string.Equals(category.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                unknown.Add(name);
            else
                Add(match.Id);
        }
        if (unknown.Count == 0)
            return true;
        error = "Unknown category name(s): " + string.Join(", ", unknown) + " " +
                Hint("pass a BuiltInCategory id (OST_...) or an exact category name from the model");
        return false;
    }
}
