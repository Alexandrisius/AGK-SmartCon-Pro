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
            steps = Steps(result, apply: true),
            planReadBack = new
            {
                executedSteps = result.Steps.Count(step => step.ReadBackExecuted),
                expectationsChecked = result.ExpectationsChecked,
                verified = result.Verified.Count,
                mismatches = result.Mismatches.Count,
                toolExpectationsChecked = tool.Expected,
                toolVerified = tool.Verified,
                toolMismatched = tool.Mismatched
            },
            verified,
            mismatches,
            verifiedTotal = verified.Count,
            mismatchesTotal = mismatches.Count,
            hint = BuildApplyHint(result, tool, digestAutoFilled)
        }, SerializerOptions);

        return ToolResult.Ok(
            payload,
            "Plan applied: " + result.AffectedCount + " change(s); " +
            verified.Count + " verified (" + tool.Verified + " tool-level), " +
            mismatches.Count + " mismatch(es)." +
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

        if (result.Mismatches.Count + tool.Mismatched > 0)
        {
            parts.Add(
                "Read-back found mismatches. Review them and offer undo_last for the " +
                "affected step.");
        }
        else if (result.ExpectationsChecked == 0 && tool.Expected == 0)
        {
            parts.Add(
                "No plan read-back expectations and no tool-internal read-back were declared, " +
                "so there are no verified values; read the model back with the matching list_* " +
                "or get_* tool.");
        }
        else if (result.ExpectationsChecked == 0)
        {
            parts.Add(
                "Tool-internal read-back verified " + tool.Verified + " value(s)" +
                (tool.NotVerifiable > 0 ? " (" + tool.NotVerifiable + " not verifiable)" : "") +
                ". No plan-level readBackExpectations were declared; add them to verify extra " +
                "conditions with a separate read tool.");
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
        if (result?.Data is not { ValueKind: JsonValueKind.Object } data
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
            items = StepItems(step, apply),
            toolReadBack = ToolReadBack(step, apply),
            planReadBack = new
            {
                executed = step.ReadBackExecuted,
                expectationsChecked = step.ExpectationsChecked,
                verified = step.VerifiedCount,
                mismatches = step.MismatchCount
            },
            fidelity = step.FidelityChecked
                ? new
                {
                    expected = step.FidelityExpected,
                    matched = step.FidelityMatched
                }
                : null,
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
        if (result?.Data is not { ValueKind: JsonValueKind.Object } data)
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
