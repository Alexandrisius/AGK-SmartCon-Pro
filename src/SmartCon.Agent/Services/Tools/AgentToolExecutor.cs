using System.Text.Json;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Services.Tools;

public sealed partial class AgentToolExecutor : IToolExecutor
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
        return TrackDocumentState(result.WithRepairs(preparation.Repairs));
    }

    /// <summary>
    /// Remembers the documentVersion marker of every tool result and explains a
    /// *_not_found failure when the model actually changed between two calls:
    /// "the id is stale, re-read the model" instead of a silent not-found.
    /// </summary>
    private ToolResult TrackDocumentState(ToolResult result)
    {
        var previous = _sessionContext.LastDocumentVersion;
        var current = result.DocumentVersion;
        if (!string.IsNullOrWhiteSpace(current))
            _sessionContext.LastDocumentVersion = current;

        if (result.Success
            || string.IsNullOrWhiteSpace(previous)
            || string.IsNullOrWhiteSpace(current)
            || string.Equals(previous, current, StringComparison.Ordinal)
            || result.ErrorCode is null
            || !result.ErrorCode.EndsWith("_not_found", StringComparison.Ordinal)
            || (result.Message?.IndexOf("documentVersion", StringComparison.Ordinal) ?? -1) >= 0)
        {
            return result;
        }

        return result.WithMessageSuffix(
            " The document changed since the previous tool call (documentVersion " +
            previous + " → " + current + "): the id may be stale — re-read the model " +
            "with the matching list_* tool and retry.");
    }

    /// <summary>
    /// Bypass (AutoApproveSession) means the operator took the risk and left the
    /// desk: no dialog and no decision-model risk gate for any tool, including
    /// destructive ones and change-set discards. Every write still records a
    /// change set, so undo_last can revert it. Anything short of bypass keeps the
    /// normal confirmation flow.
    /// </summary>
    private Task<string?> TryAutoApproveAsync(
        AgentToolDefinition definition,
        JsonElement args,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(
            _sessionContext.ApprovalMode == AgentApprovalMode.AutoApproveSession
                ? "bypass"
                : null);
    }

    private static bool RequiresConfirmation(AgentToolDefinition definition, JsonElement args)
    {
        if (definition.Risk == AgentToolRisk.Read)
            return false;

        if (!definition.SupportsDryRun)
            return true;

        if (HasEffectiveWriteIntent(definition, args))
            return true;

        return !GetDryRun(args);
    }

    /// <summary>
    /// Some write tools express the write intent through aliases that do not
    /// look like dryRun=false. start_job is the critical one: mode="apply" or
    /// resumeJobId starts a mass write while dryRun still defaults to true, so
    /// the confirmation gate must read the effective mode, not the flag.
    /// </summary>
    private static bool HasEffectiveWriteIntent(
        AgentToolDefinition definition,
        JsonElement args)
    {
        if (!string.Equals(definition.Name, "start_job", StringComparison.Ordinal))
            return false;

        var mode = GetStringIgnoreCase(args, "mode");
        if (string.Equals(mode, "apply", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "resume", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var resumeJobId = GetStringIgnoreCase(args, "resumeJobId");
        return !string.IsNullOrWhiteSpace(resumeJobId);
    }

    private static string? GetStringIgnoreCase(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object)
            return null;

        if (args.TryGetProperty(name, out var exact) && exact.ValueKind == JsonValueKind.String)
            return exact.GetString();

        foreach (var property in args.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
    }

    private static bool GetDryRun(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object)
        {
            if (args.TryGetProperty("dryRun", out var value))
            {
                if (value.ValueKind == JsonValueKind.False)
                    return false;
                if (value.ValueKind == JsonValueKind.True)
                    return true;
            }

            foreach (var property in args.EnumerateObject())
            {
                if (!string.Equals(property.Name, "dryRun", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (property.Value.ValueKind == JsonValueKind.False)
                    return false;
                if (property.Value.ValueKind == JsonValueKind.True)
                    return true;
            }
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

        // #400: the rule tree is not restorable through the change set, so the
        // overlay must carry the same line the dry-run warnings have.
        var rulesNotUndoable =
            string.Equals(definition.Name, "set_filter_rules", StringComparison.Ordinal);
        if (rulesNotUndoable)
        {
            risks.Add(
                "undo_last не откатит изменение правил фильтра: откат — повторный вызов " +
                "set_filter_rules с previousRules/previousCategories из dry-run");
        }

        var request = new AgentConfirmationRequest
        {
            ToolName = definition.Name,
            Operation = definition.Name,
            Summary = definition.Description,
            Digest = digest,
            ArgumentsJson = rawArgs,
            AffectedCount = CountAffected(definition, args),
            Reversible = !rulesNotUndoable,
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
