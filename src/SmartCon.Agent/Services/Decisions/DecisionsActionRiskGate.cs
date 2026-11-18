using System.Text.Json;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Agent.Services.Decisions;

/// <summary>
/// Optional Noul risk gate on top of the bypass mode: "is it safe to apply this
/// change without asking?". Only a high-confidence yes allows auto-apply; any
/// refusal, missing answer, low probability or transport error is fail-closed
/// (Confirm). The final decision still belongs to the executor matrix.
/// </summary>
public sealed class DecisionsActionRiskGate : IActionRiskGate
{
    public const double AutoThreshold = 0.85;

    private readonly IAgentDecisionClient _client;

    public DecisionsActionRiskGate(IAgentDecisionClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<AgentActionRiskAssessment> AssessAsync(
        AgentActionRiskRequest request,
        CancellationToken ct)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        if (!_client.IsEnabled)
            return new AgentActionRiskAssessment
            {
                Decision = AgentRiskDecision.Confirm,
                Source = "disabled"
            };

        var state = JsonSerializer.Serialize(new
        {
            tool = request.ToolName,
            risk = request.Risk.ToString(),
            affectedCount = request.AffectedCount,
            reversible = request.Reversible,
            arguments = request.ArgumentSummary
        });

        var questions = new Dictionary<string, AgentDecisionQuestion>(StringComparer.Ordinal)
        {
            ["auto_safe"] = AgentDecisionQuestion.Noul(
                "Is it safe to apply this Revit change automatically without asking the user?",
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["true"] =
                        "The change only renames or adjusts view settings or parameters, is " +
                        "reversible with undo, and affects at most a few dozen items.",
                    ["false"] =
                        "The change is destructive, affects hundreds of items, changes model " +
                        "structure or geometry, or its scope is unclear."
                })
        };

        try
        {
            var answers = await _client.AskAsync(state, questions, ct).ConfigureAwait(false);
            if (!answers.TryGetValue("auto_safe", out var answer)
                || answer.Error is not null
                || answer.Noul is null)
            {
                return new AgentActionRiskAssessment
                {
                    Decision = AgentRiskDecision.Confirm,
                    Source = "unavailable"
                };
            }

            var probability = answer.Noul.Value;
            var auto = probability >= AutoThreshold;
            return new AgentActionRiskAssessment
            {
                Decision = auto ? AgentRiskDecision.Auto : AgentRiskDecision.Confirm,
                Confidence = probability,
                Source = "decision-model",
                Rationale = "p(auto-safe)=" + probability.ToString(
                    "F3", System.Globalization.CultureInfo.InvariantCulture)
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new AgentActionRiskAssessment
            {
                Decision = AgentRiskDecision.Confirm,
                Source = "unavailable"
            };
        }
    }
}
