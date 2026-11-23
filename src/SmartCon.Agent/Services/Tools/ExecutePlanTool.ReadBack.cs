using System.Text.Json;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;

namespace SmartCon.Agent.Services.Tools;

public sealed partial class ExecutePlanTool
{
    private static ToolResult ApplyResult(
        AgentPlanExecutionResult result,
        bool digestAutoFilled)
    {
        var tool = SumToolReadBack(result);
        var verified = new List<string>(result.Verified);
        var mismatches = new List<string>(result.Mismatches);
        foreach (var step in result.Steps)
        {
            var totals = ToolReadBackOf(step.ApplyResult);
            if (totals.Verified > 0)
            {
                verified.Add(
                    step.StepId + ": tool read-back verified " + totals.Verified + " value(s)");
            }

            if (totals.Mismatched > 0)
            {
                mismatches.Add(
                    step.StepId + ": tool read-back mismatched " + totals.Mismatched + " value(s)");
            }
        }

        var failedReadBacks = result.Steps
            .Where(step => step.ReadBackFailed)
            .Select(step => step.StepId)
            .ToList();

        // Verification was requested as soon as a read-back was declared on any
        // step — even when its tool then failed — or a tool verified on its own.
        // Counting only the compared expectations reported a failed read-back as
        // "verification not requested" next to mismatches=1 (review r1 #1).
        var verificationRequested =
            result.Steps.Any(step => step.ReadBackExecuted || step.ReadBackFailed)
            || result.ExpectationsChecked > 0
            || tool.Expected > 0
            || mismatches.Count > 0;
        var payload = JsonSerializer.SerializeToElement(new
        {
            dryRun = false,
            planDigest = result.PlanDigest,
            planDigestAlgorithm = "sha256",
            planDigestAutoFilled = digestAutoFilled,
            appliedWithoutPreview = digestAutoFilled,
            affected = result.AffectedCount,
            previewIncomplete = result.PreviewIncomplete,
            notPreviewed = result.NotPreviewed,
            warning = result.PreviewIncomplete
                ? "Steps " + string.Join(", ", result.NotPreviewed) +
                  " were applied without a preview: they depend on earlier write steps, so " +
                  "the dry run could not exercise them. Verify their result with a read tool."
                : null,
            steps = Steps(result, apply: true),
            planReadBack = new
            {
                executedSteps = result.Steps.Count(step => step.ReadBackExecuted),
                declared = result.Steps.Sum(step => step.ExpectationsDeclared),
                expectationsChecked = result.ExpectationsChecked,
                verified = result.Verified.Count,
                mismatches = result.Mismatches.Count,
                notVerifiable = result.NotVerifiable.Count,
                failedReadBackSteps = failedReadBacks,
                toolExpectationsChecked = tool.Expected,
                toolVerified = tool.Verified,
                toolMismatched = tool.Mismatched
            },
            verified,
            mismatches,
            notVerifiable = result.NotVerifiable,
            verifiedTotal = verified.Count,
            mismatchesTotal = mismatches.Count,
            notVerifiableTotal = result.NotVerifiable.Count,
            hint = BuildApplyHint(result, tool, digestAutoFilled)
        }, SerializerOptions);

        // "0 verified" on an apply without read-back expectations read as "checked
        // and confirmed nothing" — the honest wording is that verification was not
        // requested at all (C4-18). With mismatches the verified/mismatch line is
        // always printed, whatever was declared (review r1 #1).
        var verificationText = verificationRequested
            ? verified.Count + " verified (" + tool.Verified + " tool-level), " +
              mismatches.Count + " mismatch(es)" +
              (result.NotVerifiable.Count > 0
                  ? ", " + result.NotVerifiable.Count + " not verifiable"
                  : string.Empty)
            : "verification not requested";

        return ToolResult.Ok(
            payload,
            "Plan applied: " + result.AffectedCount + " change(s); " + verificationText + "." +
            (result.PreviewIncomplete
                ? " " + result.NotPreviewed.Count + " step(s) applied without preview."
                : string.Empty) +
            (digestAutoFilled ? " Applied without a separate preview." : string.Empty),
            result.AffectedCount);
    }

