using System.Globalization;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Agent;

/// <summary>One candidate string for semantic ranking, identified by a stable id.</summary>
public sealed record SemanticCandidate(string Id, string Text);

/// <summary>One ranked candidate: its id, the merged score and the 1-based rank.</summary>
public sealed record SemanticMatch(string Id, double Score, int Rank)
{
    /// <summary>
    /// True when the score is the fabricated 0.5 of a bare Choice without
    /// probabilities or confidence - a guess, not a scored answer.
    /// </summary>
    public bool BareChoice { get; init; }
}

/// <summary>
/// Outcome of a semantic ranking pass: one top-K row per request, aligned with
/// the request order, or a typed error. A failed decision call keeps the rows
/// of the answered requests and lists the request ids that stayed unanswered,
/// so a partial result is never presented as complete.
/// </summary>
public sealed class SemanticRankOutcome
{
    public required IReadOnlyList<IReadOnlyList<SemanticMatch>> Matches { get; init; } =
        Array.Empty<IReadOnlyList<SemanticMatch>>();

    /// <summary>null, or one of decision_unavailable / decision_call_failed / decision_refused.</summary>
    public string? Error { get; init; }

    public string? ErrorDetail { get; init; }

    public IReadOnlyList<string> IncompleteQueryIds { get; init; } = Array.Empty<string>();

    public int CallsMade { get; init; }

    public bool Ok => Error is null && IncompleteQueryIds.Count == 0;

    /// <summary>The one row of a one-request pass; empty for a many-request pass.</summary>
    public IReadOnlyList<SemanticMatch> First =>
        Matches.Count == 1 ? Matches[0] : Array.Empty<SemanticMatch>();

    public static SemanticRankOutcome Failed(string error, string? detail) => new()
    {
        Matches = Array.Empty<IReadOnlyList<SemanticMatch>>(),
        Error = error,
        ErrorDetail = detail
    };
}

/// <summary>
/// Pure ranking core over <see cref="IAgentDecisionClient"/>: packs one or many
/// match requests into Choice questions, asks them in batches and merges the
/// answers back into per-request top-K lists. A single request against one
/// candidate list fails as a whole when a batch is refused (the
/// <c>SemanticMatchTool</c> semantics); the many-request mode keeps the answered
/// requests and reports the failed ones as incomplete, so a partial result is
/// never presented as complete.
/// </summary>
public sealed class AgentSemanticRanker
{
    /// <summary>Choice questions carry at most 255 options (DecisionsToolRouter cap).</summary>
    public const int MaxChoiceOptions = 255;

    /// <summary>At most 20 Choice questions travel in one decision call.</summary>
    public const int MaxQuestionsPerCall = 20;

    /// <summary>Instructions of a Choice question without request-specific context.</summary>
    public const string MatchInstructions = "Which item is the best match for the request?";

    private const int MaxQueryLength = 500;

    private readonly IAgentDecisionClient _client;

    public AgentSemanticRanker(IAgentDecisionClient decisionClient) =>
        _client = decisionClient ?? throw new ArgumentNullException(nameof(decisionClient));

    public string ProviderName => _client.ProviderName;

    public string Model => _client.Model;

    public bool IsEnabled => _client.IsEnabled;

