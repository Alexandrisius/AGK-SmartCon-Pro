using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Threading;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Core helpers shared by every undo zone: the exception-safe revert wrapper,
/// the change-item report factory, element resolution (UniqueId first, then
/// ElementId) and the generic instance:/type: parameter restore.
/// </summary>
public sealed partial class UndoLastTool
{
    /// <summary>
    /// The rollback_job redirect when the newest write of the document is an
    /// applied job, not a change set (S.47, #532). With <paramref name="after"/>
    /// only jobs finished after that timestamp count. Null when the newest job
    /// wrote nothing or there is none.
    /// </summary>
    private string? LastAppliedJobRedirect(string documentId, CancellationToken ct, DateTime? after = null)
    {
        IReadOnlyList<SmartCon.Core.Models.Agent.Jobs.JobRecord>? recent = null;
        try
        {
            if (_jobs is not null)
                recent = AsyncBridge.RunSync(() => _jobs.ListRecentAsync(documentId, 5, ct));
        }
        catch (Exception)
        {
            // The store is unavailable: fall through to the plain undo path.
        }

        if (recent is null || recent.Count == 0)
            return null;

        foreach (var job in recent)
        {
            if (job.State is not (SmartCon.Core.Models.Agent.Jobs.JobState.Completed
                or SmartCon.Core.Models.Agent.Jobs.JobState.CompletedWithFailures
                or SmartCon.Core.Models.Agent.Jobs.JobState.CompletedUnverified
                or SmartCon.Core.Models.Agent.Jobs.JobState.Interrupted))
            {
                continue;
            }

            var finished = job.FinishedAt ?? job.UpdatedAt;
            if (after.HasValue && finished <= after.Value)
                continue;

            var totals = SmartCon.Core.Models.Agent.Jobs.JobJson
                .Deserialize<SmartCon.Core.Models.Agent.Jobs.JobTotals>(job.TotalsJson);
            var applied = (totals?.Done ?? 0) + (totals?.Mismatch ?? 0);
            if (applied <= 0)
                continue;

            return "The last write of this document is the JOB " + job.JobId.ToString("D") +
                   " (" + job.Spec.Action + ", " + applied + " unit(s) applied, finished " + finished +
                   ") - jobs are reverted by the job engine, not by undo_last. [Action: call " +
                   "rollback_job(jobId=\"" + job.JobId.ToString("D") + "\") - or list the recent jobs " +
                   "with get_job_status]";
        }

        return null;
    }

    private static (bool Ok, string? ErrorCode, string? Reason) RevertSafely(
        Document doc,
        Element element,
        ChangeItem item,
        bool force)
    {
        try
        {
            return Revert(doc, element, item, force);
        }
        catch (Exception ex)
        {
            return (false, "revert_failed", ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static WriteItemReport CreateReport(ChangeItem item) => new()
    {
        Id = item.ElementId,
        Name = item.ParameterName,
        OldValue = item.NewValue,
        NewValue = item.OldValue
    };

    private static HashSet<long>? ParseIdSet(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var ids = JsonSerializer.Deserialize<long[]>(json!);
            return ids is null ? null : new HashSet<long>(ids);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (bool, string?, string?) RevertParameter(
        Document doc,
        Element element,
        ChangeItem item,
        bool force)
    {
        var key = item.ParameterName!;
        var typeScope = key.StartsWith("type:", StringComparison.Ordinal);
        var name = key.Substring(key.IndexOf(':') + 1);

        var resolution = AgentParameterWriteHelper.Resolve(
            doc,
            element,
            name,
            null,
            typeScope ? "type" : "instance");

        if (!resolution.Ok)
            return (false, resolution.ErrorCode, resolution.Reason);

        var parameter = resolution.Parameter!;
        var current = AgentParameterWriteHelper.RawValue(parameter);
        if (string.Equals(current, item.OldValue, StringComparison.Ordinal))
            return (false, null, "The parameter already matches the recorded previous value.");

        if (!force && !string.Equals(current, item.NewValue, StringComparison.Ordinal))
        {
            return (false, "undo_conflict",
                "Parameter '" + name + "' has the value '" + current +
                "' instead of '" + item.NewValue + "' — it was changed after the " +
                "operation (conflict).");
        }

        if (!AgentParameterWriteHelper.TrySet(
                parameter, item.OldValue, "raw", out var errorCode, out var reason))
        {
            return (false, errorCode, reason);
        }

        return (true, null, null);
    }

    internal static Element? ResolveElement(Document doc, ChangeItem item)
    {
        if (!string.IsNullOrEmpty(item.UniqueId))
        {
            var byUniqueId = doc.GetElement(item.UniqueId);
            if (byUniqueId is not null)
                return byUniqueId;
        }

        return item.ElementId.HasValue
            ? doc.GetElement(ElementIdCompat.Create(item.ElementId.Value))
            : null;
    }

    private static bool IsWorksharingSkip(string? errorCode) =>
        errorCode is "element_owned_by_other_user" or "element_out_of_date"
            or "element_deleted_in_central" or "element_checkout_denied";

    private static string IdText(ElementId id) => id == ElementId.InvalidElementId
        ? string.Empty
        : id.GetValue().ToString(CultureInfo.InvariantCulture);
}
