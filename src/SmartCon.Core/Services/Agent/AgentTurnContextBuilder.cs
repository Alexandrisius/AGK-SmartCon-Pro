using System.Text;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Agent;

/// <summary>
/// Renders <see cref="WorkingState"/> as the bounded &lt;turn_context&gt; block that
/// is prepended to the outgoing user message. Pure: no Revit API, no WPF.
/// </summary>
public static class AgentTurnContextBuilder
{
    public const int MaxLength = 2000;
    public const int MaxConstraints = 10;
    public const int MaxSelectionIds = 10;
    public const int MaxChangeSets = 10;
    public const int MaxTouchedElements = 20;
    public const int MaxOpenQuestions = 10;
    public const int MaxPlanStepsPerSection = 8;
    public const int MaxPlanStepLength = 120;

    private const string Header = "<turn_context>";
    private const string Footer = "</turn_context>";

    public static string Build(WorkingState state, AgentProjectContext context)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (context is null) throw new ArgumentNullException(nameof(context));

        var lines = BuildLines(state, context);
        return Render(lines);
    }

    private static List<string> BuildLines(WorkingState state, AgentProjectContext context)
    {
        var lines = new List<string>(16);

        if (!string.IsNullOrWhiteSpace(state.Goal))
            lines.Add("goal: " + Value(state.Goal));

        var constraints = Take(state.Constraints, MaxConstraints);
        if (constraints.Count > 0)
        {
            lines.Add("constraints:");
            foreach (var constraint in constraints)
                lines.Add("- " + Value(constraint));
        }

        var plan = BuildPlanLine(state);
        if (plan is not null)
            lines.Add(plan);

        var view = BuildCurrentViewLine(state, context);
        if (view is not null)
            lines.Add(view);

        if (state.SelectedUniqueIds.Count > 0)
        {
            var ids = Take(state.SelectedUniqueIds, MaxSelectionIds);
            lines.Add(
                "selection: " + state.SelectedUniqueIds.Count + " selected (" +
                string.Join(", ", ids.Select(Value)) + ")");
        }

        var changeSets = Take(state.ChangeSetIds, MaxChangeSets);
        if (changeSets.Count > 0)
            lines.Add("change_sets: " + string.Join(", ", changeSets.Select(Value)));

        var touched = Take(state.TouchedElementIds, MaxTouchedElements);
        if (touched.Count > 0)
            lines.Add("touched_elements: " + string.Join(", ", touched.Select(Value)));

        var questions = Take(state.OpenQuestions, MaxOpenQuestions);
        if (questions.Count > 0)
            lines.Add("open_questions: " + string.Join("; ", questions.Select(Value)));

        var document = BuildDocumentLine(context);
        if (document is not null)
            lines.Add(document);

        return lines;
    }

    private static string? BuildPlanLine(WorkingState state)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(state.PlanId))
            parts.Add("id=" + Value(state.PlanId));
        if (state.CompletedSteps.Count > 0)
            parts.Add("completed: " + RenderPlanSteps(state.CompletedSteps));
        if (state.PendingSteps.Count > 0)
            parts.Add("pending: " + RenderPlanSteps(state.PendingSteps));

        return parts.Count == 0 ? null : "plan: " + string.Join("; ", parts);
    }

    /// <summary>
    /// Bounds the plan line: a long-running plan must not consume the whole
    /// turn-context budget (LOW-12).
    /// </summary>
    private static string RenderPlanSteps(IReadOnlyList<string> steps)
    {
        var count = System.Math.Min(steps.Count, MaxPlanStepsPerSection);
        var rendered = new List<string>(count);
        for (var index = 0; index < count; index++)
            rendered.Add(Value(TruncatePlanStep(steps[index])));

        return string.Join(", ", rendered);
    }

    private static string TruncatePlanStep(string? step)
    {
        var text = step ?? string.Empty;
        return text.Length <= MaxPlanStepLength
            ? text
            : text.Substring(0, MaxPlanStepLength - 1) + "…";
    }

    private static string? BuildCurrentViewLine(WorkingState state, AgentProjectContext context)
    {
        var viewId = state.ActiveViewId ?? context.ActiveViewId;
        var viewName = string.IsNullOrWhiteSpace(state.ActiveViewName)
            ? context.ActiveViewName
            : state.ActiveViewName;

        if (!string.IsNullOrWhiteSpace(viewName))
        {
            return viewId.HasValue
                ? "current_view: " + Value(viewName) + " (id " + viewId.Value + ")"
                : "current_view: " + Value(viewName);
        }

        return viewId.HasValue ? "current_view: (id " + viewId.Value + ")" : null;
    }

    private static string? BuildDocumentLine(AgentProjectContext context)
    {
        if (!context.HasDocument)
            return null;

        var parts = new List<string>(5)
        {
            Value(context.Title),
            "workshared " + (context.IsWorkshared ? "yes" : "no")
        };

        if (!string.IsNullOrWhiteSpace(context.Units))
            parts.Add("units " + Value(context.Units));

        parts.Add(
            "levels " + context.LevelCount +
            "/views " + context.ViewCount +
            "/sheets " + context.SheetCount);

        return "document: " + string.Join("; ", parts);
    }

    private static string Render(IReadOnlyList<string> lines)
    {
        var budget = MaxLength - Header.Length - Footer.Length - 2 - 2;
        var body = new List<string>(lines.Count);
        var used = 0;
        var truncated = false;

        foreach (var line in lines)
        {
            var cost = line.Length + 1;
            if (used + cost > budget)
            {
                truncated = true;
                break;
            }

            body.Add(line);
            used += cost;
        }

        if (truncated)
            body.Add("…");

        var builder = new StringBuilder(MaxLength);
        builder.Append(Header).Append('\n');
        foreach (var line in body)
            builder.Append(line).Append('\n');
        builder.Append(Footer);
        return builder.ToString();
    }

    private static List<string> Take(IReadOnlyList<string> values, int max)
    {
        if (values.Count == 0)
            return new List<string>(0);

        var count = System.Math.Min(values.Count, max);
        var result = new List<string>(count);
        for (var index = 0; index < count; index++)
            result.Add(values[index]);
        return result;
    }

    private static string Value(string? value)
    {
        // Every field is rendered as exactly one line: a multi-line goal or
        // constraint must not break the section structure of the block.
        var sanitized = AgentTextSanitizer.Sanitize(value)
            .Replace("\r\n", " ")
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ');
        return Escape(sanitized);
    }

    private static string Escape(string value)
    {
        if (value.Length == 0)
            return value;

        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }
}