    /// <summary>
    /// Ranks one request against one candidate list: the list is split into
    /// Choice batches of at most <see cref="MaxChoiceOptions"/> options, the
    /// batches are asked in separate calls and merged by max probability.
    /// </summary>
    public async Task<SemanticRankOutcome> RankAsync(
        string state,
        IReadOnlyList<SemanticCandidate> candidates,
        string query,
        int topK,
        CancellationToken ct)
    {
        if (!_client.IsEnabled)
            return SemanticRankOutcome.Failed(
                "decision_unavailable",
                "The decision layer is disabled or not configured");

        // The caller composes the decision state (query + context); the query is
        // the stateless fallback so the question never travels without context.
        var decisionState = string.IsNullOrWhiteSpace(state) ? query : state;

        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        var bareChoices = new HashSet<string>(StringComparer.Ordinal);
        for (var start = 0; start < candidates.Count; start += MaxChoiceOptions)
        {
            var batch = candidates.Skip(start).Take(MaxChoiceOptions).ToList();
            IReadOnlyDictionary<string, AgentDecisionAnswer> answers;
            try
            {
                answers = await AskAsync(
                    decisionState,
                    new Dictionary<string, AgentDecisionQuestion>(StringComparer.Ordinal)
                    {
                        ["match"] = ChoiceQuestion(CriteriaOf(batch))
                    },
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return CallFailed(ex);
            }

            // A broken client can hand back a null or empty dictionary; that is
            // an unusable channel, not an empty ranking.
            if (answers is null || answers.Count == 0)
            {
                return SemanticRankOutcome.Failed(
                    "decision_unavailable",
                    "The decision client returned no answers");
            }

            if (!answers.TryGetValue("match", out var answer) || !IsUsable(answer))
                return Refused();

            MergeChoice(
                answer, batch.Select(candidate => candidate.Id).ToArray(), scores, bareChoices);
        }

        return new SemanticRankOutcome
        {
            Matches = new[]
            {
                RankTop(candidates, scores, System.Math.Max(1, topK), bareChoices)
            }
        };
    }

    /// <summary>
    /// Ranks MANY requests, each with its own candidate list, packing the Choice
    /// questions into calls of at most <see cref="MaxQuestionsPerCall"/> questions
    /// and at most <see cref="MaxChoiceOptions"/> options per question. Question
    /// ids are unique per call (q0, q1, …); a request with more candidates than
    /// one question allows is split and merged like the single-request path.
    /// </summary>
    public async Task<SemanticRankOutcome> RankQueriesAsync(
        string state,
        IReadOnlyList<(string Id, IReadOnlyList<SemanticCandidate> Candidates, string Query)> queries,
        int topK,
        CancellationToken ct)
    {
        if (queries.Count == 0)
            return new SemanticRankOutcome { Matches = Array.Empty<IReadOnlyList<SemanticMatch>>() };

        if (!_client.IsEnabled)
            return SemanticRankOutcome.Failed(
                "decision_unavailable",
                "The decision layer is disabled or not configured");

        var units = new List<QueryUnit>();
        for (var index = 0; index < queries.Count; index++)
        {
            var candidates = queries[index].Candidates;
            for (var start = 0; start < candidates.Count; start += MaxChoiceOptions)
                units.Add(new QueryUnit(index, candidates.Skip(start).Take(MaxChoiceOptions).ToList()));
        }

        var scores = new Dictionary<string, double>[queries.Count];
        var bare = new HashSet<string>[queries.Count];
        for (var index = 0; index < scores.Length; index++)
        {
            scores[index] = new Dictionary<string, double>(StringComparer.Ordinal);
            bare[index] = new HashSet<string>(StringComparer.Ordinal);
        }

        var incomplete = new SortedSet<int>();
        string? error = null;
        string? errorDetail = null;
        var callsMade = 0;

        for (var callStart = 0; callStart < units.Count; callStart += MaxQuestionsPerCall)
        {
            var callUnits = units.Skip(callStart).Take(MaxQuestionsPerCall).ToList();
            var questions = new Dictionary<string, AgentDecisionQuestion>(StringComparer.Ordinal);
            var byQuestion = new Dictionary<string, QueryUnit>(StringComparer.Ordinal);
            for (var index = 0; index < callUnits.Count; index++)
            {
                var unit = callUnits[index];
                var questionId = "q" + index.ToString(CultureInfo.InvariantCulture);
                questions[questionId] = AgentDecisionQuestion.Choice(
                    QuestionInstructions(queries[unit.QueryIndex].Query),
                    CriteriaOf(unit.Batch));
                byQuestion[questionId] = unit;
            }

            IReadOnlyDictionary<string, AgentDecisionAnswer> answers;
            try
            {
                callsMade++;
                answers = await AskAsync(state, questions, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                error ??= "decision_call_failed";
                errorDetail ??= ex.Message;
                SmartConLogger.Warn(
                    "The decision model call failed: " + ex.Message +
                    " [Action: fall back to reading the source lines yourself or ask the user]");
                foreach (var unit in callUnits)
                    incomplete.Add(unit.QueryIndex);
                continue;
            }

            // A broken client can hand back a null or empty dictionary; the
            // questions of the call count as unanswered, not as empty rankings.
            if (answers is null || answers.Count == 0)
            {
                error ??= "decision_unavailable";
                errorDetail ??= "The decision client returned no answers";
                SmartConLogger.Warn(
                    "The decision client returned no answers for " + questions.Count + " question(s)" +
                    " [Action: fall back to reading the source lines yourself or ask the user]");
                foreach (var unit in callUnits)
                    incomplete.Add(unit.QueryIndex);
                continue;
            }

            foreach (var pair in byQuestion)
            {
                if (!answers.TryGetValue(pair.Key, out var answer) || !IsUsable(answer))
                {
                    incomplete.Add(pair.Value.QueryIndex);
                    continue;
                }

                MergeChoice(
                    answer,
                    pair.Value.Batch.Select(candidate => candidate.Id).ToArray(),
                    scores[pair.Value.QueryIndex],
                    bare[pair.Value.QueryIndex]);
            }
        }

        var take = System.Math.Max(1, topK);
        var matches = new IReadOnlyList<SemanticMatch>[queries.Count];
        for (var index = 0; index < queries.Count; index++)
            matches[index] = RankTop(queries[index].Candidates, scores[index], take, bare[index]);

        return new SemanticRankOutcome
        {
            Matches = matches,
            Error = error,
            ErrorDetail = errorDetail,
            IncompleteQueryIds = incomplete
                .Where(index => index < queries.Count)
                .Select(index => queries[index].Id)
                .ToArray(),
            CallsMade = callsMade
        };
    }

    /// <summary>
    /// The Choice question of a match batch. Shared with
    /// <c>SemanticMatchTool</c>, so both ask with the same wording.
    /// </summary>
    public static AgentDecisionQuestion ChoiceQuestion(
        IReadOnlyDictionary<string, string?> criteria) =>
        AgentDecisionQuestion.Choice(MatchInstructions, criteria);

    /// <summary>
    /// Merges one Choice answer into the running scores: probabilities by id
    /// keep the maximum across batches; a bare choice that was not scored gets
    /// <c>Confidence ?? 0.5</c>. Ids outside the batch are ignored.
    /// </summary>
    public static void MergeChoice(
        AgentDecisionAnswer answer,
        IReadOnlyCollection<string> batchIds,
        Dictionary<string, double> scores) =>
        MergeChoice(answer, batchIds, scores, null);

    /// <summary>
    /// The merge with bare-choice bookkeeping: <paramref name="bareChoices"/>
    /// (when given) collects the ids whose score is the fabricated
    /// <c>Confidence ?? 0.5</c> of a choice without probabilities, so callers
    /// can tell a confident answer from a guess.
    /// </summary>
    internal static void MergeChoice(
        AgentDecisionAnswer answer,
        IReadOnlyCollection<string> batchIds,
        Dictionary<string, double> scores,
        HashSet<string>? bareChoices)
    {
        var known = new HashSet<string>(batchIds, StringComparer.Ordinal);
        if (answer.Probabilities is not null)
        {
            foreach (var pair in answer.Probabilities)
            {
                if (!known.Contains(pair.Key))
                    continue;

                if (!scores.TryGetValue(pair.Key, out var existing) || pair.Value > existing)
                    scores[pair.Key] = pair.Value;
            }
        }

        if (!string.IsNullOrEmpty(answer.Choice)
            && known.Contains(answer.Choice!)
            && !scores.ContainsKey(answer.Choice!))
        {
            scores[answer.Choice!] = answer.Confidence ?? 0.5;
            if (answer.Confidence is null)
                bareChoices?.Add(answer.Choice!);
        }
    }

    private async Task<IReadOnlyDictionary<string, AgentDecisionAnswer>> AskAsync(
        string state,
        IReadOnlyDictionary<string, AgentDecisionQuestion> questions,
        CancellationToken ct)
    {
        var decisionState = string.IsNullOrWhiteSpace(state)
            ? string.Empty
            : state!.Trim();
        return await _client.AskAsync(decisionState, questions, ct).ConfigureAwait(false);
    }

    private static SemanticRankOutcome CallFailed(Exception ex)
    {
        SmartConLogger.Warn(
            "The decision model call failed: " + ex.Message +
            " [Action: fall back to reading the source lines yourself or ask the user]");
        return SemanticRankOutcome.Failed("decision_call_failed", ex.Message);
    }

    private static SemanticRankOutcome Refused()
    {
        SmartConLogger.Warn(
            "The decision model refused or failed the choice question " +
            "[Action: fall back to reading the source lines yourself or ask the user]");
        return SemanticRankOutcome.Failed(
            "decision_refused",
            "The decision model refused the choice question or returned no usable answer");
    }

    private static bool IsUsable(AgentDecisionAnswer answer) =>
        answer.Error is null
        && (answer.Probabilities is not null || answer.Choice is not null);

    private static Dictionary<string, string?> CriteriaOf(IReadOnlyList<SemanticCandidate> batch)
    {
        var criteria = new Dictionary<string, string?>(batch.Count, StringComparer.Ordinal);
        foreach (var candidate in batch)
            criteria[candidate.Id] = candidate.Text;
        return criteria;
    }

    private static string QuestionInstructions(string query)
    {
        var trimmed = query?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return MatchInstructions;

        var text = trimmed!;
        if (text.Length > MaxQueryLength)
            text = text.Substring(0, MaxQueryLength - 3) + "...";
        return MatchInstructions + "\nMatch: " + text;
    }

    private static IReadOnlyList<SemanticMatch> RankTop(
        IReadOnlyList<SemanticCandidate> candidates,
        Dictionary<string, double> scores,
        int topK,
        HashSet<string>? bareChoices)
    {
        // A repeated id keeps only its first position, like the tool-side
        // parsing of semantic_match.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<(string Id, double Score, int Index)>(candidates.Count);
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            if (!seen.Add(candidate.Id) || !scores.TryGetValue(candidate.Id, out var score))
                continue;

            rows.Add((candidate.Id, score, index));
        }

        return rows
            .OrderByDescending(row => row.Score)
            .ThenBy(row => row.Index)
            .Take(topK)
            .Select((row, position) => new SemanticMatch(
                row.Id,
                System.Math.Round(row.Score, 4),
                position + 1)
            {
                BareChoice = bareChoices is not null && bareChoices.Contains(row.Id)
            })
            .ToArray();
    }

    private sealed record QueryUnit(int QueryIndex, IReadOnlyList<SemanticCandidate> Batch);
}
