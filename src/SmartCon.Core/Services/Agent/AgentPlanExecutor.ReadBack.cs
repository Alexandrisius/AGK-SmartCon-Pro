using System.Text.Json;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Core.Services.Agent;

public sealed partial class AgentPlanExecutor
{
    private async Task<(
        IReadOnlyList<string> Verified,
        IReadOnlyList<string> Mismatches,
        IReadOnlyList<string> NotVerifiable)> RunReadBackAsync(
        IReadOnlyList<AgentPlanStep> steps,
        IReadOnlyList<AgentPlanStepResult> results,
        AgentPlanExecutionContext execution,
        CancellationToken ct)
    {
        var verified = new List<string>();
        var mismatches = new List<string>();
        var notVerifiable = new List<string>();

        foreach (var step in steps)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(step.ReadBackTool))
                continue;

            var readBackToolName = step.ReadBackTool!;
            var result = results.First(item => item.StepId == step.StepId);
            result.ExpectationsDeclared = step.ReadBackExpectations.Count;
            var tool = GetTool(readBackToolName);
            if (tool is null)
            {
                result.ReadBackFailed = true;
                mismatches.Add(step.StepId + ": read-back tool '" + readBackToolName +
                               "' is not registered");
                continue;
            }

            var readBackArguments = step.ArgumentsReadBackJson;
            if (ContainsStepReference(readBackArguments))
            {
                var byId = results.ToDictionary(item => item.StepId, StringComparer.Ordinal);
                readBackArguments = TryResolveStepReferences(readBackArguments, byId, out var error)
                                    ?? step.ArgumentsReadBackJson;
                if (error is not null)
                {
                    result.ReadBackFailed = true;
                    mismatches.Add(step.StepId + ": read-back {" + StepReferenceKey +
                                   "} reference cannot be resolved: " + error);
                    continue;
                }
            }

            var readBack = await ExecuteToolAsync(
                    tool, readBackArguments, false, execution, ct)
                .ConfigureAwait(false);
            result.ReadBackResult = readBack;
            result.ReadBackExecuted = true;

            if (!readBack.Success || readBack.Data is not { } data)
            {
                // Nothing was compared: the report must say that verification
                // was requested but could not run, not that none was requested
                // (review r1 #1).
                result.ReadBackFailed = true;
                mismatches.Add(step.StepId + ": read-back failed (" +
                               (readBack.ErrorCode ?? "unknown") + ")" +
                               DescribeAppliedCreatedIds(result.ApplyResult));
                continue;
            }

            result.ExpectationsChecked = step.ReadBackExpectations.Count;
            foreach (var expectation in step.ReadBackExpectations)
            {
                var resolution = ResolvePath(data, expectation.Path);
                if (!resolution.Resolved && resolution.Status == PathStatus.UnsupportedForm)
                {
                    // Syntax the resolver does not understand compares nothing:
                    // reported as not verifiable, never as a value mismatch.
                    result.NotVerifiableCount++;
                    notVerifiable.Add(step.StepId + ": " + expectation.Path +
                                      " — not verifiable (path form not supported: " +
                                      resolution.Reason + ")");
                    continue;
                }

                // A supported path that addressed nothing (no such row or key)
                // did not confirm the expectation: it is a mismatch like a
                // differing value, with the reason attached (review r1 #2).
                var actual = resolution.Resolved ? resolution.Value : null;
                if (!resolution.Resolved
                    || actual is null
                    || !string.Equals(actual, expectation.Expected, StringComparison.Ordinal))
                {
                    result.MismatchCount++;
                    if (!resolution.Resolved)
                        result.NotFoundMismatchCount++;
                    mismatches.Add(step.StepId + ": " + expectation.Path + " expected '" +
                                   expectation.Expected + "', actual '" + (actual ?? "null") + "'" +
                                   (resolution.Resolved
                                       ? string.Empty
                                       : " — object not found in read-back payload (" +
                                         resolution.Reason + ")"));
                }
                else
                {
                    result.VerifiedCount++;
                    verified.Add(step.StepId + ": " + expectation.Path + " = " + actual);
                }
            }

            // A read-back that ran without expectations compared nothing: it is
            // reported as "read-back executed, nothing to compare" by the apply
            // report, never counted as verified (review r2, WA-6).
        }

        return (verified, mismatches, notVerifiable);
    }
}
