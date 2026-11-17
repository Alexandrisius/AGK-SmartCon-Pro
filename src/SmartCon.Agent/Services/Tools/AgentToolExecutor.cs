using System.Text.Json;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Services.Tools;

public sealed class AgentToolExecutor : IToolExecutor
{
    private readonly IToolRegistry _registry;
    private readonly IAgentAwaitableEvent _revit;
    private readonly AgentSessionContext _sessionContext;
    private readonly IAgentConfirmationService _confirmation;
    private readonly IAgentAuditLog? _auditLog;

    public AgentToolExecutor(
        IToolRegistry registry,
        IAgentAwaitableEvent revit,
        AgentSessionContext sessionContext,
        IAgentConfirmationService confirmation,
        IAgentAuditLog? auditLog = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _revit = revit ?? throw new ArgumentNullException(nameof(revit));
        _sessionContext = sessionContext ?? throw new ArgumentNullException(nameof(sessionContext));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        _auditLog = auditLog;
    }

    public async Task<ToolResult> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_registry.TryGet(name, out var tool) || tool is null)
        {
            return ToolResult.Fail(
                "unknown_tool",
                "Unknown tool '" + name + "'. Use only the tools listed in this request.");
        }

        var approved = false;
        if (RequiresConfirmation(tool.Definition, args))
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

            await WriteAuditAsync(tool.Definition.Name, args, "confirmed", ct).ConfigureAwait(false);
        }

        var context = new AgentToolContext(
            _revit,
            _sessionContext.DocumentId,
            _sessionContext.ConversationId)
        {
            WriteApproved = approved
        };

        return await tool.ExecuteAsync(context, args, ct).ConfigureAwait(false);
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

        var request = new AgentConfirmationRequest
        {
            ToolName = definition.Name,
            Operation = definition.Name,
            Summary = definition.Description,
            Digest = digest,
            ArgumentsJson = rawArgs,
            AffectedCount = CountItems(args),
            Reversible = true,
            Risks = definition.Risk == AgentToolRisk.Destructive
                ? new[] { "destructive operation" }
                : Array.Empty<string>()
        };

        return await _confirmation.RequestAsync(request, ct).ConfigureAwait(false);
    }

    private static int CountItems(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
            return 0;

        if (args.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            return items.GetArrayLength();

        if (args.TryGetProperty("targetViewIds", out var targets)
            && targets.ValueKind == JsonValueKind.Array)
        {
            return targets.GetArrayLength();
        }

        if (args.TryGetProperty("mapping", out var mapping) && mapping.ValueKind == JsonValueKind.Array)
            return mapping.GetArrayLength();

        return 0;
    }

    private async Task WriteAuditAsync(
        string toolName,
        JsonElement args,
        string decision,
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
                Tool = toolName + "#confirm",
                ArgsHash = AgentHashing.Sha256Hex(rawArgs),
                Result = decision
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
