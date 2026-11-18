using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;

namespace SmartCon.Revit.Agent.Tools;

/// <summary>
/// Resolves category names coming from the model to Revit categories. Accepts
/// the BuiltInCategory enum name (OST_Walls) and the localized display name,
/// which is what users type in Russian.
/// </summary>
internal static class AgentCategoryResolver
{
    internal static IReadOnlyList<ElementId> Resolve(
        Document doc,
        IReadOnlyList<string> names,
        ICollection<string> unknown)
    {
        var ids = new List<ElementId>(names.Count);
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (Enum.TryParse<BuiltInCategory>(name, ignoreCase: true, out var builtIn))
            {
                var byBuiltIn = RevitCategoryLookup(doc, builtIn);
                if (byBuiltIn is not null)
                {
                    AddOnce(ids, byBuiltIn.Id);
                    continue;
                }
            }

            Category? match = null;
            foreach (Category category in doc.Settings.Categories)
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

            AddOnce(ids, match.Id);
        }

        return ids;
    }

    private static Category? RevitCategoryLookup(Document doc, BuiltInCategory builtIn)
    {
        try
        {
            return Category.GetCategory(doc, builtIn);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void AddOnce(ICollection<ElementId> ids, ElementId id)
    {
        foreach (var existing in ids)
        {
            if (existing.GetValue() == id.GetValue())
                return;
        }

        ids.Add(id);
    }
}

internal static class AgentPaging
{
    internal const int DefaultLimit = 200;

    internal const int MaxLimit = 500;

    internal static int NormalizeOffset(int offset) => Math.Max(0, offset);

    internal static int NormalizeLimit(int limit, int fallback = DefaultLimit, int max = MaxLimit)
    {
        var value = limit <= 0 ? fallback : limit;
        return Math.Max(1, Math.Min(value, max));
    }

    internal static IReadOnlyList<T> Slice<T>(IReadOnlyList<T> items, int offset, int limit)
    {
        if (offset >= items.Count)
            return Array.Empty<T>();

        var count = Math.Min(limit, items.Count - offset);
        var result = new List<T>(count);
        for (var index = 0; index < count; index++)
            result.Add(items[offset + index]);
        return result;
    }
}