    private static string BuildApplyHint(
        AgentPlanExecutionResult result,
        ToolReadBackTotals tool,
        bool digestAutoFilled)
    {
        var parts = new List<string>();
        if (digestAutoFilled)
        {
            parts.Add(
                "No planDigest was passed; it was derived from the approved arguments " +
                "(planDigestAutoFilled=true), so this apply ran without a separate dry-run preview.");
        }

        if (result.PreviewIncomplete)
        {
            parts.Add(
                "Steps " + string.Join(", ", result.NotPreviewed) + " ran without a preview " +
                "(they depend on earlier write steps). Confirm their result with the matching " +
                "read tool.");
        }

        var failedReadBacks = result.Steps
            .Where(step => step.ReadBackFailed)
            .Select(step => step.StepId)
            .ToList();
        var valueMismatches = result.Steps.Sum(step => step.MismatchCount) + tool.Mismatched;

        // A read-back that could not run is not a differing value: there is
        // nothing to review or undo yet, the changes are simply unverified.
        if (failedReadBacks.Count > 0)
        {
            parts.Add(
                "The read-back could not run for step(s) " + string.Join(", ", failedReadBacks) +
                " (see mismatches[]): the changes of those steps are not verified — re-read " +
                "them with the matching read tool before reporting success.");
        }

        if (valueMismatches > 0)
        {
            parts.Add(
                "Read-back found mismatches. Review them and offer undo_last for the " +
                "affected step.");
        }

        if (failedReadBacks.Count > 0 || valueMismatches > 0)
            return string.Join(" ", parts);

        var declared = result.Steps.Sum(step => step.ExpectationsDeclared);
        if (!result.Steps.Any(step => step.ReadBackExecuted) && declared == 0 && tool.Expected == 0)
        {
            parts.Add(
                "No plan read-back expectations and no tool-internal read-back were declared " +
                "(verification not requested); read the model back with the matching list_* " +
                "or get_* tool.");
        }
        else if (declared == 0 && tool.Expected == 0)
        {
            parts.Add(
                "The read-back tool ran but no readBackExpectations were declared, so no " +
                "value was compared; declare them to verify conditions.");
        }
        else if (result.ExpectationsChecked == 0)
        {
            parts.Add(
                "Tool-internal read-back verified " + tool.Verified + " value(s)" +
                (tool.NotVerifiable > 0 ? " (" + tool.NotVerifiable + " not verifiable)" : "") +
                ". No plan-level readBackExpectations were declared; add them to verify extra " +
                "conditions with a separate read tool.");
        }
        else if (result.NotVerifiable.Count > 0)
        {
            parts.Add(
                result.Verified.Count + " expectation(s) verified, " +
                result.NotVerifiable.Count + " could not be verified: their path form is not " +
                "supported (see notVerifiable[]). Re-check those values with a supported " +
                "path form.");
        }
        else
        {
            parts.Add(
                "All " + (result.ExpectationsChecked + tool.Expected) +
                " read-back expectation(s) verified.");
        }

        return string.Join(" ", parts);
    }

    private static ToolReadBackTotals SumToolReadBack(AgentPlanExecutionResult result)
    {
        var totals = new ToolReadBackTotals();
        foreach (var step in result.Steps)
        {
            var stepTotals = ToolReadBackOf(step.ApplyResult);
            totals.Expected += stepTotals.Expected;
            totals.Verified += stepTotals.Verified;
            totals.Mismatched += stepTotals.Mismatched;
            totals.NotVerifiable += stepTotals.NotVerifiable;
        }

        return totals;
    }

