using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Agent;

/// <summary>
/// The per-type differentiation stage of <see cref="AgentClassificationMatcher"/>:
/// a family that ended with ONE code (the descent result or a single surviving
/// candidate) may still carry types whose own text - the type name plus the
/// values not shared by every type - clearly describes a different row of the
/// table. Each such type is asked one question: the options are the family
/// code, its sibling leaves under the same parent branch, and an honest none.
/// A confident sibling earns its own code; an abstain, a low score or a failed
/// question keeps the family code. The stage runs only for confident families
/// whose types really differ, so its cost stays bounded.
/// </summary>
public sealed partial class AgentClassificationMatcher
{
    /// <summary>Id of the honest-none option of the differentiation question.</summary>
    private const string TypeAbstainId = "__none__";

    /// <summary>
    /// At most this many differentiation questions per classify call (the ranker
    /// packs them into decision calls of at most
    /// <see cref="AgentSemanticRanker.MaxQuestionsPerCall"/> questions).
    /// </summary>
    private const int MaxTypeDifferentiationQuestions = 100;

    private async Task<TypeStageResult> RunTypeDifferentiationAsync(
        IReadOnlyList<ClassifyFamilyGroup> staged,
        AgentClassificationTable table,
        Dictionary<string, IReadOnlyList<SemanticMatch>> codesByFamily,
        Dictionary<string, double> chainScores,
        ClassifyOptions options,
        RunState run,
        CancellationToken ct)
    {
        var queries = new List<(string, IReadOnlyList<SemanticCandidate>, string)>();
        var answers = new Dictionary<long, TypeStageAnswer>();
        var familyCodeByTypeId = new Dictionary<long, SemanticMatch>();
        var siblingCache = new Dictionary<string, IReadOnlyList<SemanticCandidate>>(StringComparer.Ordinal);
        var leftOut = 0;
        foreach (var family in staged)
        {
            if (!codesByFamily.TryGetValue(family.Key, out var familyCodes)
                || familyCodes.Count != 1)
            {
                continue;
            }

            var single = familyCodes[0];

            // Asking about the types of an unconfident family is wasted money:
            // the family itself already lands in uncertain together with its code.
            var familyScore = chainScores.TryGetValue(family.Key, out var chain)
                ? System.Math.Min(single.Score, chain)
                : single.Score;
            if (single.BareChoice || familyScore < options.MinScore || family.Types.Count < 2)
                continue;

            // Types whose names and distinctive values tokenize into the same
            // token set differ by nothing the model could decide on.
            if (!TypeTextsDiffer(family))
                continue;

            if (!siblingCache.TryGetValue(family.Key, out var questionOptions))
            {
                questionOptions = SiblingOptionsOf(table, single.Id);
                siblingCache[family.Key] = questionOptions;
            }

            if (questionOptions.Count == 0)
                continue;

            for (var index = 0; index < family.Types.Count; index++)
            {
                var type = family.Types[index];
                if (queries.Count >= MaxTypeDifferentiationQuestions)
                {
                    leftOut++;
                    continue;
                }

                queries.Add(("type:" + type.ElementId, questionOptions, TypeQueryOf(family, index)));
                familyCodeByTypeId[type.ElementId] = single;
            }
        }

        if (leftOut > 0)
        {
            run.Warnings.Add(
                leftOut + " type(s) of single-code families were not asked for a code of " +
                "their own (the per-type question budget of " + MaxTypeDifferentiationQuestions +
                " was reached); they keep the family code. [Action: re-run classify_types " +
                "with the typeIds of those types to differentiate them]");
        }

        if (queries.Count == 0)
            return new TypeStageResult { Answers = answers };

        var outcome = await _ranker.RankQueriesAsync(
            TypeStageStateOf(options), queries, 3, ct).ConfigureAwait(false);
        run.Observe(outcome);

        // A dead channel is a typed failure (D1); a partially unanswered call is
        // softer - the unanswered types simply keep the family code.
        if (outcome.Error == "decision_unavailable")
            return new TypeStageResult { Answers = answers, HardFailure = outcome };

        var unanswered = new HashSet<string>(outcome.IncompleteQueryIds, StringComparer.Ordinal);
        for (var index = 0; index < queries.Count; index++)
        {
            var idText = queries[index].Item1.Substring("type:".Length);
            if (!long.TryParse(idText, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var id)
                || !familyCodeByTypeId.TryGetValue(id, out var familyCode))
            {
                continue;
            }

            answers[id] = new TypeStageAnswer(
                familyCode,
                unanswered.Contains(queries[index].Item1) ? null : TopOf(outcome.Matches[index]),
                outcome.Matches[index]);
        }

        return new TypeStageResult { Answers = answers };
    }

