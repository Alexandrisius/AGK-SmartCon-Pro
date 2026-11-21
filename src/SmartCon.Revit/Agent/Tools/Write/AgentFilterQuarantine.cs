using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Builds and persists the quarantine snapshot of a view filter right before it
/// is deleted (#459, S.26). The snapshot mirrors get_filter_details: categories
/// with names, the rule tree (operators and values) and every applied view with
/// its two checkboxes and graphic overrides — everything a manual rebuild needs.
/// A store failure never blocks the deletion: it is reported as a warning.
/// </summary>
internal static class AgentFilterQuarantine
{
    internal const int MaxViewsInSnapshot = 500;

    /// <summary>
    /// Captures the filter state and saves it to the quarantine store. Returns
    /// the snapshot id, or null when the store is absent or refused the write.
    /// </summary>
    internal static string? TryQuarantine(
        Document doc,
        ParameterFilterElement filter,
        Dictionary<long, List<View>>? usage,
        string reason,
        IAgentFilterQuarantineStore? store,
        List<string> warnings)
    {
        if (store is null)
            return null;

        string? uniqueId = null;
        try
        {
            uniqueId = filter.UniqueId;
        }
        catch (Exception)
        {
            // A filter being deleted may already refuse UniqueId.
        }

        var views = usage is not null
            ? DescribeAppliedViews(filter, usage)
            : new List<object>();

        var snapshot = new AgentFilterSnapshot
        {
            Id = Guid.NewGuid().ToString("N"),
            FilterName = filter.Name,
            UniqueId = uniqueId,
            ElementId = filter.Id.GetValue(),
            DocumentId = SafeDocumentId(doc),
            RulesJson = AgentJson.Serialize(DescribeRules(doc, filter)).ToString(),
            CategoriesJson = AgentJson.Serialize(DescribeCategories(doc, filter)).ToString(),
            AppliedViewsJson = AgentJson.Serialize(views).ToString(),
            Reason = reason,
            CreatedAtUtc = DateTime.UtcNow
        };

        try
        {
            store.SaveAsync(snapshot, CancellationToken.None).GetAwaiter().GetResult();
            return snapshot.Id;
        }
        catch (Exception ex)
        {
            warnings.Add(
                "Quarantine snapshot for filter '" + snapshot.FilterName +
                "' was not saved: " + ex.GetType().Name + ": " + ex.Message +
                " [Action: proceed with the deletion only if the filter rules are " +
                "known, otherwise cancel and capture get_filter_details first]");
            return null;
        }
    }

    private static string? SafeDocumentId(Document doc)
    {
        try
        {
            return AgentDocumentIdentity.GetDocumentId(doc);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static object DescribeRules(Document doc, ParameterFilterElement filter)
    {
        try
        {
            return AgentFilterRuleDescriber.Describe(filter.GetElementFilter(), doc);
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object?> { ["type"] = "unreadable", ["error"] = ex.Message };
        }
    }

    private static object DescribeCategories(Document doc, ParameterFilterElement filter)
    {
        var categories = new List<object>();
        try
        {
            foreach (var categoryId in filter.GetCategories())
            {
                categories.Add(new Dictionary<string, object?>
                {
                    ["categoryId"] = categoryId.GetValue(),
                    ["name"] = Category.GetCategory(doc, categoryId)?.Name
                });
            }
        }
        catch (Exception ex)
        {
            categories.Add(new Dictionary<string, object?> { ["error"] = ex.Message });
        }

        return categories;
    }

    private static List<object> DescribeAppliedViews(
        ParameterFilterElement filter,
        Dictionary<long, List<View>> usage)
    {
        var views = new List<object>();
        if (!usage.TryGetValue(filter.Id.GetValue(), out var applied))
            return views;

        var filterId = filter.Id;
        foreach (var view in applied.Take(MaxViewsInSnapshot))
        {
            var row = new Dictionary<string, object?>
            {
                ["viewId"] = view.Id.GetValue(),
                ["viewName"] = SafeName(view),
                ["viewType"] = view.ViewType.ToString(),
                ["filterEnabled"] = AgentFilterViewState.ReadEnabled(view, filterId),
                ["visible"] = AgentFilterViewState.ReadVisible(view, filterId)
            };

            try
            {
                row["overrides"] = AgentFilterWriteHelper.Describe(
                    view.GetFilterOverrides(filterId));
            }
            catch (Exception)
            {
                // Overrides are optional detail of the snapshot.
            }

            views.Add(row);
        }

        if (applied.Count > MaxViewsInSnapshot)
        {
            views.Add(new Dictionary<string, object?>
            {
                ["truncated"] = true,
                ["totalAppliedViews"] = applied.Count
            });
        }

        return views;
    }

    private static string SafeName(View view)
    {
        try
        {
            return view.Name;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
