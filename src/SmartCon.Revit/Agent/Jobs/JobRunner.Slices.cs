using System.Diagnostics;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Jobs;

/// <summary>Preview, apply, cancel and completion slices.</summary>
public sealed partial class JobRunner
{
    private JobSliceResult RunPreviewSlice(JobRecord job, Document document, TimeSpan budget, long started)
    {
        var action = _actions[job.Spec.Action];
        var context = CreateContext(document, job.Spec.DocumentId, job.JobId, job.Spec, dryRun: true);
        var batchSize = EffectiveBatchSize(job, action);
        var singleBatchPerSlice = action.Scope is JobActionScope.FamilyDefinition or JobActionScope.RfaFile;
        var processed = 0;
        var cursor = 0;

        while (true)
        {
            var current = Sync(_store.GetAsync(job.JobId, CancellationToken.None));
            if (current is null || current.CancelRequested)
            {
                // Cancellation is honoured between batches: the state machine
                // settles the job on the next slice.
                break;
            }

            var items = Sync(_store.LoadItemsAsync(
                job.JobId,
                new[] { JobItemState.Pending },
                cursor,
                batchSize,
                CancellationToken.None));

            if (items.Count == 0)
            {
                break;
            }

            var updates = new List<JobItemUpdate>(items.Count);
            foreach (var item in items)
            {
                if (IsOverBudget(started, budget) && processed > 0)
                {
                    break;
                }

                cursor = item.Seq;
                var unit = ToUnit(item);
                JobUnitPreview preview;
                try
                {
                    preview = action.Preview(context, unit) ??
                              PreviewFailure("job_item_failed", "Action returned no preview");
                }
                catch (Exception ex)
                {
                    preview = PreviewFailure(
                        JobErrorCodes.ItemFailed,
                        ex.GetType().Name + ": " + ex.Message);
                }

                var state = !preview.Success
                    ? JobItemState.Failed
                    : preview.Skipped
                        ? JobItemState.Skipped
                        : JobItemState.Previewed;

                updates.Add(new JobItemUpdate
                {
                    Seq = item.Seq,
                    State = state,
                    PreviewJson = JobJson.Serialize(preview),
                    ExpectedJson = preview.ExpectedJson,
                    ActualJson = preview.ActualJson,
                    UndoJson = preview.UndoJson,
                    ErrorCode = preview.ErrorCode,
                    ErrorDetail = preview.ErrorDetail
                });
                processed++;
            }

            if (updates.Count > 0)
            {
                Sync(_store.UpdateItemsAsync(job.JobId, updates, CancellationToken.None));
            }

            if (IsOverBudget(started, budget) || singleBatchPerSlice)
            {
                break;
            }
        }

        var stillPending = Sync(_store.LoadItemsAsync(
            job.JobId,
            new[] { JobItemState.Pending },
            0,
            1,
            CancellationToken.None)).Count;

        if (stillPending > 0)
        {
            var latest = Sync(_store.GetAsync(job.JobId, CancellationToken.None));
            return new JobSliceResult
            {
                JobId = job.JobId,
                State = latest?.CancelRequested == true ? JobState.Cancelling : JobState.PreviewRunning,
                HasMoreWork = true,
                ProcessedThisSlice = processed
            };
        }

        var all = Sync(_store.LoadAllItemsAsync(job.JobId, CancellationToken.None));
        var envelope = BuildPreviewEnvelope(job, all, TakePreviewWarnings(job.JobId));
        Sync(_store.UpdatePreviewAsync(job.JobId, JobJson.Serialize(envelope), CancellationToken.None));
        Sync(_store.UpdateTotalsAsync(
            job.JobId,
            JobJson.Serialize(JobTotals.FromItems(all)),
            CancellationToken.None));

        var transition = Sync(_store.TransitionAsync(
            job.JobId,
            JobState.PreviewRunning,
            JobState.PreviewReady,
            null,
            null,
            CancellationToken.None));

        if (transition is not null)
        {
            TryFail(job, transition.Code, transition.Message);
            return new JobSliceResult { JobId = job.JobId, Error = transition, State = JobState.Failed };
        }

        Sync(_store.AppendEventAsync(
            job.JobId,
            JobEventTypes.PreviewCompleted,
            "{\"count\":" + envelope.Count + ",\"willChange\":" + envelope.WillChangeCount + "}",
            CancellationToken.None));

        _wantsPump = false;
        return new JobSliceResult
        {
            JobId = job.JobId,
            State = JobState.PreviewReady,
            HasMoreWork = false,
            ProcessedThisSlice = processed
        };
    }

