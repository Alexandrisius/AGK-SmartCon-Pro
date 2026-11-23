using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;

namespace SmartCon.Agent.Services.Decisions;

/// <summary>
/// Optional decision-model tool router: asks a Choice question over the tool
/// catalog and keeps the rule-based BM25 shortlist as the fail-closed fallback.
/// Only tool names and descriptions are sent (no model data, no element names).
/// </summary>
public sealed class DecisionsToolRouter : IToolRouter
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
            {
                // "Nothing in the catalog fits" is an answer of its own: the rule
                // shortlist returned with it is no match, only the nearest cards.
                return AnswersNoCatalogMatch(answers)
                    ? new ToolRoutingDecision
                    {
                        Tools = rule.Tools,
                        Fallback = rule.Fallback,
                        TopScore = rule.TopScore,
                        NoCatalogMatch = true
                    }
                    : rule;
            }

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
                    return Decision(selected, confidence);
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

            return Decision(selected, confidence);

            // Only picks that made the shortlist are reported as picks: one the
            // catalog does not hold, or one a full shortlist crowded out, is no pick.
            ToolRoutingDecision Decision(List<AgentToolDefinition> shortlist, double topScore) =>
                new()
                {
                    Tools = shortlist,
                    TopScore = topScore,
                    Picked = chosen.Where(names.Contains).ToArray()
                };
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

        if (AnswersNoCatalogMatch(answers))
            return chosen;

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

    /// <summary>
    /// The decision model was asked whether the catalog can fulfil the request
    /// and said no. No answer at all is not a no: the rule shortlist stands.
    /// </summary>
    private static bool AnswersNoCatalogMatch(
        IReadOnlyDictionary<string, AgentDecisionAnswer> answers) =>
        answers.TryGetValue("catalog_match", out var match)
        && match.Noul is not null
        && match.Noul.Value < MinCatalogMatch;

    private static string Shorten(string value, int max) =>
        value.Length <= max ? value : value.Substring(0, max);
}