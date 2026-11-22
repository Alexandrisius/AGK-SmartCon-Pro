using System.Globalization;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Agent;

/// <summary>One candidate string for semantic ranking, identified by a stable id.</summary>
public sealed record SemanticCandidate(string Id, string Text);

/// <summary>One ranked candidate: its id, the merged score and the 1-based rank.</summary>
public sealed record SemanticMatch(string Id, double Score, int Rank);

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

    /// <summary>The single row of a one-request pass; empty for a many-request pass.</summary>
    public IReadOnlyList<SemanticMatch> Single =>
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

            if (!answers.TryGetValue("match", out var answer) || !IsUsable(answer))
                return Refused();

            MergeChoice(answer, batch.Select(candidate => candidate.Id).ToArray(), scores);
        }

        return new SemanticRankOutcome
        {
            Matches = new[] { RankTop(candidates, scores, System.Math.Max(1, topK)) }
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
        for (var index = 0; index < scores.Length; index++)
            scores[index] = new Dictionary<string, double>(StringComparer.Ordinal);

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
                    scores[pair.Value.QueryIndex]);
            }
        }

        var take = System.Math.Max(1, topK);
        var matches = new IReadOnlyList<SemanticMatch>[queries.Count];
        for (var index = 0; index < queries.Count; index++)
            matches[index] = RankTop(queries[index].Candidates, scores[index], take);

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
        Dictionary<string, double> scores)
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
        int topK)
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
                position + 1))
            .ToArray();
    }

    private sealed record QueryUnit(int QueryIndex, IReadOnlyList<SemanticCandidate> Batch);
}

/// <summary>
/// The deterministic half of semantic matching: tokenization and the lexical
/// prefilter that narrows a big candidate list before any model call.
/// </summary>
internal static class AgentSemanticLexicon
{
    private const int MinPrefixLength = 5;

    /// <summary>
    /// Scores every leaf by the tokens it shares with the family text. Two
    /// tokens match when equal, or one is a prefix of the other while the
    /// shorter is at least 5 chars. Family-name tokens weigh 2, the rest 1. The
    /// best <paramref name="take"/> leaves with a score above zero survive,
    /// score first then code order.
    /// </summary>
    internal static IReadOnlyList<AgentClassificationEntry> LexicalTop(
        IReadOnlyList<string> nameTokens,
        IReadOnlyList<string> otherTokens,
        IReadOnlyList<AgentClassificationEntry> leaves,
        Dictionary<string, HashSet<string>> leafTokens,
        int take)
    {
        var weighted = nameTokens
            .Select(token => (Token: token, Weight: 2))
            .Concat(otherTokens
                .Where(token => !nameTokens.Contains(token))
                .Select(token => (Token: token, Weight: 1)))
            .ToList();
        if (weighted.Count == 0)
            return Array.Empty<AgentClassificationEntry>();

        var scored = new List<(AgentClassificationEntry Leaf, int Score)>();
        foreach (var leaf in leaves)
        {
            if (!leafTokens.TryGetValue(leaf.Code, out var tokens))
                continue;

            var score = weighted.Sum(pair =>
                tokens.Any(leafToken => TokensMatch(pair.Token, leafToken)) ? pair.Weight : 0);
            if (score > 0)
                scored.Add((leaf, score));
        }

        return scored
            .OrderByDescending(pair => pair.Score)
            .ThenBy(pair => pair.Leaf.Code, AgentClassificationTable.CodeComparer)
            .Take(take)
            .Select(pair => pair.Leaf)
            .ToArray();
    }

    internal static bool TokensMatch(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
            return true;

        if (left.Length < MinPrefixLength || right.Length < MinPrefixLength)
            return false;

        return left.Length < right.Length
            ? right.StartsWith(left, StringComparison.Ordinal)
            : left.StartsWith(right, StringComparison.Ordinal);
    }

