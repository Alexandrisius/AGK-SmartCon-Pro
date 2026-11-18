using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Models.Agent.Eval;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.IntegrationTests.Agent.Eval;

internal sealed partial class AgentEvalRunner
{
    private static readonly HashSet<string> WriteToolNames = new(StringComparer.Ordinal)
    {
        "rename_views",
        "rename_levels",
        "apply_view_template",
        "set_view_range",
        "set_view_crop",
        "set_view_visibility",
        "configure_views",
        "copy_view_settings",
        "set_parameters",
        "remap_parameter_values",
        "undo_last",
        "discard_change_set"
    };

    /// <summary>
    /// Executes the model's dry-run plan with dryRun=false, verifies that the
    /// model changed, then reverts the created change set with undo_last and
    /// verifies the original state. Failure reasons are collected on the result.
    /// </summary>
    private static async Task ApplyAndUndoAsync(
        AgentEvalTask task,
        IReadOnlyList<IAgentTool> toolset,
        AgentEvalTaskResult result,
        EvalModelSeed seed,
        Document document,
        CancellationToken ct)
    {
        var dryCall = result.Calls.LastOrDefault(call =>
                         call.Success
                         && WriteToolNames.Contains(call.ToolName)
                         && call.DryRun
                         && task.ExpectedTools.Contains(call.ToolName, StringComparer.Ordinal))
                     ?? result.Calls.LastOrDefault(call =>
                         call.Success && WriteToolNames.Contains(call.ToolName));

        if (dryCall is null)
        {
            result.Failure ??= "no successful dry-run write call to apply";
            result.ReadBackMatched = false;
            return;
        }

        var tool = toolset.FirstOrDefault(candidate =>
            string.Equals(candidate.Definition.Name, dryCall.ToolName, StringComparison.Ordinal));
        if (tool is null)
        {
            result.Failure ??= "applied tool no longer exists";
            result.ReadBackMatched = false;
            return;
        }

        var before = CaptureState(task.Id, document, seed);
        var applyArguments = SetDryRunFalse(dryCall.ArgumentsJson);
        var applyResult = await ExecuteRawAsync(tool, applyArguments, seed, ct);
        result.Calls.Add(new AgentEvalCall
        {
            ToolName = tool.Definition.Name,
            ArgumentsJson = applyArguments,
            Success = applyResult.Success,
            ErrorCode = applyResult.ErrorCode,
            AffectedCount = applyResult.AffectedCount,
            DryRun = false,
            ResultJson = ToolResultBudget.Serialize(applyResult)
        });

        var after = CaptureState(task.Id, document, seed);
        var changed = !string.Equals(before, after, StringComparison.Ordinal);

        if (dryCall.AffectedCount != applyResult.AffectedCount)
            result.DryRunFidelity = false;

        var undoArguments = BuildUndoArguments(applyResult, document);
        var undoTool = toolset.FirstOrDefault(candidate =>
            string.Equals(candidate.Definition.Name, "undo_last", StringComparison.Ordinal));
        var undoResult = undoTool is null
            ? ToolResult.Fail("unknown_tool", "undo_last is not registered.")
            : await ExecuteRawAsync(undoTool, undoArguments, seed, ct);

        result.Calls.Add(new AgentEvalCall
        {
            ToolName = "undo_last",
            ArgumentsJson = undoArguments,
            Success = undoResult.Success,
            ErrorCode = undoResult.ErrorCode,
            AffectedCount = undoResult.AffectedCount,
            DryRun = false,
            ResultJson = ToolResultBudget.Serialize(undoResult)
        });

        var final = CaptureState(task.Id, document, seed);
        result.ReadBackMatched = changed
                                 && undoResult.Success
                                 && string.Equals(before, final, StringComparison.Ordinal);

        if (!applyResult.Success)
            result.Failure ??= "apply failed: " + (applyResult.ErrorCode ?? "unknown");
        else if (!changed)
            result.Failure ??= "apply did not change the model";
        else if (!undoResult.Success)
            result.Failure ??= "undo failed: " + (undoResult.ErrorCode ?? "unknown");
        else if (!string.Equals(before, final, StringComparison.Ordinal))
            result.Failure ??= "undo did not restore the original model state";
    }

