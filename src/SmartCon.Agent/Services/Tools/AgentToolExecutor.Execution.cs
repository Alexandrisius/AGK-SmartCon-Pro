using System.Text.Json;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Services.Tools;

public sealed partial class AgentToolExecutor
{
    private const int DefaultToolTimeoutMs = 30000;
    private const int TimeoutGraceMs = 5000;
    private const int MaxUnknownToolSuggestions = 3;

    /// <summary>
    /// Validates the model arguments and, when the first pass fails, applies the
    /// tolerant repair. Returns the repaired arguments, the applied repairs, or a
    /// ready <c>invalid_arguments</c> failure containing both the remaining schema
    /// errors and the attempted repairs.
    /// </summary>
    private static PreparedArguments PrepareArguments(
        AgentToolDefinition definition,
        JsonElement args)
    {
        var raw = args.ValueKind == JsonValueKind.Undefined ? "{}" : args.GetRawText();
        var errors = AgentToolSchemaValidator.Validate(definition.InputSchemaJson, raw);
        if (errors.Count == 0)
            return new PreparedArguments { Arguments = args };

        var repaired = AgentToolInputRepair.Repair(definition.InputSchemaJson, args);
        if (repaired.Changed)
        {
            var repairedRaw = repaired.Arguments.ValueKind == JsonValueKind.Undefined
                ? "{}"
                : repaired.Arguments.GetRawText();
            errors = AgentToolSchemaValidator.Validate(
                definition.InputSchemaJson, repairedRaw);

            if (errors.Count == 0)
            {
                SmartConLogger.Info(
                    "Agent tool input repaired: tool=" + definition.Name +
                    ", repairs=[" + string.Join("; ", repaired.Repairs) + "]");
                return new PreparedArguments
                {
                    Arguments = repaired.Arguments,
                    Repairs = repaired.Repairs
                };
            }
        }

        var data = JsonSerializer.SerializeToElement(new
        {
            errors,
            repairs = repaired.Repairs,
            hint = "Fix the listed paths; see the tool schema."
        });
        var message = "Invalid arguments for tool '" + definition.Name + "': " +
                      string.Join("; ", errors) +
                      ". [Action: fix the listed paths; see the tool schema]";

        return new PreparedArguments
        {
            Failure = ToolResult.Fail("invalid_arguments", message, data: data)
        };
    }

    private ToolResult BuildUnknownToolResult(string name)
    {
        var suggestions = SuggestToolNames(name);
        var data = JsonSerializer.SerializeToElement(new
        {
            suggestions,
            hint = "Use find_tools to locate the right tool."
        });

        var message = "Unknown tool '" + name + "'.";
        if (suggestions.Count > 0)
        {
            message += " Did you mean " +
                       string.Join(" or ", suggestions.Select(item => "'" + item + "'")) + "?";
        }

        message += " Use only the tools listed in this request; find_tools can locate the rest.";
        return ToolResult.Fail("unknown_tool", message, data: data);
    }

    private IReadOnlyList<string> SuggestToolNames(string name)
    {
        var threshold = System.Math.Max(2, (name.Length + 1) / 2);
        return _registry.GetDefinitions()
            .Select(definition => (
                definition.Name,
                Distance: LevenshteinDistance(name, definition.Name)))
            .Where(candidate => candidate.Distance <= threshold)
            .OrderBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.Name, StringComparer.Ordinal)
            .Take(MaxUnknownToolSuggestions)
            .Select(candidate => candidate.Name)
            .ToList();
    }

    private static int LevenshteinDistance(string left, string right)
    {
        if (left.Length == 0)
            return right.Length;
        if (right.Length == 0)
            return left.Length;

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var column = 0; column <= right.Length; column++)
            previous[column] = column;

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var cost = char.ToLowerInvariant(left[row - 1])
                           == char.ToLowerInvariant(right[column - 1])
                    ? 0
                    : 1;
                current[column] = System.Math.Min(
                    System.Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + cost);
            }

            var swap = previous;
            previous = current;
            current = swap;
        }

        return previous[right.Length];
    }

    /// <summary>
    /// Runs the tool with its per-definition timeout. The confirmation gate runs
    /// before this method, so waiting for the user never consumes the budget.
    /// Cancellation is cooperative: a tool that ignores the token receives a
    /// five-second grace period and is then reported as timed out, never killed.
    /// </summary>
    private async Task<ToolResult> ExecuteWithTimeoutAsync(
        IAgentTool tool,
        AgentToolContext context,
        JsonElement args,
        CancellationToken ct)
    {
        // The catalog card carries the real per-tool timeout; the raw
        // tool.Definition.TimeoutMs is 0 for most tools, so reading it directly
        // silently fell back to the 30 s default even for cards that declare
        // 60-180 s (the ФОП session lost two get_family_parameters calls to
        // exactly this gap).
        var effectiveDefinition = AgentToolCatalog.Enrich(tool.Definition);
        var timeoutMs = effectiveDefinition.TimeoutMs > 0
            ? effectiveDefinition.TimeoutMs
            : DefaultToolTimeoutMs;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var execution = tool.ExecuteAsync(context, args, linked.Token);

        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(timeoutMs, delayCancellation.Token);
        var completed = await Task.WhenAny(execution, delay).ConfigureAwait(false);
        if (completed == execution || execution.IsCompleted)
        {
            delayCancellation.Cancel();
            return await execution.ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        linked.Cancel();

        var grace = Task.Delay(TimeoutGraceMs, CancellationToken.None);
        var finished = await Task.WhenAny(execution, grace).ConfigureAwait(false);
        var stillRunning = finished != execution;

        SmartConLogger.Info(
            "Agent tool timed out: tool=" + tool.Definition.Name +
            ", timeoutMs=" + timeoutMs + ", stillRunning=" + stillRunning);

        if (stillRunning)
        {
            ObserveFaultLater(execution);
            SmartConLogger.Warn(
                "Agent tool '" + tool.Definition.Name + "' is still running after " +
                timeoutMs + " ms (grace " + TimeoutGraceMs + " ms); the Revit main " +
                "thread may be busy [Action: check Revit responsiveness; the tool may " +
                "still complete in the background]");
        }
        else
        {
            _ = execution.Exception;
        }

        // TimeoutMs is a property of the tool definition, not an argument: the
        // model cannot raise it, so the old hint sent it into a retry loop
        // (C1-01). A tool that ignored the cancellation is still inside Revit:
        // a second call would stack work on a busy host.
        return ToolResult.Fail(
            "tool_timeout",
            stillRunning
                ? "Tool '" + tool.Definition.Name + "' exceeded " + timeoutMs +
                  " ms and is still running. The Revit host did not answer in " + timeoutMs +
                  " ms; the tool may still be running — do not retry blindly, tell the user. " +
                  "[Action: wait for Revit to answer again (a dialog or a long operation may be " +
                  "blocking it) before calling any tool]"
                : "Tool '" + tool.Definition.Name + "' exceeded " + timeoutMs +
                  " ms and was cancelled. [Action: narrow the request (fewer items, a filter, " +
                  "a page) and call it again]",
            data: JsonSerializer.SerializeToElement(new { timeoutMs, stillRunning }));
    }

    private static void ObserveFaultLater(Task task)
    {
        _ = task.ContinueWith(
            completed => { _ = completed.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously
            | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private sealed class PreparedArguments
    {
        public JsonElement Arguments { get; init; }

        public IReadOnlyList<string> Repairs { get; init; } = Array.Empty<string>();

        public ToolResult? Failure { get; init; }
    }
}
