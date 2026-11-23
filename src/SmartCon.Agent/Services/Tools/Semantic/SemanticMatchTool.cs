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

        // Batch mode (#470): one call answers MANY queries against the same
        // item list - live case 5 burned 12-70 single-query calls and never
        // reached the apply step.
        if (request.Queries.Count > 0)
        {
            var results = new List<object>(request.Queries.Count);
            for (var index = 0; index < request.Queries.Count; index++)
            {
                var query = request.Queries[index];
                var single = new SemanticMatchRequest
                {
                    Items = request.Items,
                    Query = query.Query,
                    Instructions = query.Instructions ?? request.Instructions,
                    TopK = request.TopK,
                    MinScore = request.MinScore,
                    Warnings = request.Warnings
                };

                var singleResult = await MatchAsync(single, ct).ConfigureAwait(false);
                if (!singleResult.Success)
                    return singleResult;

                results.Add(new
                {
                    id = query.Id,
                    matches = singleResult.Data
                });
            }

            var payload = System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                results,
                queries = request.Queries.Count,
                decisionUsed = true
            });
            // Live case 5 round 2: the model sent 2-3 queries per call and burned
            // its round budget before applying anything. Nudge small batches to
            // combine the rest in one call.
            var batchHint = request.Queries.Count < 5
                ? " Send the REST of your items as further queries in this same call (up to 20 per call) instead of re-calling with 2-3."
                : string.Empty;
            return ToolResult.Ok(
                payload,
                "Matched " + request.Queries.Count + " quer" +
                (request.Queries.Count == 1 ? "y" : "ies") +
                " against " + request.Items.Count + " candidate(s)." + batchHint);
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
            @"""description"":""How many best matches to return; an upper bound only - minScore drops the rest""}," +
            @"""minScore"":{""type"":""number"",""minimum"":0,""maximum"":1,""default"":0.08," +
            @"""description"":""Discard candidates scored below this probability""}," +
            @"""instructions"":{""type"":""string"",""description"":""Extra selection context, max 500 chars""}," +
            @"""classifierPath"":{""type"":""string""," +
            @"""description"":""Compact mode: parse this classifier txt instead of passing items (id=code, text=breadcrumb)""}," +
            @"""codePrefix"":{""type"":""string""," +
            @"""description"":""With classifierPath: keep only rows whose code starts with this prefix, e.g. 30. or 10.20""}," +
            @"""queries"":{""type"":""array"",""minItems"":1,""maxItems"":20," +
            @"""description"":""Batch mode: match MANY queries against the same items in ONE call (id = e.g. family/type key)""," +
            @"""items"":{""type"":""object"",""properties"":{""id"":{""type"":""string""}," +
            @"""query"":{""type"":""string""},""instructions"":{""type"":""string""}}," +
            @"""required"":[""id"",""query""],""additionalProperties"":false}}}," +
            @"""additionalProperties"":false}";

        return new AgentToolDefinition
        {
            Name = ToolName,
            Description =
                "Ranks a list of short strings (up to 500) by semantic similarity to a query " +
                "using the small decision model and returns only the top-K matches with " +
                "scores - the whole list never enters the chat context. Input: " +
                "items=[{id,text}] plus ONE query - or the batch mode queries=[{id,query}] " +
                "(many lookups in ONE call - use it whenever several families/types need codes, #470); or the compact " +
                "mode: classifierPath (+codePrefix filter) reads the txt itself, so the " +
                "argument stays tiny (parallel calls never truncate #470). Typical lists: " +
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
