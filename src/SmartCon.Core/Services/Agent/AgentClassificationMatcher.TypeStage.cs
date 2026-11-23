using System.Globalization;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Agent;

/// <summary>
/// The per-type differentiation stage of <see cref="AgentClassificationMatcher"/>:
/// a family that ended with ONE code (the descent result or a single surviving
/// candidate) may still carry types whose own text - the type name plus the
/// values not shared by every type - describes a different row of the table.
/// The types that differ by more than size or article number are grouped by
/// their significant token set and each group is asked ONE question: the
/// options are the family code, its sibling leaves under the same parent branch
/// and an honest none, and the answer applies to every type of the group. The
/// stage is optional: whatever is left - a failed call, a refusal, the question
/// budget - keeps the family code and is named in a warning.
/// </summary>
public sealed partial class AgentClassificationMatcher
{
    /// <summary>Id of the honest-none option of the differentiation question.</summary>
    private const string TypeAbstainId = "__none__";

    /// <summary>
    /// At most this many differentiation questions per classify call (the ranker
    /// packs them into decision calls of at most
    /// <see cref="AgentSemanticRanker.MaxQuestionsPerCall"/> questions). The
    /// types beyond it keep the family code and are named in a warning.
    /// </summary>
    public const int MaxTypeDifferentiationQuestions = 100;

    /// <summary>Type ids one warning lists; the rest is only counted.</summary>
    private const int LeftoverIdsInWarning = 200;

    /// <summary>Families one warning names; the rest is only counted.</summary>
    private const int LeftoverFamiliesInWarning = 20;

    private const string LeftoverAction =
        "[Action: accept the family codes, or to tell their types apart re-run " +
        "classify_types with typeIds set to the listed ids and onlyEmpty=false when " +
        "their codes are already written]";

    /// <summary>
    /// Asks the differentiation questions and maps every type that got a usable
    /// answer to it. The types without an entry keep the family code; what was
    /// not asked or not answered is reported as a warning, never as a failure -
    /// the family codes already stand.
    /// </summary>
    private async Task<Dictionary<long, TypeStageAnswer>> RunTypeDifferentiationAsync(
        IReadOnlyList<ClassifyFamilyGroup> staged,
        AgentClassificationTable table,
        Dictionary<string, IReadOnlyList<SemanticMatch>> codesByFamily,
        Dictionary<string, double> chainScores,
        ClassifyOptions options,
        RunState run,
        CancellationToken ct)
    {
        var answers = new Dictionary<long, TypeStageAnswer>();
        var questions = new List<TypeQuestion>();
        var leftOut = new List<LeftoverFamily>();
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
            var familyScore = FamilyScoreOf(single, family.Key, chainScores);
            if (single.BareChoice || familyScore < options.MinScore || family.Types.Count < 2)
                continue;

            // One question per significant token set: a row of sizes or article
            // numbers is one question, not one per type.
            var groups = TypeGroupsOf(family);
            var questionOptions = groups.Count < 2
                ? Array.Empty<SemanticCandidate>()
                : SiblingOptionsOf(table, single.Id);
            if (questionOptions.Count == 0)
                continue;

            foreach (var (_, indexes) in groups)
            {
                if (questions.Count >= MaxTypeDifferentiationQuestions)
                {
                    AddLeftout(leftOut, family, indexes);
                    continue;
                }

                questions.Add(new TypeQuestion(family, indexes, single, questionOptions));
            }
        }

        if (leftOut.Count > 0)
        {
            run.Warnings.Add(LeftoverWarning(
                "the per-type question budget of " + MaxTypeDifferentiationQuestions +
                " per classify_types call was reached",
                leftOut));
        }

        if (questions.Count == 0)
            return answers;

        var outcome = await _ranker.RankQueriesAsync(
            TypeStageStateOf(options),
            questions.Select(question => (question.Id, question.Options, question.Query)).ToList(),
            3, ct).ConfigureAwait(false);
        run.ObserveOptional(outcome);