    private JobSliceResult RunApplySlice(JobRecord job, Document document, TimeSpan budget, long started)
    {
        var action = _actions[job.Spec.Action];
        var context = CreateContext(document, job.Spec.DocumentId, job.JobId, job.Spec, dryRun: false);
        var batchSize = EffectiveBatchSize(job, action);
        var singleBatchPerSlice = action.Scope is JobActionScope.FamilyDefinition or JobActionScope.RfaFile;
        var processed = 0;
        var lastSeq = 0;
        var changedAny = false;
        var savedAny = false;

        if (job.CancelRequested)
        {
            Sync(_store.TransitionAsync(
                job.JobId,
                JobState.Running,
                JobState.Cancelling,
                null,
                null,
                CancellationToken.None));

            return new JobSliceResult
            {
                JobId = job.JobId,
                State = JobState.Cancelling,
                HasMoreWork = true,
                ProcessedThisSlice = processed
            };
        }

        while (true)
        {
            var current = Sync(_store.GetAsync(job.JobId, CancellationToken.None));
            if (current is null || current.CancelRequested)
            {
                break;
            }

            var items = Sync(_store.LoadItemsAsync(
                job.JobId,
                new[] { JobItemState.Previewed },
                lastSeq,
                batchSize,
                CancellationToken.None));

            if (items.Count == 0)
            {
                break;
            }

            if (IsOverBudget(started, budget) && processed > 0)
            {
                break;
            }

            var result = ApplyBatch(job, document, action, context, items);
            processed += items.Count;
            changedAny |= result.Changed;
            savedAny |= result.Saved;
            lastSeq = items[items.Count - 1].Seq;

            if (result.Error is not null)
            {
                _wantsPump = false;
                TryFail(job, result.Error.Code, result.Error.ToMessage());
                return new JobSliceResult
                {
                    JobId = job.JobId,
                    State = JobState.Failed,
                    ProcessedThisSlice = processed,
                    Error = result.Error
                };
            }

            if (IsOverBudget(started, budget) || singleBatchPerSlice)
            {
                break;
            }
        }

        var pending = Sync(_store.LoadItemsAsync(
            job.JobId,
            new[] { JobItemState.Previewed },
            0,
            1,
            CancellationToken.None)).Count;

        if (pending > 0)
        {
            var latest = Sync(_store.GetAsync(job.JobId, CancellationToken.None));
            return new JobSliceResult
            {
                JobId = job.JobId,
                State = latest?.CancelRequested == true ? JobState.Cancelling : JobState.Running,
                HasMoreWork = true,
                ProcessedThisSlice = processed
            };
        }

        if (changedAny && !savedAny)
        {
            var saveError = SaveNow(job, document, LastSeqOf(job));
            if (saveError is not null)
            {
                _wantsPump = false;
                TryFail(job, saveError.Code, saveError.ToMessage());
                return new JobSliceResult
                {
                    JobId = job.JobId,
                    State = JobState.Failed,
                    Error = saveError
                };
            }
        }

        return FinalizeJob(job, document, action, processed);
    }

