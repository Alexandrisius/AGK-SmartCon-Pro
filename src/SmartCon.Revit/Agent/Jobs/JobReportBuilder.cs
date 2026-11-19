using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Jobs;

/// <summary>
/// Builds the operator report from the stored rows and an independent read-back.
/// The report never trusts the intent journal alone (B.1, B.3).
/// </summary>
public sealed class JobReportBuilder
{
    private readonly IJobStore _store;
    private readonly IReadOnlyDictionary<string, IJobAction> _actions;

    public JobReportBuilder(IJobStore store, IEnumerable<IJobAction> actions)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        if (actions is null) throw new ArgumentNullException(nameof(actions));

        var map = new Dictionary<string, IJobAction>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in actions)
        {
            map[action.Name] = action;
        }

        _actions = map;
    }

    public JobReport? Build(
        Document document,
        Guid jobId,
        int offset,
        int limit,
        bool readBack,
        out JobError? error)
    {
        error = null;
        var job = _store.GetAsync(jobId, CancellationToken.None).GetAwaiter().GetResult();
        if (job is null)
        {
            error = new JobError(
                JobErrorCodes.JobNotFound,
                "Job '" + jobId.ToString("D") + "' was not found",
                "call get_job_status to list recent jobs");
            return null;
        }

        var documentId = AgentDocumentIdentity.GetDocumentId(document);
        if (!string.Equals(job.Spec.DocumentId, documentId, StringComparison.Ordinal))
        {
            // Reading (and especially writing mismatch states) for another document's
            // job would corrupt that job with values from the wrong model.
            error = JobErrors.DocumentMismatch(job.Spec.DocumentId, documentId);
            return null;
        }

        var items = _store.LoadAllItemsAsync(jobId, CancellationToken.None).GetAwaiter().GetResult();

        var mismatches = 0;
        if (readBack && _actions.TryGetValue(job.Spec.Action, out var action))
        {
            mismatches = VerifyItems(document, job, action, items);
            if (mismatches > 0)
            {
                items = _store.LoadAllItemsAsync(jobId, CancellationToken.None).GetAwaiter().GetResult();
            }
        }

        var totals = JobTotals.FromItems(items);
        var maxSeq = items.Count == 0 ? 0 : items[items.Count - 1].Seq;
        var unsaved = maxSeq - job.LastSavedCursor;
        var isVolatile = job.Spec.Options.SavePolicy == JobSavePolicy.Never;
        var saved = !isVolatile && unsaved <= 0 &&
                    (job.LastSavedCursor > 0 || job.LastSavedAt is not null || totals.Done == 0);

        var warnings = new List<string>();
        if (isVolatile)
        {
            warnings.Add("savePolicy=never: applied changes are in memory only");
        }
        else if (unsaved > 0 && totals.Done > 0)
        {
            warnings.Add(unsaved + " unit(s) are applied but not saved yet");
        }

        if (mismatches > 0)
        {
            warnings.Add(mismatches + " unit(s) no longer match the expected values in the model");
        }

        if (job.ErrorCode is not null)
        {
            warnings.Add(job.ErrorCode + ": " + job.ErrorDetail);
        }

        var page = items
            .Skip(Math.Max(offset, 0))
            .Take(limit <= 0 ? 100 : limit)
            .Select(ToReportItem)
            .ToList();

        return new JobReport
        {
            JobId = job.JobId,
            Action = job.Spec.Action,
            DocumentId = job.Spec.DocumentId,
            State = job.State,
            Saved = saved,
            Volatile = isVolatile,
            LastSavedCursor = job.LastSavedCursor,
            LastSavedAt = job.LastSavedAt,
            CreatedAt = job.CreatedAt,
            FinishedAt = job.FinishedAt,
            Totals = totals,
            Items = page,
            Warnings = warnings,
            ErrorCode = job.ErrorCode,
            ErrorDetail = job.ErrorDetail,
            Truthful = mismatches == 0,
            MismatchCount = mismatches,
            HasMore = offset + page.Count < items.Count,
            NextOffset = offset + page.Count
        };
    }

    public string ToCsv(JobReport report)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("seq,unitKey,label,status,expected,actual,verified,errorCode,errorDetail,attempts");

        foreach (var item in report.Items)
        {
            builder.Append(item.Seq).Append(',')
                .Append(Escape(item.UnitKey)).Append(',')
                .Append(Escape(item.Label)).Append(',')
                .Append(Escape(item.Status)).Append(',')
                .Append(Escape(item.Expected)).Append(',')
                .Append(Escape(item.Actual)).Append(',')
                .Append(item.Verified ? "true" : "false").Append(',')
                .Append(Escape(item.ErrorCode)).Append(',')
                .Append(Escape(item.ErrorDetail)).Append(',')
                .Append(item.Attempts)
                .AppendLine();
        }

        return builder.ToString();
    }

    private int VerifyItems(
        Document document,
        JobRecord job,
        IJobAction action,
        IReadOnlyList<JobItem> items)
    {
        var context = new JobActionContext
        {
            Document = document,
            DocumentId = job.Spec.DocumentId,
            JobId = job.JobId,
            DryRun = true,
            SpecJson = job.Spec.SpecJson
        };

        var updates = new List<JobItemUpdate>();
        var mismatches = 0;

        foreach (var item in items.Where(item => item.State is JobItemState.Done or JobItemState.Skipped))
        {
            var preview = JobJson.Deserialize<JobUnitPreview>(item.PreviewJson);
            if (preview is null)
            {
                continue;
            }

            JobUnitVerification verification;
            try
            {
                verification = action.Verify(
                    context,
                    new JobUnit
                    {
                        Seq = item.Seq,
                        UnitKey = item.UnitKey,
                        ArgsJson = item.ArgsJson,
                        Label = item.Label
                    },
                    preview);
            }
            catch (Exception ex)
            {
                verification = new JobUnitVerification
                {
                    Verified = false,
                    MismatchReason = ex.GetType().Name + ": " + ex.Message
                };
            }

            if (verification.Verified)
            {
                continue;
            }

            mismatches++;
            updates.Add(new JobItemUpdate
            {
                Seq = item.Seq,
                State = JobItemState.Mismatch,
                ActualJson = verification.ActualJson,
                ErrorCode = JobErrorCodes.Mismatch,
                ErrorDetail = verification.MismatchReason ?? "read-back mismatch in the report"
            });
        }

        if (updates.Count > 0)
        {
            _store.UpdateItemsAsync(job.JobId, updates, CancellationToken.None).GetAwaiter().GetResult();
            _store.AppendEventAsync(
                job.JobId,
                "mismatch_detected",
                "{\"count\":" + updates.Count + "}",
                CancellationToken.None).GetAwaiter().GetResult();
        }

        return mismatches;
    }

    private static JobReportItem ToReportItem(JobItem item)
    {
        var preview = JobJson.Deserialize<JobUnitPreview>(item.PreviewJson);
        return new JobReportItem
        {
            Seq = item.Seq,
            UnitKey = item.UnitKey,
            Label = item.Label,
            Status = item.State.ToString(),
            Expected = preview?.ExpectedJson,
            Actual = item.ActualJson ?? preview?.ActualJson,
            Undo = item.UndoJson,
            ErrorCode = item.ErrorCode,
            ErrorDetail = item.ErrorDetail,
            Verified = item.State is JobItemState.Done or JobItemState.Skipped,
            Attempts = item.Attempts
        };
    }

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var text = value!.Replace("\"", "\"\"");
        return text.IndexOf(',') >= 0 || text.IndexOf('"') >= 0 || text.IndexOf('\n') >= 0
            ? "\"" + text + "\""
            : text;
    }
}