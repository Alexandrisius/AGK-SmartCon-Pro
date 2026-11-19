using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;

namespace SmartCon.Agent.Services.Decisions;

/// <summary>
/// Optional decision-model tool router: asks a Choice question over the tool
/// catalog and keeps the rule-based BM25 shortlist as the fail-closed fallback.
/// Only tool names and descriptions are sent (no model data, no element names).
/// </summary>
public sealed class DecisionsToolRouter : IToolRouter, IDynamicToolRanker
{
    private const int MaxChoiceOptions = 255;
    private const int MaxChosenTools = 3;
    private const double MinChosenProbability = 0.08;
    private const double MinCatalogMatch = 0.5;

    private readonly IToolRouter _fallback;
    private readonly IAgentDecisionClient _client;

    public DecisionsToolRouter(IToolRouter fallback, IAgentDecisionClient client)
    {
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public bool IsEnabled => _client.IsEnabled;

    public async Task<ToolRoutingDecision> RouteAsync(
        string query,
        IReadOnlyList<AgentToolDefinition> tools,
        int maxTools = 14,
        CancellationToken ct = default)
    {
        var rule = await _fallback
            .RouteAsync(query, tools, maxTools, ct)
            .ConfigureAwait(false);

        if (!IsEnabled || tools.Count == 0)
            return rule;

        try
        {
            var answers = await AskToolQuestionsAsync(query, tools, ct).ConfigureAwait(false);
            var chosen = ChosenToolNames(answers);
            if (chosen.Count == 0)
                return rule;

            answers.TryGetValue("tool", out var toolAnswer);
            var confidence = toolAnswer?.Confidence ?? 0;

            var selected = new List<AgentToolDefinition>(maxTools);
            var names = new HashSet<string>(StringComparer.Ordinal);

            foreach (var tool in tools)
            {
                if (!RuleToolRouter.CoreToolNames.Contains(tool.Name) || !names.Add(tool.Name))
                    continue;

                selected.Add(tool);
                if (selected.Count >= maxTools)
                    return new ToolRoutingDecision { Tools = selected, TopScore = confidence };
            }

            foreach (var name in chosen)
            {
                if (selected.Count >= maxTools)
                    break;

                var tool = tools.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, name, StringComparison.Ordinal));
                if (tool is not null && names.Add(tool.Name))
                    selected.Add(tool);
            }

            foreach (var tool in rule.Tools)
            {
                if (selected.Count >= maxTools)
                    break;

                if (names.Add(tool.Name))
                    selected.Add(tool);
            }

            return new ToolRoutingDecision { Tools = selected, TopScore = confidence };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return rule;
        }
    }

    /// <summary>
    /// Ranks the non-core catalog for the turn-start dynamic slots. The
    /// always-available hot set is the caller's responsibility: this method
    /// never injects core tools. Disabled or failed decision calls fall back to
    /// the deterministic card BM25 ranking.
    /// </summary>
    public async Task<IReadOnlyList<AgentToolDefinition>> RankAsync(
        string query,
        IReadOnlyList<AgentToolDefinition> tools,
        int max,
        CancellationToken ct = default)
    {
        if (max <= 0 || tools.Count == 0)
            return Array.Empty<AgentToolDefinition>();

        var lexical = ToolCardRouter.Rank(query, tools, max);
        if (!IsEnabled)
            return lexical;

        try
        {
            var answers = await AskToolQuestionsAsync(query, tools, ct).ConfigureAwait(false);
            var chosen = ChosenToolNames(answers);
            if (chosen.Count == 0)
                return lexical;

            var byName = new Dictionary<string, AgentToolDefinition>(StringComparer.Ordinal);
            foreach (var tool in tools)
                byName[tool.Name] = tool;

            var selected = new List<AgentToolDefinition>(max);
            var names = new HashSet<string>(StringComparer.Ordinal);

            foreach (var name in chosen)
            {
                if (selected.Count >= max)
                    return selected;

                if (byName.TryGetValue(name, out var tool) && names.Add(tool.Name))
                    selected.Add(tool);
            }

            foreach (var tool in lexical)
            {
                if (selected.Count >= max)
                    break;

                if (names.Add(tool.Name))
                    selected.Add(tool);
            }

            return selected;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return lexical;
        }
    }

    private Task<IReadOnlyDictionary<string, AgentDecisionAnswer>> AskToolQuestionsAsync(
        string query,
        IReadOnlyList<AgentToolDefinition> tools,
        CancellationToken ct)
    {
        var criteria = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var tool in tools.Take(MaxChoiceOptions))
            criteria[tool.Name] = Shorten(tool.Description, 240);

        var questions = new Dictionary<string, AgentDecisionQuestion>(StringComparer.Ordinal)
        {
            ["tool"] = AgentDecisionQuestion.Choice(
                "Which tool is the best match for the user's request?",
                criteria),
            ["catalog_match"] = AgentDecisionQuestion.Noul(
                "Does the catalog contain a tool that can fulfil the user's request?")
        };

        return _client.AskAsync(query, questions, ct);
    }

    private static List<string> ChosenToolNames(
        IReadOnlyDictionary<string, AgentDecisionAnswer> answers)
    {
        var chosen = new List<string>(MaxChosenTools + 1);

        if (!answers.TryGetValue("catalog_match", out var match)
            || match.Noul is null
            || match.Noul.Value < MinCatalogMatch)
        {
            return chosen;
        }

        if (!answers.TryGetValue("tool", out var toolAnswer)
            || toolAnswer.Error is not null
            || toolAnswer.Choice is null)
        {
            return chosen;
        }

        chosen.Add(toolAnswer.Choice);
        if (toolAnswer.Probabilities is not null)
        {
            foreach (var pair in toolAnswer.Probabilities
                         .Where(pair => pair.Value >= MinChosenProbability)
                         .OrderByDescending(pair => pair.Value)
                         .Take(MaxChosenTools))
            {
                if (!chosen.Contains(pair.Key, StringComparer.Ordinal))
                    chosen.Add(pair.Key);
            }
        }

        return chosen;
    }

    private static string Shorten(string value, int max) =>
        value.Length <= max ? value : value.Substring(0, max);
}