    private JobSliceResult RunCancelSlice(JobRecord job, Document document, TimeSpan budget, long started)
    {
        if (!job.Spec.Options.RollbackOnError)
        {
            var done = Sync(_store.TransitionAsync(
                job.JobId,
                JobState.Cancelling,
                JobState.Cancelled,
                JobErrorCodes.CancelledByUser,
                JobErrors.Cancelled().ToMessage(),
                CancellationToken.None));

            if (done is not null)
            {
                TryFail(job, done.Code, done.Message);
                return new JobSliceResult { JobId = job.JobId, Error = done, State = JobState.Failed };
            }

            Sync(_store.MarkFinishedAsync(
                job.JobId,
                JobState.Cancelled,
                JobErrorCodes.CancelledByUser,
                JobErrors.Cancelled().ToMessage(),
                CancellationToken.None));
            Sync(_store.AppendEventAsync(job.JobId, JobEventTypes.Cancelled, null, CancellationToken.None));
            Sync(_store.UpdateTotalsAsync(
                job.JobId,
                JobJson.Serialize(JobTotals.FromItems(
                    Sync(_store.LoadAllItemsAsync(job.JobId, CancellationToken.None)))),
                CancellationToken.None));

            _wantsPump = false;
            return new JobSliceResult { JobId = job.JobId, State = JobState.Cancelled, HasMoreWork = false };
        }

        var outcome = RunUndoBatches(job, document, budget, started, singleBatchPerSlice: true);
        if (outcome.HasMoreWork)
        {
            return new JobSliceResult
            {
                JobId = job.JobId,
                State = JobState.Cancelling,
                HasMoreWork = true,
                ProcessedThisSlice = outcome.Processed
            };
        }

        var target = outcome.UndoFailed > 0 || outcome.SaveError is not null
            ? JobState.RollbackPartial
            : JobState.RolledBack;
        var transition = Sync(_store.TransitionAsync(
            job.JobId,
            JobState.Cancelling,
            target,
            outcome.UndoFailed > 0 ? JobErrorCodes.RollbackFailed : null,
            outcome.UndoFailed > 0
                ? JobErrors.RollbackFailed(outcome.UndoFailed + " units could not be restored").ToMessage()
                : null,
            CancellationToken.None));

        if (transition is not null)
        {
            TryFail(job, transition.Code, transition.Message);
            return new JobSliceResult { JobId = job.JobId, Error = transition, State = JobState.Failed };
        }

        Sync(_store.MarkFinishedAsync(
            job.JobId,
            target,
            outcome.UndoFailed > 0 ? JobErrorCodes.RollbackFailed : null,
            outcome.UndoFailed > 0 ? outcome.UndoFailed + " units could not be restored" : null,
            CancellationToken.None));
        Sync(_store.AppendEventAsync(
            job.JobId,
            JobEventTypes.RollbackCompleted,
            "{\"restored\":" + outcome.Restored + ",\"failed\":" + outcome.UndoFailed + "}",
            CancellationToken.None));
        Sync(_store.UpdateTotalsAsync(
            job.JobId,
            JobJson.Serialize(JobTotals.FromItems(
                Sync(_store.LoadAllItemsAsync(job.JobId, CancellationToken.None)))),
            CancellationToken.None));

        _wantsPump = false;
        return new JobSliceResult
        {
            JobId = job.JobId,
            State = target,
            HasMoreWork = false,
            ProcessedThisSlice = outcome.Processed
        };
    }

    private void MarkApplyingAsFailed(JobRecord job, IReadOnlyList<BatchPlan> plans)
    {
        var updates = plans.Select(plan => new JobItemUpdate
        {
            Seq = plan.Item.Seq,
            State = JobItemState.Failed,
            ErrorCode = "job_batch_rolled_back",
            ErrorDetail = "The batch transaction was rolled back; nothing was applied"
        }).ToList();

        if (updates.Count > 0)
        {
            Sync(_store.UpdateItemsAsync(job.JobId, updates, CancellationToken.None));
        }
    }

    private int LastSeqOf(JobRecord job)
    {
        var items = Sync(_store.LoadAllItemsAsync(job.JobId, CancellationToken.None));
        return items.Count == 0 ? 0 : items[items.Count - 1].Seq;
    }