    /// <summary>
    /// True when at least two types of the family tokenize differently - the
    /// family-name tokens are excluded because every type carries them.
    /// </summary>
    private static bool TypeTextsDiffer(ClassifyFamilyGroup family)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < family.Types.Count; index++)
        {
            var tokens = new HashSet<string>(
                AgentSemanticLexicon.Tokenize(family.Types[index].TypeName),
                StringComparer.Ordinal);
            var distinct = index < family.TypeDistinctValues.Count
                ? family.TypeDistinctValues[index]
                : Array.Empty<string>();
            foreach (var value in distinct)
                foreach (var token in AgentSemanticLexicon.Tokenize(value))
                    tokens.Add(token);
            foreach (var nameToken in family.FamilyNameTokens)
                tokens.Remove(nameToken);
            if (!keys.Add(string.Join(
                    "|", tokens.OrderBy(token => token, StringComparer.Ordinal))))
            {
                continue;
            }

            if (keys.Count >= 2)
                return true;
        }

        return false;
    }

    /// <summary>
    /// The options of the differentiation question: the LEAF rows under the
    /// parent of the chosen code (the chosen code itself included) plus the
    /// honest none. A parent without at least two leaf children offers no
    /// alternative to ask about.
    /// </summary>
    private static IReadOnlyList<SemanticCandidate> SiblingOptionsOf(
        AgentClassificationTable table,
        string code)
    {
        var entry = table.Find(code);
        if (entry is null || entry.ParentCode is null)
            return Array.Empty<SemanticCandidate>();

        var siblings = table.Entries
            .Where(row => string.Equals(row.ParentCode, entry.ParentCode, StringComparison.OrdinalIgnoreCase)
                          && row.IsLeaf)
            .OrderBy(row => row.Code, AgentClassificationTable.CodeComparer)
            .ToList();
        if (siblings.Count < 2 || siblings.All(row => !string.Equals(
                row.Code, code, StringComparison.OrdinalIgnoreCase)))
        {
            return Array.Empty<SemanticCandidate>();
        }

        var options = new List<SemanticCandidate>(siblings.Count + 1);
        foreach (var sibling in siblings)
            options.Add(new SemanticCandidate(sibling.Code, CandidateText(sibling)));
        options.Add(new SemanticCandidate(TypeAbstainId, AbstainText));
        return options;
    }

    /// <summary>
    /// The question text of one type: the category plus the composed type query
    /// (family name, type name, the values not shared by every type).
    /// </summary>
    private static string TypeQueryOf(ClassifyFamilyGroup family, int index)
    {
        var query = family.TypeQueries[index];
        return string.IsNullOrWhiteSpace(family.Category)
            ? query
            : family.Category + "; " + query;
    }

    private static string TypeStageStateOf(ClassifyOptions options)
    {
        var state =
            "Task: assign a classification code (Assembly Code) to the building element " +
            "described in each question. Every question of this call describes ONE type " +
            "(size) of a family whose code is already chosen - the family code, its " +
            "SIBLING leaf rows of the same parent branch of the table and an honest none " +
            "are the options. Pick a sibling when the type's own text (its name or the " +
            "values not shared by every type of the family) clearly describes that " +
            "sibling; pick the family code when the type fits it. Choose the none option " +
            "when no option fits: an honest none is better than a wrong code.";
        return AppendInstructions(state, options.Instructions);
    }

    private static SemanticMatch? TopOf(IReadOnlyList<SemanticMatch> matches) =>
        matches.Count == 0 ? null : matches[0];

    /// <summary>Answer of one type of the differentiation stage, when it was asked.</summary>
    private sealed record TypeStageAnswer(
        SemanticMatch FamilyCode,
        SemanticMatch? Chosen,
        IReadOnlyList<SemanticMatch> Matches);

    /// <summary>The stage outcome: per-type answers and, on a dead channel, the hard failure.</summary>
    private sealed class TypeStageResult
    {
        public required Dictionary<long, TypeStageAnswer> Answers { get; init; }

        public SemanticRankOutcome? HardFailure { get; init; }
    }
}
