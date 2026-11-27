using Autodesk.Revit.DB;
using RevitFamily = Autodesk.Revit.DB.Family;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Revit.Agent.Tools.Families;
namespace SmartCon.Revit.Agent.Jobs.Actions;

/// <summary>
/// Shared family-selector resolution for family-definition job actions
/// (map_family_parameters, replace_family_parameter): turns the select block
/// (familyIds/familyUniqueIds/familyNames/nameContains/categoryIds, limit) into
/// the sorted list of editable families plus unmatched-name warnings, and
/// resolves a family across the LoadFamily recreation (unit key, then the
/// post-load unique id, then the family name).
/// </summary>
internal static class FamilyJobSelectors
{
    /// <summary>
    /// categoryNames and its alias categories may both arrive; set_parameters
    /// UNIONS them, so the family jobs do too instead of silently dropping the
    /// second list (validator v16, P3).
    /// </summary>
    internal static IReadOnlyList<string>? MergeCategoryNameAliases(
        IReadOnlyList<string>? categoryNames,
        IReadOnlyList<string>? categories)
    {
        if (categoryNames is not { Count: > 0 })
            return categories;
        if (categories is not { Count: > 0 })
            return categoryNames;

        var merged = new List<string>(categoryNames);
        var seen = new HashSet<string>(categoryNames, StringComparer.OrdinalIgnoreCase);
        foreach (var name in categories)
        {
            if (seen.Add(name))
                merged.Add(name);
        }

        return merged;
    }

    internal sealed class FamilySelection
    {
        public bool Success => ErrorCode is null;

        public string? ErrorCode { get; init; }

        public string? ErrorDetail { get; init; }

        public IReadOnlyList<RevitFamily> Families { get; init; } = Array.Empty<RevitFamily>();

        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    }

