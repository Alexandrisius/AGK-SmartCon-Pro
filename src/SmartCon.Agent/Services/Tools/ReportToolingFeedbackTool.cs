using System.Text.Json;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Services.Tools;

/// <summary>
/// Meta-tool that lets the model report friction with its own tooling
/// (mvp-plan Phase 3). The entry goes to agent.db and to smartcon.log; the
/// result tells the model not to spam — one report per distinct issue.
/// </summary>
public sealed class ReportToolingFeedbackTool : IAgentTool
{
    private readonly IAgentFeedbackStore _store;
    private readonly IAgentRuntimeInfo _runtime;

    public ReportToolingFeedbackTool(IAgentFeedbackStore store, IAgentRuntimeInfo runtime)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "report_tooling_feedback",
        Description =
            "Reports friction with SmartCon Agent itself (not with the Revit model). Call it " +
            "once per distinct problem when a tool was inconvenient, returned too little or too " +
            "much data, needed a repeated call, or a description/schema was unclear. area: " +
            "tools | prompt | ui. rating 1 (bad) … 5 (good). Be concrete: what you tried, what " +
            "you expected, what would help.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""area"":{""type"":""string"",""enum"":[""tools"",""prompt"",""ui""]},""toolName"":{""type"":""string""},""rating"":{""type"":""integer"",""minimum"":1,""maximum"":5},""issue"":{""type"":""string"",""minLength"":1},""suggestion"":{""type"":""string""},""example"":{""type"":""string""}},""required"":[""area"",""rating"",""issue""],""additionalProperties"":false}"
    };

    public Task<ToolResult> ExecuteAsync(
        AgentToolContext context,
        JsonElement args,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            var area = NormalizeArea(GetString(args, "area"));
            var issue = GetString(args, "issue");
            if (string.IsNullOrWhiteSpace(issue))
            {
                return Task.FromResult(ToolResult.Fail(
                    "invalid_arguments",
                    "issue is required: describe what was inconvenient or missing."));
            }

            var rating = GetInt(args, "rating", 3);
            if (rating is < 1 or > 5)
                rating = Math.Max(1, Math.Min(5, rating));

            var toolName = GetString(args, "toolName");
            var suggestion = GetString(args, "suggestion");
            var example = GetString(args, "example");
            var contextJson = example is null ? null : JsonSerializer.Serialize(new { example });

            var entry = new AgentFeedbackEntry
            {
                SessionId = context.ConversationId == Guid.Empty
                    ? null
                    : context.ConversationId.ToString("D"),
                DocumentId = string.IsNullOrEmpty(context.DocumentId) ? null : context.DocumentId,
                Model = _runtime.Model,
                Provider = _runtime.Provider,
                ToolName = toolName,
                Area = area,
                Rating = rating,
                Issue = issue!.Trim(),
                Suggestion = suggestion?.Trim(),
                ContextJson = contextJson
            };

            return RecordAsync(entry, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                "Agent feedback was not recorded: " + ex.GetType().Name + ": " + ex.Message +
                " [Action: check write access to agent.db; the conversation can continue]");
            return Task.FromResult(ToolResult.Fail(
                "feedback_not_recorded",
                "The feedback could not be stored, but the session can continue."));
        }
    }

    private async Task<ToolResult> RecordAsync(AgentFeedbackEntry entry, CancellationToken ct)
    {
        await _store.RecordAsync(entry, ct).ConfigureAwait(false);

        SmartConLogger.Info(
            "Agent tooling feedback: area=" + entry.Area +
            " tool=" + (entry.ToolName ?? "-") +
            " rating=" + entry.Rating +
            " issue=" + entry.Issue +
            (string.IsNullOrEmpty(entry.Suggestion) ? string.Empty : " suggestion=" + entry.Suggestion));

        var data = JsonSerializer.SerializeToElement(new
        {
            recorded = true,
            feedbackId = entry.Id.ToString("D"),
            area = entry.Area,
            rating = entry.Rating
        });

        return ToolResult.Ok(
            data,
            "Feedback recorded (id " + entry.Id.ToString("N").Substring(0, 8) + "). " +
            "Report each distinct problem only once.",
            affectedCount: 1);
    }

    private static string NormalizeArea(string? area)
    {
        if (string.IsNullOrWhiteSpace(area))
            return "tools";

        var value = area!.Trim().ToLowerInvariant();
        return value is "tools" or "prompt" or "ui" ? value : "tools";
    }

    private static string? GetString(JsonElement args, string property)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static int GetInt(JsonElement args, string property, int fallback)
    {
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed))
        {
            return parsed;
        }

        return fallback;
    }
}
