using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Revit.Agent.Tools;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Jobs;

/// <summary>
/// Worksharing half of the batch runner: ownership of every unit target is taken
/// once before the batch, a unit that stayed unavailable is skipped per item.
/// </summary>
public sealed partial class JobRunner
{
    private JobError? CheckoutBatchTargets(
        Document document,
        AgentWorksharingScope worksharing,
        IReadOnlyList<JobItem> items)
    {
        if (!worksharing.NeedsCentral)
            return null;

        var targets = new HashSet<ElementId>();
        foreach (var item in items)
        {
            foreach (var id in UnitTargets(document, item.ArgsJson))
                targets.Add(id);
        }

        worksharing.Checkout(targets, CancellationToken.None);
        return worksharing.FailureCode is null
            ? null
            : new JobError(
                worksharing.FailureCode,
                worksharing.FailureMessage ?? "The central model is unavailable.",
                "retry after the central model is reachable");
    }

    /// <summary>
    /// Element ids a job unit points at: numeric …Id arguments plus an
    /// ownerUniqueId (a type-scope write belongs to the type element).
    /// </summary>
    private static IEnumerable<ElementId> UnitTargets(Document document, string argsJson)
    {
        foreach (var (_, raw) in AgentJsonIds.ScanJson(argsJson))
        {
            var id = ElementIdCompat.Create(raw);
            if (document.GetElement(id) is not null)
                yield return id;
        }

        foreach (var uniqueId in AgentJsonIds.ScanUniqueIds(argsJson))
        {
            var element = document.GetElement(uniqueId);
            if (element is not null)
                yield return element.Id;
        }
    }

    private static WorksharingBlock? FindUnavailable(
        Document document,
        AgentWorksharingScope worksharing,
        JobItem item)
    {
        if (!worksharing.NeedsCentral)
            return null;

        foreach (var id in UnitTargets(document, item.ArgsJson))
        {
            if (worksharing.TryGetBlock(id, out var block))
                return block;
        }

        return null;
    }
}