    private static JobPreview BuildPreviewEnvelope(
        JobRecord job,
        IReadOnlyList<JobItem> items,
        IReadOnlyList<string> expansionWarnings)
    {
        var units = items.Select(item => new JobUnit
        {
            Seq = item.Seq,
            UnitKey = item.UnitKey,
            ArgsJson = item.ArgsJson
        });

        var selectorHash = JobPreviewDigest.ComputeSelectorHash(units);
        var specHash = JobPreviewDigest.ComputeSpecHash(job.Spec.SpecJson);
        var expiresAt = DateTime.UtcNow.AddMinutes(PreviewTtlMinutes);
        var willChange = items.Count(item => item.State is JobItemState.Previewed or JobItemState.Applying);
        var skipped = items.Count(item => item.State == JobItemState.Skipped);
        var failed = items.Count(item => item.State == JobItemState.Failed);

        var digest = JobPreviewDigest.Compute(
            job.Spec.Action,
            job.Spec.DocumentId,
            selectorHash,
            specHash,
            items.Count,
            willChange,
            expiresAt);

        var sample = items
            .Where(item => item.State != JobItemState.Skipped)
            .Take(5)
            .Select(item =>
            {
                var preview = JobJson.Deserialize<JobUnitPreview>(item.PreviewJson);
                return new JobPreviewSample
                {
                    UnitKey = item.UnitKey,
                    Label = item.Label,
                    OldValue = Describe(preview?.ActualJson),
                    NewValue = Describe(preview?.ExpectedJson),
                    WillChange = item.State == JobItemState.Previewed
                };
            })
            .ToList();

        var warnings = new List<string>(expansionWarnings);
        if (failed > 0)
        {
            warnings.Add(failed + " units failed the dry run and will be reported as failed, not applied");
        }

        // Per-unit dry-run warnings (e.g. "instance-scope targets are not verifiable")
        // must reach the operator, not only the stored item.
        foreach (var item in items)
        {
            var itemPreview = JobJson.Deserialize<JobUnitPreview>(item.PreviewJson);
            if (itemPreview is null)
            {
                continue;
            }

            foreach (var warning in itemPreview.Warnings)
            {
                if (!warnings.Contains(warning))
                {
                    warnings.Add(warning);
                }
            }

            if (warnings.Count >= 15)
            {
                break;
            }
        }

        if (items.Count > 500)
        {
            warnings.Add("The job touches " + items.Count + " units; progress is saved every batch");
        }

        return new JobPreview
        {
            Action = job.Spec.Action,
            DocumentId = job.Spec.DocumentId,
            Count = items.Count,
            WillChangeCount = willChange,
            SkipCount = skipped,
            FailedCount = failed,
            Sample = sample,
            Warnings = warnings,
            SelectorHash = selectorHash,
            SpecHash = specHash,
            Digest = digest,
            ExpiresAtUtc = expiresAt
        };
    }

    private static string? Describe(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return json!.Length <= 120 ? json : json.Substring(0, 120) + "…";
    }

    private static JobUnit ToUnit(JobItem item) => new()
    {
        Seq = item.Seq,
        UnitKey = item.UnitKey,
        ArgsJson = item.ArgsJson,
        Label = item.Label
    };

    private static JobUnitPreview PreviewFailure(string code, string detail) => new()
    {
        ErrorCode = code,
        ErrorDetail = detail
    };

    private static int EffectiveBatchSize(JobRecord job, IJobAction action)
    {
        var configured = Math.Max(1, job.Spec.Options.BatchSize);
        return action.Scope is JobActionScope.FamilyDefinition or JobActionScope.RfaFile
            ? 1
            : configured;
    }

    private static bool IsOverBudget(long started, TimeSpan budget)
    {
        var elapsed = Stopwatch.GetTimestamp() - started;
        return elapsed * 1000.0 / Stopwatch.Frequency >= budget.TotalMilliseconds;
    }

    private sealed class BatchPlan
    {
        public required JobItem Item { get; init; }

        public required JobUnitPreview Preview { get; init; }

        public JobError? Error { get; init; }
    }

    private sealed class ApplyBatchOutcome
    {
        public bool Changed { get; init; }

        public bool Saved { get; init; }

        public JobError? Error { get; init; }
    }
}