        var incomplete = new HashSet<string>(outcome.IncompleteQueryIds, StringComparer.Ordinal);
        var notAnswered = new List<LeftoverFamily>();
        for (var index = 0; index < questions.Count; index++)
        {
            var question = questions[index];
            var row = index < outcome.Matches.Count ? outcome.Matches[index] : null;
            if (row is null || row.Count == 0 || incomplete.Contains(question.Id))
            {
                AddLeftout(notAnswered, question.Family, question.Indexes);
                continue;
            }

            var answer = new TypeStageAnswer(question.FamilyCode, row[0], row);
            foreach (var type in question.Types)
                answers[type.ElementId] = answer;
        }

        if (notAnswered.Count > 0)
        {
            run.Warnings.Add(LeftoverWarning(
                outcome.Error is null
                    ? "the decision layer returned no usable answer to their per-type question(s)"
                    : "the decision layer failed (" + outcome.Error +
                      (string.IsNullOrEmpty(outcome.ErrorDetail)
                          ? string.Empty
                          : ": " + outcome.ErrorDetail) + ")",
                notAnswered));
        }

        return answers;
    }

    /// <summary>
    /// The family score of a single-code family: the code's own score, capped by
    /// the descent chain when the family came down the table.
    /// </summary>
    private static double FamilyScoreOf(
        SemanticMatch single,
        string familyKey,
        Dictionary<string, double> chainScores) =>
        chainScores.TryGetValue(familyKey, out var chain)
            ? System.Math.Min(single.Score, chain)
            : single.Score;

    /// <summary>
    /// The types of a family grouped by what they mean: one group per significant
    /// token set, in the order of the family's types.
    /// </summary>
    private static List<(string Key, List<int> Indexes)> TypeGroupsOf(ClassifyFamilyGroup family)
    {
        var groups = new List<(string, List<int>)>();
        var byKey = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var index = 0; index < family.Types.Count; index++)
        {
            var key = SignificanceKeyOf(family, index);
            if (!byKey.TryGetValue(key, out var indexes))
            {
                byKey[key] = indexes = new List<int>();
                groups.Add((key, indexes));
            }

            indexes.Add(index);
        }

        return groups;
    }

    /// <summary>
    /// What a type of a multi-type family is beyond its size: the tokens of its
    /// name and of the values not shared by every type - without the
    /// family-name tokens (every type carries them) and without size tokens.
    /// </summary>
    private static string SignificanceKeyOf(ClassifyFamilyGroup family, int index)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        AddMeaningTokens(tokens, family.Types[index].TypeName);
        var distinct = index < family.TypeDistinctValues.Count
            ? family.TypeDistinctValues[index]
            : Array.Empty<string>();
        foreach (var value in distinct)
            AddMeaningTokens(tokens, value);
        foreach (var familyToken in family.FamilyNameTokens)
            tokens.Remove(familyToken);
        return string.Join("|", tokens.OrderBy(token => token, StringComparer.Ordinal));
    }

    private static void AddMeaningTokens(HashSet<string> tokens, string text)
    {
        foreach (var token in AgentSemanticLexicon.Tokenize(text))
            if (!IsSizeToken(token))
                tokens.Add(token);
    }

    /// <summary>
    /// A token with a digit in it is a size or an article number (DN100, 200mm,
    /// 100x100, a grade or a GUID segment): it says which size, not what.
    /// </summary>
    private static bool IsSizeToken(string token)
    {
        foreach (var character in token)
        {
            if (char.IsDigit(character))
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
    /// The question text of one group: the category, the family and the first
    /// type (composed by the texts stage) - plus, for a group of several types,
    /// how many more share its meaning.
    /// </summary>
    private static string GroupQueryOf(ClassifyFamilyGroup family, IReadOnlyList<int> indexes)
    {
        var query = string.IsNullOrEmpty(family.Category)
            ? family.TypeQueries[indexes[0]]
            : family.Category + "; " + family.TypeQueries[indexes[0]];
        if (indexes.Count > 1)
        {
            var others = indexes.Skip(1).Take(3).Select(index => family.Types[index].TypeName);
            query += "; same for " + (indexes.Count - 1) +
                     " more type(s) that differ only in size or article number: " +
                     string.Join(", ", others) + (indexes.Count - 1 > 3 ? ", …" : string.Empty);
        }

        return query;
    }

    /// <summary>
    /// The differentiation question allows an honest none, and the none has a
    /// meaning: the instructions never demand "choose the closest anyway".
    /// </summary>
    private static string TypeStageStateOf(ClassifyOptions options)
    {
        var state =
            "Task: assign a classification code (Assembly Code) to the building element " +
            "described in each question. Every question of this call describes ONE type " +
            "of a family whose code is already chosen - the family code, its SIBLING " +
            "leaf rows of the same parent branch of the table and an honest none are " +
            "the options. Pick a sibling when the type's own text (its name or the " +
            "values not shared by every type of the family) describes that sibling; " +
            "pick the family code when the type fits it. Choose the none option when " +
            "neither the family code nor a sibling fits the type: an honest none is " +
            "better than a wrong code.";
        return AppendInstructions(state, options.Instructions);
    }

    private static void AddLeftout(
        List<LeftoverFamily> leftout,
        ClassifyFamilyGroup family,
        List<int> indexes)
    {
        var bucket = leftout.Find(item =>
            string.Equals(item.Key, family.Key, StringComparison.Ordinal));
        if (bucket is null)
            leftout.Add(bucket = new LeftoverFamily(family.Key, family.FamilyName));
        foreach (var index in indexes)
            bucket.TypeIds.Add(family.Types[index].ElementId);
    }

    /// <summary>
    /// Names the types that stay with the family code and why, with the ids a
    /// re-run needs - bounded, so a huge leftover cannot flood the payload.
    /// </summary>
    private static string LeftoverWarning(string cause, List<LeftoverFamily> leftout)
    {
        var parts = new List<string>();
        var listedTypes = 0;
        var idsLeft = LeftoverIdsInWarning;
        foreach (var family in leftout.Take(LeftoverFamiliesInWarning))
        {
            if (idsLeft <= 0)
                break;

            var ids = family.TypeIds.Take(idsLeft).ToList();
            idsLeft -= ids.Count;
            listedTypes += ids.Count;
            parts.Add("'" + family.Name + "' (typeIds " +
                      string.Join(", ", ids.Select(id => id.ToString(CultureInfo.InvariantCulture))) +
                      (ids.Count < family.TypeIds.Count ? ", …" : string.Empty) + ")");
        }

        var typeCount = leftout.Sum(item => item.TypeIds.Count);
        var rest = typeCount - listedTypes;
        return typeCount + " type(s) of " + leftout.Count +
               (leftout.Count == 1 ? " family" : " families") +
               " stay with the family code - " + cause + ": " +
               string.Join(", ", parts) +
               (rest > 0
                   ? "; and " + rest + " more type(s) not listed - select them by the " +
                     "categories or familyIds of their families"
                   : string.Empty) +
               ". " + LeftoverAction;
    }

    /// <summary>The answer one group of types received, shared by every type of the group.</summary>
    private sealed record TypeStageAnswer(
        SemanticMatch FamilyCode,
        SemanticMatch Chosen,
        IReadOnlyList<SemanticMatch> Matches);

    /// <summary>One differentiation question: a group of types that mean the same.</summary>
    private sealed class TypeQuestion
    {
        public TypeQuestion(
            ClassifyFamilyGroup family,
            List<int> indexes,
            SemanticMatch familyCode,
            IReadOnlyList<SemanticCandidate> options)
        {
            Indexes = indexes;
            Family = family;
            FamilyCode = familyCode;
            Options = options;
            Types = indexes.Select(index => family.Types[index]).ToArray();
            Id = "type:" + Types[0].ElementId.ToString(CultureInfo.InvariantCulture);
            Query = GroupQueryOf(family, indexes);
        }

        public string Id { get; }

        public ClassifyFamilyGroup Family { get; }

        public IReadOnlyList<ClassifyTypeSnapshot> Types { get; }

        public List<int> Indexes { get; }

        public SemanticMatch FamilyCode { get; }

        public IReadOnlyList<SemanticCandidate> Options { get; }

        public string Query { get; }
    }

    /// <summary>Types of one family that stay with the family code, by family key.</summary>
    private sealed class LeftoverFamily
    {
        public LeftoverFamily(string key, string name)
        {
            Key = key;
            Name = name;
        }

        public string Key { get; }

        public string Name { get; }

        public List<long> TypeIds { get; } = new();
    }
}