    internal static FamilySelection Resolve(
        Document document,
        IReadOnlyList<long>? familyIds,
        IReadOnlyList<string>? familyUniqueIds,
        IReadOnlyList<string>? familyNames,
        string? nameContains,
        IReadOnlyList<long>? categoryIds,
        IReadOnlyList<string>? categoryNames,
        int? limit,
        bool placedOnly = false)
    {
        if (limit.HasValue && limit.Value <= 0)
        {
            return Error(
                JobErrorCodes.InvalidSpec,
                "select.limit must be greater than zero. [Action: pass a positive limit or omit it]");
        }

        // Category NAMES resolve exactly like the set_parameters selector:
        // display name, OST_* enum or id string, arrays included (v16 D3).
        var unknownCategoryNames = new List<string>();
        if (categoryNames is { Count: > 0 })
        {
            var resolved = SmartCon.Revit.Agent.Tools.AgentCategoryResolver.Resolve(
                document, categoryNames, unknownCategoryNames);
            if (resolved.Count == 0)
            {
                // Empty/whitespace names are dropped without entering `unknown`,
                // so SuggestSuffix(unknown[0]) used to throw (validator v16, P2-4).
                var detail = unknownCategoryNames.Count > 0
                    ? "unknown categories " + string.Join(", ", unknownCategoryNames) +
                      SmartCon.Revit.Agent.Tools.AgentCategoryResolver.SuggestSuffix(
                          document, unknownCategoryNames[0])
                    : "no recognizable category names (empty or whitespace-only entries)";
                return Error(
                    JobErrorCodes.InvalidSpec,
                    "select.categoryNames: " + detail +
                    " [Action: pass names from list_categories]");
            }

            var merged = categoryIds?.ToList() ?? new List<long>();
            foreach (var id in resolved)
            {
                var idValue = id.GetValue();
                if (!merged.Contains(idValue))
                    merged.Add(idValue);
            }

            // Keep going with the valid names; the dropped ones surface as
            // warnings below (same policy as list_families).
            categoryIds = merged;
        }

        var hasSelector = familyIds is { Count: > 0 }
                          || familyUniqueIds is { Count: > 0 }
                          || familyNames is { Count: > 0 }
                          || !string.IsNullOrWhiteSpace(nameContains)
                          || categoryIds is { Count: > 0 };
        if (!hasSelector)
        {
            return Error(
                JobErrorCodes.SelectorEmpty,
                "The select block names no family: pass familyIds, familyUniqueIds, familyNames, " +
                "nameContains or categoryIds. [Action: list the loaded families and narrow the selector]");
        }

        // #433: familyNames resolve to whatever loaded family bears the name; names
        // that match nothing stay here and surface as warnings (or, when nothing at
        // all was selected, in the selector-empty error).
        var pendingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (familyNames is { Count: > 0 })
        {
            foreach (var name in familyNames)
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    pendingNames.Add(name.Trim());
                }
            }
        }

        var families = new List<RevitFamily>();
        var loadedNames = new List<string>();
        var skipped = 0;
        foreach (var element in new FilteredElementCollector(document).OfClass(typeof(RevitFamily)))
        {
            if (element is not RevitFamily family)
            {
                continue;
            }

            loadedNames.Add(family.Name);
            pendingNames.Remove(family.Name);
            if (!Matches(family, familyIds, familyNames, familyUniqueIds, nameContains, categoryIds))
            {
                continue;
            }

            if (family.IsInPlace || !family.IsEditable)
            {
                skipped++;
                continue;
            }

            // placedOnly narrows the work to families with at least one placed
            // instance (live v18: «migrate all families» silently grew to the
            // whole library and alphabet put an unplaced 273-type family at
            // the head of the queue for 44 minutes — the placed ones stayed
            // in the interrupted tail).
            if (placedOnly && !PlacedFamilyIds(document).Contains(family.Id.GetValue()))
            {
                continue;
            }

            families.Add(family);
        }

        families.Sort((left, right) =>
            string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));

        if (families.Count == 0 && placedOnly)
        {
            return Error(
                JobErrorCodes.SelectorEmpty,
                "select.placedOnly: none of the matched families has placed instances. " +
                "[Action: drop placedOnly to include the unplaced library, or check the selector]");
        }

        if (families.Count == 0)
        {
            if (pendingNames.Count > 0)
            {
                return Error(
                    JobErrorCodes.SelectorEmpty,
                    "None of the select.familyNames matched a loaded family: " +
                    JoinNames(pendingNames) + "." + NearestSuffix(pendingNames, loadedNames) +
                    " [Action: list the loaded families and fix the names]");
            }

            return Error(
                JobErrorCodes.SelectorEmpty,
                skipped > 0
                    ? "All " + skipped + " matched families are in-place or not editable, so none can be " +
                      "edited in a family document. [Action: pick a loadable, editable family]"
                    : "The select block matched no family. [Action: list the loaded families and widen the " +
                      "selector]");
        }

        if (limit is { } value && families.Count > value)
        {
            families.RemoveRange(value, families.Count - value);
        }

        var warnings = new List<string>();
        if (unknownCategoryNames.Count > 0)
        {
            warnings.Add(
                "select.categoryNames ignored unknown categories: " +
                string.Join(", ", unknownCategoryNames) +
                " [Action: pass names from list_categories]");
        }

        foreach (var unmatched in pendingNames.OrderBy(name => name, StringComparer.Ordinal))
        {
            warnings.Add(
                "No loaded family named '" + unmatched + "'." +
                AgentNameSuggestions.Suffix(unmatched, loadedNames) +
                " [Action: fix the name or list_families to see the loaded families]");
        }

        if (skipped > 0)
        {
            warnings.Add(
                skipped + " matched family/families are in-place or not editable; they were skipped because " +
                "a family document cannot be opened for them.");
        }

        return new FamilySelection { Families = families, Warnings = warnings };
    }

    /// <summary>
    /// <summary>
    /// Ids of families that have at least one placed instance in the project
    /// (one collector pass per job expansion; placedOnly lives on it).
    /// </summary>
    private static HashSet<long> PlacedFamilyIds(Document document)
    {
        var ids = new HashSet<long>();
        foreach (var element in new FilteredElementCollector(document)
                     .WhereElementIsNotElementType()
                     .WhereElementIsViewIndependent())
        {
            if (element is not FamilyInstance instance)
            {
                continue;
            }

            var symbol = instance.Symbol;
            if (symbol?.Family is { } family)
            {
                ids.Add(family.Id.GetValue());
            }
        }

        return ids;
    }

    /// <summary>
    /// LoadFamily recreates the family element, so the unit key (and the family id)
    /// captured at expansion stops resolving after apply. Resolution order: the
    /// frozen unit key, then the post-load-back unique id stored in the undo payload,
    /// then the family name (family names are unique in a project).
    /// </summary>
    internal static bool TryResolveFamily(
        Document document,
        string unitKey,
        string? newFamilyUniqueId,
        string? familyName,
        out RevitFamily? family)
    {
        family = null;
        if (!string.IsNullOrWhiteSpace(unitKey))
        {
            family = FindByUniqueId(document, unitKey);
            if (family is not null)
            {
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(newFamilyUniqueId))
        {
            family = FindByUniqueId(document, newFamilyUniqueId!);
            if (family is not null)
            {
                return true;
            }
        }

        if (string.IsNullOrWhiteSpace(familyName))
        {
            return false;
        }

        foreach (var element in new FilteredElementCollector(document).OfClass(typeof(RevitFamily)))
        {
            if (element is RevitFamily candidate
                && string.Equals(candidate.Name, familyName!, StringComparison.OrdinalIgnoreCase))
            {
                family = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool Matches(
        RevitFamily family,
        IReadOnlyList<long>? familyIds,
        IReadOnlyList<string>? familyNames,
        IReadOnlyList<string>? familyUniqueIds,
        string? nameContains,
        IReadOnlyList<long>? categoryIds)
    {
        if (familyIds is { Count: > 0 } && !familyIds.Contains(family.Id.GetValue()))
        {
            return false;
        }

        if (familyNames is { Count: > 0 } && !MatchesAnyName(familyNames, family.Name))
        {
            return false;
        }

        if (familyUniqueIds is { Count: > 0 })
        {
            var matched = false;
            foreach (var uniqueId in familyUniqueIds)
            {
                if (string.Equals(uniqueId, family.UniqueId, StringComparison.Ordinal))
                {
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(nameContains)
            && family.Name.IndexOf(nameContains!, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        if (categoryIds is { Count: > 0 })
        {
            var categoryId = family.FamilyCategory?.Id.GetValue();
            if (!categoryId.HasValue || !categoryIds.Contains(categoryId.Value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesAnyName(IReadOnlyList<string> names, string familyName)
    {
        foreach (var name in names)
        {
            if (string.Equals(name?.Trim(), familyName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static RevitFamily? FindByUniqueId(Document document, string uniqueId)
    {
        try
        {
            return document.GetElement(uniqueId) as RevitFamily;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string JoinNames(IEnumerable<string> names) =>
        string.Join(", ", names.OrderBy(name => name, StringComparer.Ordinal));

    private static string NearestSuffix(IEnumerable<string> wanted, IEnumerable<string> loaded)
    {
        var nearest = new List<string>();
        foreach (var name in wanted)
        {
            foreach (var suggestion in AgentNameSuggestions.Nearest(name, loaded))
            {
                if (!nearest.Contains(suggestion))
                {
                    nearest.Add(suggestion);
                }
            }
        }

        return nearest.Count == 0
            ? string.Empty
            : " Nearest loaded name(s): " + string.Join(", ", nearest.Take(5)) + ".";
    }

    private static FamilySelection Error(string code, string detail) => new()
    {
        ErrorCode = code,
        ErrorDetail = detail
    };
}