    /// <summary>Lower-case tokens, ё folded to е, split on every non-letter/digit.</summary>
    internal static string[] Tokenize(string text)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<string>();

        var tokens = new List<string>();
        var builder = new System.Text.StringBuilder();
        foreach (var character in text!)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                continue;
            }

            Flush(builder, tokens);
        }

        Flush(builder, tokens);
        return tokens.ToArray();
    }

    private static void Flush(System.Text.StringBuilder builder, List<string> tokens)
    {
        if (builder.Length == 0)
            return;

        var token = builder.ToString().ToLowerInvariant().Replace('ё', 'е');
        if (!tokens.Contains(token))
            tokens.Add(token);
        builder.Clear();
    }
}

/// <summary>
/// Text preparation of classify_types: groups the selected type snapshots into
/// families and composes the matching texts - the family text (stage 1) and the
/// per-type queries (stage 2, the type name plus the values that differ between
/// the family's types).
/// </summary>
internal static class AgentClassificationTexts
{
    private const int MaxFamilyTextLength = 600;

    internal static IReadOnlyList<ClassifyFamilyGroup> GroupTypes(
        IReadOnlyList<ClassifyTypeSnapshot> types)
    {
        var groups = new List<ClassifyFamilyGroup>();
        var byKey = new Dictionary<string, (ClassifyFamilyGroup Group, List<string> Values, HashSet<string> Common)>(
            StringComparer.Ordinal);
        foreach (var type in types)
        {
            var key = type.FamilyId is { } familyId
                ? "f" + familyId.ToString(CultureInfo.InvariantCulture)
                : "sys:" + type.Category + "|" + type.FamilyName;
            if (!byKey.TryGetValue(key, out var entry))
            {
                entry = (
                    new ClassifyFamilyGroup
                    {
                        Key = key,
                        Category = type.Category,
                        FamilyName = type.FamilyName,
                        FamilyId = type.FamilyId
                    },
                    new List<string>(),
                    new HashSet<string>(type.Values, StringComparer.OrdinalIgnoreCase));
                byKey[key] = entry;
                groups.Add(entry.Group);
            }
            else
            {
                entry.Common.IntersectWith(new HashSet<string>(
                    type.Values, StringComparer.OrdinalIgnoreCase));
            }

            entry.Group.Types = entry.Group.Types.Append(type).ToArray();
            foreach (var value in type.Values)
            {
                if (!entry.Values.Contains(value, StringComparer.OrdinalIgnoreCase))
                    entry.Values.Add(value);
            }
        }

        foreach (var entry in byKey.Values)
        {
            var group = entry.Group;
            var familyParts = new List<string> { group.Category, group.FamilyName };
            familyParts.AddRange(entry.Values);
            var familyText = JoinParts(familyParts);
            group.FamilyText = familyText.Length > MaxFamilyTextLength
                ? familyText.Substring(0, MaxFamilyTextLength)
                : familyText;
            group.FamilyNameTokens = AgentSemanticLexicon.Tokenize(group.FamilyName);
            group.TypeQueries = group.Types.Select(type =>
                {
                    var parts = new List<string>();
                    if (group.Types.Count == 1)
                    {
                        parts.Add(type.Category);
                        parts.Add(type.FamilyName);
                        parts.Add(type.TypeName);
                        parts.AddRange(type.Values);
                    }
                    else
                    {
                        parts.Add(type.FamilyName);
                        parts.Add(type.TypeName);
                        parts.AddRange(type.Values.Where(value => !entry.Common.Contains(value)));
                    }

                    return JoinParts(parts);
                })
                .ToArray();
        }

        return groups;
    }

    private static string JoinParts(IReadOnlyList<string> parts)
    {
        var flat = new List<string>();
        foreach (var part in parts)
        {
            if (!string.IsNullOrWhiteSpace(part) && !flat.Contains(part))
                flat.Add(part);
        }

        return string.Join("; ", flat);
    }
}
