using System.Text.Json;
using SmartCon.Agent.Services.Decisions;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Services.Tools.Semantic;

/// <summary>
/// Semantic top-K selection over an array of short strings (real-cases
/// benchmark v2, case 5 and case 2). The list is sent to the small decision
/// model in Choice batches of at most 255 options — the same channel the tool
/// router uses — and only the ranked top-K comes back, so a 100-200 line
/// classifier or a ФОП name column never enters the chat context. Fail-soft by
/// design: when the decision layer is disabled or fails, the tool answers
/// <c>decision_unavailable</c> with a hint and the agent falls back to reading
/// the lines itself (correct results matter more than context economy).
/// </summary>
public sealed partial class SemanticMatchTool : IAgentTool
{
    public const string ToolName = "semantic_match";

    /// <summary>Choice questions carry at most 255 options (DecisionsToolRouter cap).</summary>
    private const int MaxChoiceOptions = 255;

    private const int MaxItems = 500;
    private const int MaxTextLength = 500;
    private const int DefaultTopK = 3;
    private const double DefaultMinScore = 0.08;

    private const string UnavailableHint =
        "The decision model is not available. Read the candidate lines yourself " +
        "(for a classifier take them from read_classifier) or show the shortlist " +
        "to the user and pick the match manually - the task must succeed without " +
        "the small model";

    private readonly IAgentDecisionClient _decisionClient;
    private readonly AgentDecisionsSettings _settings;

    public SemanticMatchTool(
        IAgentDecisionClient decisionClient,
        AgentDecisionsSettings settings)
    {
        _decisionClient = decisionClient ?? throw new ArgumentNullException(nameof(decisionClient));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Definition = BuildDefinition();
    }

    public AgentToolDefinition Definition { get; }

    public async Task<ToolResult> ExecuteAsync(
        AgentToolContext context,
        JsonElement args,
        CancellationToken ct)
    {
        var request = ParseRequest(args, out var error);
        if (request is null)
            return ToolResult.Fail("invalid_arguments", error!);

        if (!_decisionClient.IsEnabled)
        {
            var detail = _settings.IsAvailable
                ? "The decision client reports the decision layer as disabled"
                : "The decision layer is not configured (no model/key selected)";
            return Unavailable(detail, request);
        }

        return await MatchAsync(request, ct).ConfigureAwait(false);
    }

    private ToolResult Unavailable(string detail, SemanticMatchRequest request)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            decisionUsed = false,
            reason = "decision_unavailable",
            hint = UnavailableHint,
            detail,
            candidatesTotal = request.Items.Count
        });

        return ToolResult.Fail(
            "decision_unavailable",
            detail + ". " + UnavailableHint + ".",
            data: payload);
    }

    private static AgentToolDefinition BuildDefinition()
    {
        const string schema = @"{""type"":""object"",""properties"":{" +
            @"""items"":{""type"":""array"",""minItems"":1,""maxItems"":500," +
            @"""description"":""Candidate strings; use id=code and text=breadcrumb for classifier entries""," +
            @"""items"":{""type"":""object"",""properties"":{" +
            @"""id"":{""type"":""string"",""description"":""Stable identifier returned in matches""}," +
            @"""text"":{""type"":""string"",""description"":""Candidate string, max 500 chars (longer is truncated)""}}," +
            @"""required"":[""id"",""text""],""additionalProperties"":false}}," +
            @"""query"":{""type"":""string"",""description"":""What to match against the items (max 500 chars)""}," +
            @"""topK"":{""type"":""integer"",""minimum"":1,""maximum"":10,""default"":3," +
            @"""description"":""How many best matches to return""}," +
            @"""minScore"":{""type"":""number"",""minimum"":0,""maximum"":1,""default"":0.08," +
            @"""description"":""Discard candidates scored below this probability""}," +
            @"""instructions"":{""type"":""string"",""description"":""Extra selection context, max 500 chars""}}," +
            @"""required"":[""items"",""query""],""additionalProperties"":false}";

        return new AgentToolDefinition
        {
            Name = ToolName,
            Description =
                "Ranks a list of short strings (up to 500) by semantic similarity to a query " +
                "using the small decision model and returns only the top-K matches with " +
                "scores - the whole list never enters the chat context. Input: " +
                "items=[{id,text}] plus query (and optional instructions). Typical lists: " +
                "classifier entries with breadcrumbs from read_classifier, a ФОП/shared " +
                "parameter name column, long name lists. Batches of 255 options are asked " +
                "as Choice questions and merged. Fail-soft: when the decision layer is off " +
                "the tool returns decision_unavailable with a hint - then read the lines " +
                "yourself or ask the user instead of guessing.",
            Risk = AgentToolRisk.Read,
            SupportsDryRun = false,
            Category = "Read",
            Pack = "semantic",
            TimeoutMs = 60000,
            InputSchemaJson = schema
        };
    }
}
