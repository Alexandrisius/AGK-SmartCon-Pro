using System.Text.Json;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Services.Tools;

public sealed partial class AgentToolExecutor : IToolExecutor
{
    /// <summary>Bypass never auto-applies more items than this in one call.</summary>
    public const int MaxAutoApproveAffected = 200;

    private readonly IToolRegistry _registry;
    private readonly IAgentAwaitableEvent _revit;
    private readonly AgentSessionContext _sessionContext;
    private readonly IAgentConfirmationService _confirmation;
    private readonly IAgentAuditLog? _auditLog;
    private readonly IActionRiskGate? _riskGate;

    public AgentToolExecutor(
        IToolRegistry registry,
        IAgentAwaitableEvent revit,
        AgentSessionContext sessionContext,
        IAgentConfirmationService confirmation,
        IAgentAuditLog? auditLog = null,
        IActionRiskGate? riskGate = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _revit = revit ?? throw new ArgumentNullException(nameof(revit));
        _sessionContext = sessionContext ?? throw new ArgumentNullException(nameof(sessionContext));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        _auditLog = auditLog;
        _riskGate = riskGate;
    }

    public async Task<ToolResult> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_registry.TryGet(name, out var tool) || tool is null)
        {
            return BuildUnknownToolResult(name);
        }

        // The tools the model actually works with stay at the front of the
        // request-cap recency order. Always-available tools never compete for a
        // slot, so touching them would only pollute the list.
        if (!AgentToolPacks.IsAlwaysAvailable(name))
            _sessionContext.TouchTool(name);

        if (_sessionContext.ApprovalMode == AgentApprovalMode.ReadOnly
            && tool.Definition.Risk != AgentToolRisk.Read)
        {
            return ToolResult.Fail(
                "read_only_mode",
                "The session is read-only, so '" + name + "' is blocked. Read the model and " +
                "explain what would change; switch the approval mode to apply it.");
        }

        var preparation = PrepareArguments(tool.Definition, args);
        if (preparation.Failure is not null)
            return preparation.Failure;

        args = preparation.Arguments;

        var approved = false;
        if (RequiresConfirmation(tool.Definition, args))
        {
            var autoReason = await TryAutoApproveAsync(tool.Definition, args, ct)
                .ConfigureAwait(false);
            if (autoReason is not null)
            {
                approved = true;
                await WriteAuditAsync(tool.Definition.Name, args, "auto_approved", autoReason, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                var approval = await RequestConfirmationAsync(tool.Definition, args, ct)
                    .ConfigureAwait(false);

                if (approval != AgentConfirmationResult.Approved)
                    return ToolResult.Fail(
                        approval == AgentConfirmationResult.Rejected
                            ? "confirmation_rejected"
                            : "confirmation_" + approval.ToString().ToLowerInvariant(),
                        approval switch
                        {
                            AgentConfirmationResult.Rejected =>
                                "The user rejected this change. Do not retry it; propose an " +
                                "alternative or ask what should be different.",
                            AgentConfirmationResult.TimedOut =>
                                "The confirmation request timed out. Ask the user whether to " +
                                "apply the change again.",
                            _ =>
                                "The confirmation was cancelled. Ask the user before retrying."
                        });

                approved = true;

                await WriteAuditAsync(tool.Definition.Name, args, "confirmed", "user", ct)
                    .ConfigureAwait(false);
            }
        }

        var context = new AgentToolContext(
            _revit,
            _sessionContext.DocumentId,
            _sessionContext.ConversationId)
        {
            WriteApproved = approved,
            ActiveToolNames = _sessionContext.ActiveToolNames
        };

        var result = await ExecuteWithTimeoutAsync(tool, context, args, ct)
            .ConfigureAwait(false);
        return result.WithRepairs(preparation.Repairs);
    }

    /// <summary>
    /// Auto-approval is allowed only in <see cref="AgentApprovalMode.AutoApproveSession"/>
    /// and only after the hard guards: destructive risk, more than
    /// <see cref="MaxAutoApproveAffected"/> items, and the optional risk gate
    /// (fail-closed). Returns the audit reason or null when a dialog is required.
    /// </summary>
    private async Task<string?> TryAutoApproveAsync(
        AgentToolDefinition definition,
        JsonElement args,
        CancellationToken ct)
    {
        if (_sessionContext.ApprovalMode != AgentApprovalMode.AutoApproveSession)
            return null;

        if (definition.Risk == AgentToolRisk.Destructive)
            return null;

        // C2: closing a change set is never auto-approved — it permanently drops
        // the undo path for that set.
        if (string.Equals(definition.Name, "discard_change_set", StringComparison.Ordinal))
            return null;

        var risk = definition.Risk;
        var affected = CountAffected(definition, args);

        if (string.Equals(definition.Name, "execute_plan", StringComparison.Ordinal)
            && !TryGetPlanRisk(args, out risk, out _))
        {
            return null;
        }

        if (affected > MaxAutoApproveAffected)
            return null;

        if (_riskGate is null)
            return "bypass";

        try
        {
            var assessment = await _riskGate
                .AssessAsync(
                    new AgentActionRiskRequest
                    {
                        ToolName = definition.Name,
                        Risk = risk,
                        AffectedCount = affected,
                        Reversible = true,
                        ArgumentSummary = DescribeArguments(args)
                    },
                    ct)
                .ConfigureAwait(false);

            return assessment.Decision == AgentRiskDecision.Auto ? "risk_gate" : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                "Agent risk gate failed for '" + definition.Name + "': " + ex.Message +
                " [Action: the action will require explicit confirmation (fail-closed)]");
            return null;
        }
    }

    private static string DescribeArguments(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
            return "{}";

        var keys = new List<string>();
        foreach (var property in args.EnumerateObject())
        {
            var shape = property.Value.ValueKind switch
            {
                JsonValueKind.Array => "array(" + property.Value.GetArrayLength() + ")",
                JsonValueKind.Object => "object",
                JsonValueKind.String => "string",
                JsonValueKind.Number => "number",
                JsonValueKind.True or JsonValueKind.False => "boolean",
                _ => "null"
            };
            keys.Add(property.Name + ":" + shape);
        }

        return "{" + string.Join(",", keys) + "}";
    }

    private static bool RequiresConfirmation(AgentToolDefinition definition, JsonElement args)
    {
        if (definition.Risk == AgentToolRisk.Read)
            return false;

        if (!definition.SupportsDryRun)
            return true;

        return !GetDryRun(args);
    }

    private static bool GetDryRun(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("dryRun", out var value))
        {
            if (value.ValueKind == JsonValueKind.False)
                return false;
            if (value.ValueKind == JsonValueKind.True)
                return true;
        }

        return true;
    }

    private async Task<AgentConfirmationResult> RequestConfirmationAsync(
        AgentToolDefinition definition,
        JsonElement args,
        CancellationToken ct)
    {
        var rawArgs = args.ValueKind == JsonValueKind.Undefined ? "{}" : args.GetRawText();
        var documentId = _sessionContext.DocumentId ?? string.Empty;
        var digest = AgentActionDigest.Compute(definition.Name, rawArgs, documentId);

        var risks = new List<string>();
        if (definition.Risk == AgentToolRisk.Destructive)
            risks.Add("destructive operation");

        if (string.Equals(definition.Name, "execute_plan", StringComparison.Ordinal)
            && !TryGetPlanRisk(args, out _, out _))
        {
            risks.Add("plan contains a destructive, nested or unknown step");
        }

        if (string.Equals(definition.Name, "discard_change_set", StringComparison.Ordinal))
            risks.Add("closing the change set removes the undo path");

        var request = new AgentConfirmationRequest
        {
            ToolName = definition.Name,
            Operation = definition.Name,
            Summary = definition.Description,
            Digest = digest,
            ArgumentsJson = rawArgs,
            AffectedCount = CountAffected(definition, args),
            Reversible = true,
            Risks = risks
        };

        return await _confirmation.RequestAsync(request, ct).ConfigureAwait(false);
    }

    private async Task WriteAuditAsync(
        string toolName,
        JsonElement args,
        string decision,
        string reason,
        CancellationToken ct)
    {
        if (_auditLog is null)
            return;

        try
        {
            var rawArgs = args.ValueKind == JsonValueKind.Undefined ? "{}" : args.GetRawText();
            await _auditLog.WriteAsync(new AgentAuditEntry
            {
                Actor = Environment.UserName,
                DocumentPath = _sessionContext.DocumentId,
                Tool = toolName + "#" + decision,
                ArgsHash = AgentHashing.Sha256Hex(rawArgs),
                Result = decision + ":" + reason
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                "Agent confirmation audit write failed: " + ex.Message +
                " [Action: check agent.db write access; the confirmed operation is not affected]");
        }
    }
}