    private static ToolReadBackTotals ToolReadBackOf(ToolResult? result)
    {
        // A tool collects its read-back inside its own transaction. When the tool
        // reports a failure that transaction may have been rolled back, and the
        // block then describes values that are no longer in the model: summed, it
        // would pass a rolled-back change off as verified (review VA-6).
        if (result is null
            || !result.Success
            || result.Data is not { ValueKind: JsonValueKind.Object } data
            || !data.TryGetProperty("readBack", out var readBack)
            || readBack.ValueKind != JsonValueKind.Object)
        {
            return new ToolReadBackTotals();
        }

        return new ToolReadBackTotals
        {
            Expected = ReadInt(readBack, "expected"),
            Verified = ReadInt(readBack, "verified"),
            Mismatched = ReadInt(readBack, "mismatched"),
            NotVerifiable = ReadInt(readBack, "notVerifiable")
        };
    }

    private static int ReadInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var parsed)
            ? parsed
            : 0;

    private sealed class ToolReadBackTotals
    {
        public int Expected { get; set; }

        public int Verified { get; set; }

        public int Mismatched { get; set; }

        public int NotVerifiable { get; set; }
    }

    private static IReadOnlyList<object> Steps(AgentPlanExecutionResult result, bool apply) =>
        result.Steps.Select(step => (object)new
        {
            stepId = step.StepId,
            toolName = step.ToolName,
            status = Status(step, apply),
            changed = step.Changed,
            skipped = step.Skipped,
            failed = step.Failed,
            items = TransformItems(step, apply),
            toolReadBack = ToolReadBack(step, apply),
            planReadBack = new
            {
                executed = step.ReadBackExecuted,
                failed = step.ReadBackFailed,
                declared = step.ExpectationsDeclared,
                expectationsChecked = step.ExpectationsChecked,
                verified = step.VerifiedCount,
                mismatches = step.MismatchCount,
                notVerifiable = step.NotVerifiableCount
            },
            fidelity = step.FidelityChecked
                ? new
                {
                    expected = step.FidelityExpected,
                    matched = step.FidelityMatched,
                    note = step.FidelityNote
                }
                : null,
            error = StepError(step, apply),
            preflightSkipped = step.PreflightSkipped,
            preflightDeferred = step.PreflightDeferred,
            previewSkipped = step.PreviewSkipped,
            previewSkipReason = step.PreviewSkipReason
        }).ToList();

    private static string Status(AgentPlanStepResult step, bool apply)
    {
        if (apply)
        {
            if (step.ApplyResult is null)
                return "not_run";

            return step.ApplyResult.Success ? "applied" : "failed";
        }

        if (step.DryRunResult is not null)
            return step.DryRunResult.Success ? "previewed" : "preview_failed";

        return step.PreviewSkipped ? "preview_skipped" : "not_previewed";
    }

    private static JsonElement? ToolReadBack(AgentPlanStepResult step, bool apply)
    {
        var result = apply
            ? step.ApplyResult ?? step.DryRunResult
            : step.DryRunResult ?? step.ApplyResult;
        if (result is null
            || !result.Success
            || result.Data is not { ValueKind: JsonValueKind.Object } data)
            return null;

        return data.TryGetProperty("readBack", out var readBack) ? readBack : null;
    }

    /// <summary>
    /// Per-item diff from the step's dry run (or apply): changed / skipped / failed
    /// cards with id, name, before and after. Without it the model has to re-read
    /// the model just to show the user what will change (live dogfood P1-4).
    /// </summary>
    private static JsonElement? StepItems(AgentPlanStepResult step, bool apply)
    {
        // Preview shows the dry-run diff; the applied report must show the apply
        // result, otherwise every item still says dryRun=true (feedback #2).
        var result = apply
            ? step.ApplyResult ?? step.DryRunResult
            : step.DryRunResult ?? step.ApplyResult;
        if (result?.Data is not { ValueKind: JsonValueKind.Object } data)
            return null;

        if (!data.TryGetProperty("changed", out _)
            && !data.TryGetProperty("skipped", out _)
            && !data.TryGetProperty("failed", out _))
        {
            return null;
        }

        return data;
    }

}
