using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Post-commit verification of create_view_filter. Inside the transaction every
/// view accepted the filter; what matters is whether it is still there once the
/// transaction is committed. A view template that includes "V/G Overrides
/// Filters" hands the filter list of its views to the template, so a filter can
/// be gone right after a successful commit — that must be a failed row, not a
/// changed one.
/// </summary>
public sealed partial class CreateViewFilterTool
{
    /// <summary>
    /// Key of the per-call verification data in <see cref="WriteOutcome.Data"/>;
    /// removed before the data is serialized.
    /// </summary>
    internal const string PostCommitCheckKey = "postCommitFilterCheck";

    protected override void AfterTransaction(
        Document document,
        JsonElement args,
        bool dryRun,
        WriteOutcome outcome,
        CancellationToken ct)
    {
        var hasCheck = outcome.Data.TryGetValue(PostCommitCheckKey, out var stored);
        if (hasCheck)
            outcome.Data.Remove(PostCommitCheckKey);

        // After a rolled-back commit the rows are already reported as skipped.
        if (dryRun || !hasCheck || !outcome.ModelChanged)
            return;

        if (stored is not ValueTuple<long, List<long>> check)
            return;

        VerifyAppliedAfterCommit(document, outcome, check.Item1, check.Item2);
    }

    /// <summary>
    /// Moves every view whose filter list no longer contains the filter from
    /// changed[] to failed[] (<c>filter_not_applied</c>) and drops its change
    /// item. Runs outside any transaction: nothing is written to the model.
    /// </summary>
    internal static void VerifyAppliedAfterCommit(
        Document document,
        WriteOutcome outcome,
        long filterId,
        IReadOnlyList<long> viewIds)
    {
        var lost = new List<long>();
        foreach (var viewId in viewIds)
        {
            if (document.GetElement(ElementIdCompat.Create(viewId)) is not View view
                || !AgentFilterUsage.FiltersOf(view).Contains(filterId))
            {
                lost.Add(viewId);
            }
        }

        if (lost.Count == 0)
            return;

        var lostIds = new HashSet<long>(lost);
        var moved = outcome.Batch.Changed
            .Where(row => row.Action == "filter_assigned" && row.Id.HasValue && lostIds.Contains(row.Id.Value))
            .ToList();
        foreach (var row in moved)
        {
            row.Action = null;
            row.ErrorCode = "filter_not_applied";
            row.Reason = "The filter was added to the view, but it is not in the view's filters " +
                         "after the commit: a view template that includes V/G Overrides " +
                         "Filters replaces the view's own list (see view_controlled_by_template).";
            outcome.Batch.Changed.Remove(row);
            outcome.Batch.Failed.Add(row);

            outcome.Items.RemoveAll(item =>
                item.ElementId == row.Id
                && string.Equals(item.ParameterName, "filter", StringComparison.Ordinal));
            if (outcome.AffectedCount > 0)
                outcome.AffectedCount--;
        }

        outcome.Data["filterNotAppliedViewIds"] = lost;
        outcome.Warnings.Add(
            "The filter was not kept by " + lost.Count + " view(s) after the commit: [" +
            string.Join(", ", lost.Take(10)) + "]" +
            (lost.Count > 10 ? " (+" + (lost.Count - 10) + " more)" : string.Empty) +
            ". The view's filter list belongs to its view template; add the filter to the " +
            "template or detach 'filters' from it.");
        outcome.Message = "The filter was not kept by " + lost.Count + " view(s) after the commit. " +
                          "See failed[] (filter_not_applied).";
    }
}
