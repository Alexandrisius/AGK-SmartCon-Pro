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

    public async Task<ToolRoutingDecision> RouteAsync(
        string query,
        IReadOnlyList<AgentToolDefinition> tools,
        int maxTools = 14,
        CancellationToken ct = default)
    {
        var rule = await _fallback
            .RouteAsync(query, tools, maxTools, ct)
            .ConfigureAwait(false);

        if (!_client.IsEnabled || tools.Count == 0)
            return rule;

        try
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

            var answers = await _client.AskAsync(query, questions, ct).ConfigureAwait(false);

            if (!answers.TryGetValue("catalog_match", out var match)
                || match.Noul is null
                || match.Noul.Value < MinCatalogMatch)
            {
                return rule;
            }

            if (!answers.TryGetValue("tool", out var toolAnswer)
                || toolAnswer.Error is not null
                || toolAnswer.Choice is null)
            {
                return rule;
            }

            var chosen = new List<string> { toolAnswer.Choice };
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

            var selected = new List<AgentToolDefinition>(maxTools);
            var names = new HashSet<string>(StringComparer.Ordinal);

            foreach (var tool in tools)
            {
                if (!RuleToolRouter.CoreToolNames.Contains(tool.Name) || !names.Add(tool.Name))
                    continue;

                selected.Add(tool);
                if (selected.Count >= maxTools)
                    return new ToolRoutingDecision
                    {
                        Tools = selected,
                        TopScore = toolAnswer.Confidence ?? 0
                    };
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

            return new ToolRoutingDecision
            {
                Tools = selected,
                TopScore = toolAnswer.Confidence ?? 0
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

    private static string Shorten(string value, int max) =>
        value.Length <= max ? value : value.Substring(0, max);
}
