using System.Globalization;
using System.Text.Json;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Agent;

/// <summary>
/// Maintains <see cref="WorkingState"/> from the user text, the project context
/// and observed tool results. Pure: no Revit API, no WPF, no persistence.
/// </summary>
public static class AgentWorkingStateUpdater
{
    public const int MaxGoalLength = 400;
    public const int MaxConstraintLength = 200;
    public const int MaxConstraints = 10;
    public const int MaxPlanSteps = 30;
    public const int MaxSelectedUniqueIds = 50;
    public const int MaxTouchedElementIds = 100;
    public const int MaxChangeSetIds = 20;
    public const int MaxOpenQuestions = 10;

    private static readonly string[] ConstraintMarkers =
    {
        "не ",
        "только ",
        "без ",
        "кроме ",
        "must not",
        "only ",
        "without ",
        "except"
    };

    public static void UpdateFromUserText(WorkingState state, string userText)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));

        var text = (userText ?? string.Empty).Trim();
        if (text.Length == 0)
            return;

        if (string.IsNullOrWhiteSpace(state.Goal))
            state.Goal = Truncate(text, MaxGoalLength);

        foreach (var candidate in ExtractConstraintCandidates(text))
        {
            if (state.Constraints.Count >= MaxConstraints)
                break;

            var constraint = Truncate(candidate.Trim(), MaxConstraintLength);
            if (constraint.Length == 0)
                continue;

            if (state.Constraints.Any(existing =>
                    string.Equals(existing, constraint, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            state.Constraints.Add(constraint);
        }
    }

    public static void UpdateFromProjectContext(WorkingState state, AgentProjectContext context)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (context is null) throw new ArgumentNullException(nameof(context));

        state.DocumentId = context.DocumentId;
        state.DocumentPath = context.PathName;
        state.RevitVersion = context.RevitVersion;
        state.ActiveViewId = context.ActiveViewId;
        state.ActiveViewName = context.ActiveViewName;
        state.UpdatedAt = DateTime.UtcNow;
    }

    public static void ObserveToolResult(WorkingState state, string toolName, ToolResult result)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (result is null) throw new ArgumentNullException(nameof(result));

        try
        {
            ObserveCore(state, toolName ?? string.Empty, result);
        }
        catch (Exception)
        {
            // Working state is a best-effort side channel: malformed tool data
            // must never fail the turn that produced it.
        }

        state.UpdatedAt = DateTime.UtcNow;
    }

    private static void ObserveCore(WorkingState state, string toolName, ToolResult result)
    {
        var rejected = "user rejected " + toolName;
        if (!result.Success
            && string.Equals(result.ErrorCode, "confirmation_rejected", StringComparison.Ordinal))
        {
            AddQuestion(state, rejected);
        }
        else if (result.Success && toolName.Length > 0)
        {
            state.OpenQuestions.RemoveAll(question =>
                string.Equals(question, rejected, StringComparison.OrdinalIgnoreCase));
        }

        if (result.Data is not { ValueKind: JsonValueKind.Object } data)
            return;

        if (result.Success
            && string.Equals(toolName, "get_selected_elements", StringComparison.Ordinal))
        {
            ObserveSelection(state, data);
        }

        var dryRun = data.TryGetProperty("dryRun", out var dryRunValue)
            && dryRunValue.ValueKind == JsonValueKind.True;

        if (data.TryGetProperty("changeSetId", out var changeSet)
            && changeSet.ValueKind == JsonValueKind.String)
        {
            PrependNew(state.ChangeSetIds, new[] { changeSet.GetString() }, MaxChangeSetIds);
        }

        if (data.TryGetProperty("planDigest", out var digest)
            && digest.ValueKind == JsonValueKind.String)
        {
            var value = digest.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                state.PlanId = value!.Trim();
        }

        if (!dryRun
            && data.TryGetProperty("changed", out var changed)
            && changed.ValueKind == JsonValueKind.Array)
        {
            var ids = new List<string>();
            foreach (var item in changed.EnumerateArray())
            {
                var id = ReadElementId(item);
                if (id is not null)
                    ids.Add(id);
            }

            PrependNew(state.TouchedElementIds, ids, MaxTouchedElementIds);
        }

        if (!dryRun
            && string.Equals(toolName, "execute_plan", StringComparison.Ordinal)
            && data.TryGetProperty("steps", out var steps)
            && steps.ValueKind == JsonValueKind.Array)
        {
            ObservePlanSteps(state, steps);
        }
    }

    private static void ObserveSelection(WorkingState state, JsonElement data)
    {
        var selection = new List<string>();
        if (data.TryGetProperty("elements", out var elements)
            && elements.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in elements.EnumerateArray())
            {
                var id = ReadSelectedElementId(item);
                if (id is null || selection.Contains(id, StringComparer.Ordinal))
                    continue;

                selection.Add(id);
                if (selection.Count >= MaxSelectedUniqueIds)
                    break;
            }
        }

        state.SelectedUniqueIds.Clear();
        state.SelectedUniqueIds.AddRange(selection);
    }

    private static string? ReadSelectedElementId(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;

        if (item.TryGetProperty("uniqueId", out var uniqueId)
            && uniqueId.ValueKind == JsonValueKind.String)
        {
            var value = uniqueId.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                return value!.Trim();
        }

        if (item.TryGetProperty("elementId", out var elementId)
            && elementId.ValueKind == JsonValueKind.Number
            && elementId.TryGetInt64(out var elementIdValue))
        {
            return elementIdValue.ToString(CultureInfo.InvariantCulture);
        }

        if (item.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.Number
            && id.TryGetInt64(out var idValue))
        {
            return idValue.ToString(CultureInfo.InvariantCulture);
        }

        return null;
    }

    private static void ObservePlanSteps(WorkingState state, JsonElement steps)
    {
        foreach (var step in steps.EnumerateArray())
        {
            if (step.ValueKind != JsonValueKind.Object)
                continue;

            var stepTool = ReadString(step, "toolName") ?? "step";
            var changed = ReadInt(step, "changed");
            var failed = ReadInt(step, "failed");

            if (failed > 0)
            {
                AddPlanStep(state.PendingSteps, stepTool + ": failed");
            }
            else if (changed > 0)
            {
                AddPlanStep(state.CompletedSteps, stepTool + ": " + changed + " changed");
            }
        }
    }

    private static void AddPlanStep(List<string> steps, string value)
    {
        if (steps.Any(existing => string.Equals(existing, value, StringComparison.Ordinal)))
            return;

        steps.Add(value);
        if (steps.Count > MaxPlanSteps)
            steps.RemoveRange(0, steps.Count - MaxPlanSteps);
    }

    private static void AddQuestion(WorkingState state, string question)
    {
        if (state.OpenQuestions.Any(existing =>
                string.Equals(existing, question, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        state.OpenQuestions.Add(question);
        if (state.OpenQuestions.Count > MaxOpenQuestions)
            state.OpenQuestions.RemoveRange(0, state.OpenQuestions.Count - MaxOpenQuestions);
    }

    private static void PrependNew(List<string> target, IEnumerable<string?> values, int max)
    {
        var fresh = new List<string>();
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var trimmed = value!.Trim();
            if (target.Contains(trimmed, StringComparer.Ordinal)
                || fresh.Contains(trimmed, StringComparer.Ordinal))
            {
                continue;
            }

            fresh.Add(trimmed);
        }

        if (fresh.Count == 0)
            return;

        target.InsertRange(0, fresh);
        if (target.Count > max)
            target.RemoveRange(max, target.Count - max);
    }

    private static string? ReadElementId(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;

        if (item.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.Number
            && id.TryGetInt64(out var value))
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        if (item.TryGetProperty("elementId", out var elementId)
            && elementId.ValueKind == JsonValueKind.Number
            && elementId.TryGetInt64(out value))
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        return null;
    }

    private static string? ReadString(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text!.Trim();
    }

    private static int ReadInt(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var parsed)
            ? parsed
            : 0;

    private static List<string> ExtractConstraintCandidates(string text)
    {
        var result = new List<string>();
        foreach (var line in SplitLines(text))
        {
            foreach (var sentence in SplitSentences(line))
            {
                var candidate = sentence.Trim();
                if (candidate.Length == 0)
                    continue;

                if (ConstraintMarkers.Any(marker =>
                        candidate.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    result.Add(candidate);
                }
            }
        }

        return result;
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);

    private static IEnumerable<string> SplitSentences(string line) =>
        line.Split(new[] { '.', '!', '?', ';' }, StringSplitOptions.RemoveEmptyEntries);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value.Substring(0, max) + "…";
}
