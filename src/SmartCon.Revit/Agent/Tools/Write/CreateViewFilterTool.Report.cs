using System.Globalization;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>Report and naming helpers of create_view_filter (#400 split).</summary>
public sealed partial class CreateViewFilterTool
{
    private static readonly char[] ForbiddenNameChars =
    {
        '\\', '/', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '\'', '~', '"'
    };

    private static string SanitizeName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var symbol in name)
        {
            builder.Append(Array.IndexOf(ForbiddenNameChars, symbol) >= 0 ? '-' : symbol);
        }

        var result = builder.ToString().Trim();
        return result.Length == 0 ? "SmartCon filter" : result;
    }

    private static string BuildDefaultName(
        Document doc,
        IReadOnlyList<ElementId> categoryIds,
        IReadOnlyList<FilterConditionSpec> conditions)
    {
        var builder = new StringBuilder("Фильтр ");
        var first = true;
        foreach (var id in categoryIds)
        {
            var category = Category.GetCategory(doc, id);
            var label = category?.Name ?? id.GetValue().ToString(CultureInfo.InvariantCulture);
            if (!first)
                builder.Append(", ");
            builder.Append(label);
            first = false;

            if (builder.Length > 60)
                break;
        }

        if (conditions.Count > 0)
        {
            var condition = conditions[0];
            builder.Append(" — ");
            builder.Append(condition.Field ?? condition.Parameter ?? "parameter");
            builder.Append(' ');
            builder.Append(condition.Op);
            if (condition.HasValue && condition.Value.ValueKind == JsonValueKind.String)
                builder.Append(' ').Append(condition.Value.GetString());
        }

        if (builder.Length > 90)
            return builder.ToString(0, 87) + "...";

        return builder.ToString();
    }

    private static string DescribeUnsupported(UnsupportedFilterParameter usage)
    {
        var names = new List<string>();
        foreach (var pair in usage.UnsupportedCategories)
            names.Add(pair.Name ?? pair.Id.ToString(CultureInfo.InvariantCulture));

        return "категории без этого параметра: " + string.Join(", ", names);
    }

    private static Dictionary<string, object?> FilterDetails(
        IReadOnlyList<UnsupportedFilterParameter> unsupported) =>
        new()
        {
            ["parameter"] = unsupported[0].ParameterName,
            ["parameterId"] = unsupported[0].ParameterId,
            ["unsupportedCategories"] = UnsupportedCategoryRows(unsupported),
            ["allUnsupported"] = unsupported
                .Select(usage => new
                {
                    parameter = usage.ParameterName,
                    parameterId = usage.ParameterId,
                    categoryCount = usage.UnsupportedCategories.Count
                })
                .ToList()
        };

    private static List<Dictionary<string, object?>> UnsupportedCategoryRows(
        IReadOnlyList<UnsupportedFilterParameter> unsupported)
    {
        var rows = new List<Dictionary<string, object?>>();
        var seen = new HashSet<long>();
        foreach (var usage in unsupported)
        {
            foreach (var pair in usage.UnsupportedCategories)
            {
                if (!seen.Add(pair.Id))
                    continue;

                rows.Add(new Dictionary<string, object?>
                {
                    ["id"] = pair.Id,
                    ["name"] = pair.Name
                });
            }
        }

        return rows;
    }

    private static bool IsApplied(View view, ElementId filterId)
    {
        foreach (var id in AgentFilterUsage.FiltersOf(view))
        {
            if (id == filterId.GetValue())
                return true;
        }

        return false;
    }

    private static ViewOverrideState DescribeEntry(ViewFilterState state, ElementId filterId)
    {
        foreach (var entry in state.Filters)
        {
            if (entry.FilterId == filterId.GetValue())
                return entry.Overrides;
        }

        return new ViewOverrideState();
    }

    private static SectionReport Section(string section, string status, string? reason) =>
        new() { Section = section, Status = status, Reason = reason };

    private static void Fail(
        WriteOutcome outcome,
        string errorCode,
        string message,
        List<SectionReport> sections)
    {
        outcome.Success = false;
        outcome.ErrorCode = errorCode;
        outcome.Message = message;
        Finish(outcome, sections);
    }

    private static void Finish(
        WriteOutcome outcome,
        List<SectionReport> sections,
        IReadOnlyList<object>? readBack = null)
    {
        outcome.Data["sections"] = sections.Count > 0 ? sections : null;
        if (readBack is not null)
            outcome.Data["readBack"] = readBack;
    }

    /// <summary>
    /// Facts of the finished call the result payload is built from.
    /// </summary>
    private sealed class FilterResult
    {
        public long FilterIdValue;
        public string FilterName = string.Empty;
        public bool Created;
        public bool Reused;
        public bool RolledBack;
        public bool DryRun;
        public bool Atomic;
        public ViewFilterMode Mode;
        public List<long> AppliedViewIds = new();
    }

    /// <summary>
    /// Drops the "filter created" change item and row: the filter element no
    /// longer exists after the rollback.
    /// </summary>
    private static void RemoveCreatedFilterRows(WriteOutcome outcome, long filterIdValue)
    {
        outcome.Items.RemoveAll(item =>
            string.Equals(item.ParameterName, "filter_created", StringComparison.Ordinal)
            && item.ElementId == filterIdValue);
        outcome.Batch.Changed.RemoveAll(report => report.Id == filterIdValue);
        if (outcome.AffectedCount > 0)
            outcome.AffectedCount--;
    }

    /// <summary>
    /// visibility=none keeps every element visible: without this line a success
    /// looked like "the elements are filtered out" (C4-09).
    /// </summary>
    private static void WarnIfAppliedButDisabled(
        WriteOutcome outcome,
        int viewChanged,
        ViewFilterMode mode)
    {
        if (viewChanged == 0 || mode != ViewFilterMode.None)
            return;

        outcome.Data["appliedButDisabled"] = true;
        outcome.Warnings.Add(
            "visibility=none: the filter is applied to " + viewChanged +
            " view(s) but DISABLED — nothing is hidden and no override is active. Pass " +
            "visibility=\"hide\" to hide the matching elements, or \"show\" to keep them " +
            "visible with the filter enabled.");
    }

    private static void PublishResultData(WriteOutcome outcome, FilterResult result)
    {
        // A dry run creates the filter only to read it back and then deletes it; a live
        // rollback deletes it too. A live filterId would invite a follow-up call
        // against a dead element (A4).
        var discardedProbe = result.Created && result.DryRun;
        var filterIdValue = result.FilterIdValue;
        outcome.Data["filterId"] = discardedProbe || result.RolledBack ? null : (long?)filterIdValue;
        if (discardedProbe)
        {
            outcome.Data["wouldCreateId"] = filterIdValue;
            // Every id this dry-run answer carries belongs to the discarded probe.
            outcome.Data["filterIdPreview"] = true;
        }

        if (result.RolledBack)
            outcome.Data["rolledBackId"] = filterIdValue;
        outcome.Data["filterName"] = result.FilterName;
        outcome.Data["atomic"] = result.Atomic;
        outcome.Data["created"] = result.Created && !result.DryRun && !result.RolledBack;
        outcome.Data["wouldCreate"] = discardedProbe;
        outcome.Data["reused"] = result.Reused;
        outcome.Data["visibility"] = AgentFilterViewState.ToWireName(result.Mode);
        outcome.Data["visibilitySemantics"] =
            "hide = filter enabled + matching elements hidden; show = filter enabled + " +
            "elements visible; none = filter added but disabled (elements visible).";
        if (discardedProbe)
        {
            outcome.Warnings.Add(
                "Dry run: the filter was created inside the rolled-back transaction and " +
                "nothing was saved. filterId is null; wouldCreateId and readBack[].previewFilterId " +
                "are ids of the discarded probe (filterIdPreview=true), not of a real element. " +
                "Run with dryRun=false to apply and get the real filterId.");
        }

        if (!result.DryRun && !result.RolledBack)
        {
            // Verified outside the transaction, after the commit (see Commit.cs).
            outcome.Data[PostCommitCheckKey] = (filterIdValue, result.AppliedViewIds);
        }
    }
}