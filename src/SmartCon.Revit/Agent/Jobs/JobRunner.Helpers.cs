using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent.Jobs;

namespace SmartCon.Revit.Agent.Jobs;

/// <summary>
/// Store-facing helpers of the job runner: context creation, unit
/// normalization, save preflight, stall warnings, the failure writer and the
/// synchronous store bridge.
/// </summary>
public sealed partial class JobRunner
{
    private JobActionContext CreateContext(
        Document document,
        string documentId,
        Guid jobId,
        JobSpec spec,
        bool dryRun) =>
        new()
        {
            Document = document,
            DocumentId = documentId,
            JobId = jobId,
            DryRun = dryRun,
            SpecJson = spec.SpecJson,
            CancellationToken = CancellationToken.None
        };

    private static IReadOnlyList<JobUnit> Normalize(IReadOnlyList<JobUnit> units)
    {
        var result = new List<JobUnit>(units.Count);
        for (var i = 0; i < units.Count; i++)
        {
            var unit = units[i];
            result.Add(new JobUnit
            {
                Seq = i + 1,
                UnitKey = unit.UnitKey,
                ArgsJson = unit.ArgsJson,
                Label = unit.Label
            });
        }

        return result;
    }

    private JobError? PreflightSaveable(Document document, JobOptions options)
    {
        if (!JobSavePolicyDecider.RequiresSaveableDocument(options.SavePolicy))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(document.PathName))
        {
            return JobErrors.DocumentNotSaved();
        }

        if (document.IsReadOnlyFile)
        {
            return JobErrors.DocumentReadOnly();
        }

        return null;
    }

    private void WarnWhenStalled(JobRecord job)
    {
        if (!JobStateMachine.NeedsPump(job.State))
        {
            return;
        }

        var idleSeconds = (DateTime.UtcNow - job.UpdatedAt).TotalSeconds;
        if (idleSeconds > 30)
        {
            SmartConLogger.Warn(
                "Job " + job.JobId.ToString("D") + " has not advanced for " +
                idleSeconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) +
                "s (state " + job.State + ") " +
                "[Action: check that Revit has focus; the pump resumes on the next idle cycle, " +
                "or cancel the job with cancel_job]");
        }
    }

    private void TryFail(JobRecord job, string code, string detail)
    {
        using var _scope = SmartConLogger.BeginScope("AgentJob", ("Method", "TryFail"));

        try
        {
            var latest = Sync(_store.GetAsync(job.JobId, CancellationToken.None));
            if (latest is not null && JobStateMachine.IsTerminal(latest.State))
            {
                return;
            }

            lock (_gate)
            {
                _previewWarnings.Remove(job.JobId);
            }

            Sync(_store.MarkFinishedAsync(job.JobId, JobState.Failed, code, detail, CancellationToken.None));
            Sync(_store.AppendEventAsync(job.JobId, JobEventTypes.Failed, null, CancellationToken.None));
        }
        catch (Exception ex)
        {
            SmartConLogger.Error(
                "Could not mark job " + job.JobId.ToString("D") + " as failed: " +
                ex.GetType().Name + ": " + ex.Message +
                " [Action: inspect agent.db jobs table and cancel the job manually]");
        }
    }

    private IReadOnlyList<string> TakePreviewWarnings(Guid jobId)
    {
        lock (_gate)
        {
            if (_previewWarnings.TryGetValue(jobId, out var warnings))
            {
                _previewWarnings.Remove(jobId);
                return warnings;
            }
        }

        return Array.Empty<string>();
    }

    internal static JobPreview? ParsePreview(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JobJson.Deserialize<JobPreview>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Store calls complete synchronously in the SQLite implementation, and the
    /// runner is only ever called on the Revit API thread.
    /// </summary>
    private static T Sync<T>(Task<T> task) => task.GetAwaiter().GetResult();

    private static void Sync(Task task) => task.GetAwaiter().GetResult();
}