    private static async Task<ToolResult> ExecuteRawAsync(
        IAgentTool tool,
        string argumentsJson,
        EvalModelSeed seed,
        CancellationToken ct)
    {
        JsonElement args;
        using (var parsed = JsonDocument.Parse(
                   string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson))
        {
            args = parsed.RootElement.Clone();
        }

        var context = new AgentToolContext(new ImmediateAwaitableEvent(), seed.DocumentId, Guid.Empty)
        {
            WriteApproved = true
        };

        try
        {
            return await tool.ExecuteAsync(context, args, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Fail("tool_exception", ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static string SetDryRunFalse(string argumentsJson)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(
            string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        if (node is System.Text.Json.Nodes.JsonObject obj)
        {
            obj["dryRun"] = false;
            return obj.ToJsonString();
        }

        return "{\"dryRun\":false}";
    }

    private static string BuildUndoArguments(ToolResult applyResult, Document document)
    {
        var changeSetId = ReadDataString(applyResult, "changeSetId");
        if (string.IsNullOrWhiteSpace(changeSetId))
            return "{\"dryRun\":false}";

        return "{\"changeSetId\":\"" + changeSetId + "\",\"dryRun\":false}";
    }

    private static string? ReadDataString(ToolResult result, string property)
    {
        if (result.Data is not { ValueKind: JsonValueKind.Object } data
            || !data.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static void Evaluate(
        AgentEvalTask task,
        AgentEvalTaskResult result,
        EvalModelSeed seed,
        Document document)
    {
        var failures = new List<string>();
        var environmentLimited = result.Calls.Any(call =>
            !call.Success
            && call.ErrorCode is not null
            && task.AllowErrorCodes.Contains(call.ErrorCode, StringComparer.Ordinal));

        foreach (var expected in task.ExpectedTools)
        {
            var satisfied = result.Calls.Any(call => call.Success
                                                     && string.Equals(
                                                         call.ToolName, expected,
                                                         StringComparison.Ordinal));
            if (!satisfied && task.AlternativeTools.Count > 0)
            {
                satisfied = result.Calls.Any(call =>
                    call.Success
                    && task.AlternativeTools.Contains(call.ToolName, StringComparer.Ordinal));
            }

            if (!satisfied && !environmentLimited)
                failures.Add("expected tool not called successfully: " + expected);
        }

        foreach (var call in result.Calls)
        {
            if (call.Success
                && WriteToolNames.Contains(call.ToolName)
                && !task.ExpectedTools.Contains(call.ToolName, StringComparer.Ordinal)
                && !task.AlternativeTools.Contains(call.ToolName, StringComparer.Ordinal)
                && task.Kind != AgentEvalTaskKind.WriteApplyUndo)
            {
                failures.Add("unexpected write tool: " + call.ToolName);
            }
        }

        if (result.UnauthorizedWrite)
            failures.Add("write with dryRun=false in a dry-run-only task");

        if (!environmentLimited)
        {
            foreach (var key in task.ExpectedDataKeys)
            {
                if (!result.Calls.Any(call => call.Success && HasDataKey(call.ResultJson, key)))
                    failures.Add("data key missing: " + key);
            }
        }

        var changed = DryRunChangedCount(result, task);
        if (task.ExpectedChanged.HasValue && changed != task.ExpectedChanged.Value)
            failures.Add("dry-run changed = " + (changed?.ToString() ?? "n/a") + ", expected " +
                         task.ExpectedChanged.Value);
        if (task.MinChanged.HasValue && (changed ?? 0) < task.MinChanged.Value)
            failures.Add("dry-run changed = " + (changed?.ToString() ?? "n/a") + ", expected at least " +
                         task.MinChanged.Value);

        if (string.IsNullOrWhiteSpace(result.Answer))
            failures.Add("empty final answer");

        failures.AddRange(CheckSpecific(task, result, seed, document));

        if (failures.Count > 0)
        {
            result.Passed = false;
            result.Failure = result.Failure is null
                ? string.Join("; ", failures)
                : result.Failure + "; " + string.Join("; ", failures);
        }
        else
        {
            result.Passed = true;
        }
    }

    private static IEnumerable<string> CheckSpecific(
        AgentEvalTask task,
        AgentEvalTaskResult result,
        EvalModelSeed seed,
        Document document)
    {
        switch (task.Check)
        {
            case AgentEvalCheck.LevelsCountConsistent:
            {
                var levels = ReadDataInt(result, "list_levels", "total");
                var overview = ReadDataInt(result, "get_project_overview", "levelCount");
                if (levels is null || overview is null)
                {
                    yield return "level counts are missing from tool results";
                    yield break;
                }

                if (levels.Value != overview.Value)
                {
                    yield return "list_levels.total (" + levels + ") != overview.levelCount (" +
                                 overview + ")";
                    yield break;
                }

                if (result.Answer is null
                    || !result.Answer.Contains(levels.Value.ToString(), StringComparison.Ordinal))
                {
                    yield return "answer does not mention the level count " + levels.Value;
                }

                break;
            }

            case AgentEvalCheck.TypeNameContains:
            {
                var total = ReadDataInt(result, "query_elements", "total");
                if (total is null || total.Value < 1)
                {
                    yield return "query_elements found no wall by type name";
                    yield break;
                }

                if (!QueryResultContainsElement(result, seed.WallId))
                    yield return "query_elements did not return the seeded wall " + seed.WallId;
                break;
            }

            case AgentEvalCheck.CompactViewSettings:
            {
                var call = result.Calls.LastOrDefault(candidate =>
                    candidate.Success
                    && string.Equals(candidate.ToolName, "get_view_settings", StringComparison.Ordinal));
                if (call?.ResultJson is null)
                {
                    yield return "get_view_settings result is missing";
                    yield break;
                }

                if (HasDataKey(call.ResultJson, "hiddenCategories"))
                    yield return "get_view_settings returned the full hiddenCategories list";

                var count = ReadDataInt(call.ResultJson, "hiddenCategoryCount");
                if (count is null)
                    yield return "hiddenCategoryCount is missing from the compact summary";
                else if (result.Answer is null
                         || !result.Answer.Contains(count.Value.ToString(), StringComparison.Ordinal))
                    yield return "answer does not mention the hidden category count " + count.Value;
                break;
            }

            case AgentEvalCheck.ReferenceViewPlan:
            {
                if (!result.Calls.Any(call => call.Success
                                              && (call.ToolName == "copy_view_settings"
                                                  || call.ToolName == "configure_views")))
                {
                    yield return "neither copy_view_settings nor configure_views was called";
                }

                var renameCovered = result.Calls.Any(call => call.Success
                                                             && (call.ToolName == "rename_views"
                                                                 || (call.ToolName == "configure_views"
                                                                     && call.ArgumentsJson.Contains(
                                                                         "newName", StringComparison.Ordinal))));
                if (!renameCovered
                    && (result.Answer is null
                        || (result.Answer.IndexOf("переимен", StringComparison.OrdinalIgnoreCase) < 0
                            && result.Answer.IndexOf("имя", StringComparison.OrdinalIgnoreCase) < 0)))
                {
                    yield return "plan does not cover the rename or explain that names are separate";
                }

                break;
            }

            case AgentEvalCheck.HonestUnsupported:
            {
                if (result.Calls.Any(call => call.Success
                                             && WriteToolNames.Contains(call.ToolName)))
                {
                    yield return "a write tool was executed for an unsupported request";
                }

                if (result.Answer is null || !LooksLikeRefusal(result.Answer))
                    yield return "answer does not explain that the request is unsupported";
                break;
            }
        }
    }

    private static bool LooksLikeRefusal(string answer)
    {
        foreach (var phrase in new[]
                 {
                     "не могу", "не поддержив", "нет инструмент", "недоступн", "невозможно",
                     "unsupported", "не реализован", "не предусмотрен", "не умею"
                 })
        {
            if (answer.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        return false;
    }

    private static int? DryRunChangedCount(AgentEvalTaskResult result, AgentEvalTask task)
    {
        var candidates = task.ExpectedTools.Concat(task.AlternativeTools).ToList();
        foreach (var expected in candidates)
        {
            if (!WriteToolNames.Contains(expected))
                continue;

            var call = result.Calls.LastOrDefault(candidate =>
                candidate.Success
                && candidate.DryRun
                && string.Equals(candidate.ToolName, expected, StringComparison.Ordinal));
            if (call?.ResultJson is null)
                continue;

            var count = ReadDataArrayLength(call.ResultJson, "changed");
            if (count.HasValue)
                return count;
        }

        return null;
    }

    private static bool HasDataKey(string? resultJson, string key)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
            return false;

        try
        {
            using var document = JsonDocument.Parse(resultJson!);
            return document.RootElement.TryGetProperty("data", out var data)
                   && data.ValueKind == JsonValueKind.Object
                   && data.TryGetProperty(key, out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int? ReadDataInt(string? resultJson, string key)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(resultJson!);
            if (document.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty(key, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var parsed))
            {
                return parsed;
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static int? ReadDataInt(
        AgentEvalTaskResult result,
        string toolName,
        string key)
    {
        var call = result.Calls.LastOrDefault(candidate =>
            candidate.Success
            && string.Equals(candidate.ToolName, toolName, StringComparison.Ordinal));
        return ReadDataInt(call?.ResultJson, key);
    }

    private static int? ReadDataArrayLength(string resultJson, string key)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            if (document.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty(key, out var value)
                && value.ValueKind == JsonValueKind.Array)
            {
                return value.GetArrayLength();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static bool QueryResultContainsElement(AgentEvalTaskResult result, long elementId)
    {
        var call = result.Calls.LastOrDefault(candidate =>
            candidate.Success
            && string.Equals(candidate.ToolName, "query_elements", StringComparison.Ordinal));
        if (call?.ResultJson is null)
            return false;

        try
        {
            using var document = JsonDocument.Parse(call.ResultJson);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("elements", out var elements)
                || elements.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var element in elements.EnumerateArray())
            {
                if (element.TryGetProperty("elementId", out var id)
                    && id.ValueKind == JsonValueKind.Number
                    && id.TryGetInt64(out var parsed)
                    && parsed == elementId)
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }
}
