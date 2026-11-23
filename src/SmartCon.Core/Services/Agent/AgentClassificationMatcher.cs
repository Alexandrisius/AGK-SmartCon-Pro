using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Agent;

/// <summary>
/// The classification pipeline of <c>classify_types</c>, pure data in and pure
/// data out. Strong families (lexical hits) go straight to the family and type
/// questions; weak families get a branch question and, when the lexical well is
/// dry, a level-by-level semantic descent whose options are always the children
/// of the previously chosen row - never "the first N leaves of the table".
/// Decision-channel failures surface as a typed error, never as no_candidates.
/// </summary>
public sealed partial class AgentClassificationMatcher
{
    /// <summary>Default pick rules of the non-descent questions.</summary>
    public const string PickInstructions =
        "Pick the single most specific classification entry that describes the element. " +
        "Prefer an entry of the same discipline. If nothing fits well, choose the closest " +
        "anyway - the score will show the doubt.";

    /// <summary>Leaves the lexical prefilter keeps per family.</summary>
    public const int LexicalTopCount = 30;

    /// <summary>Leaves the lexical prefilter keeps under the chosen branches.</summary>
    public const int LexicalHardCap = 40;

    /// <summary>Parameter name of the set_parameters items, stable on every Revit version.</summary>
    public const string AssemblyCodeParameter = "UNIFORMAT_CODE";

    /// <summary>The classify hint the tool returns when the decision layer is unusable.</summary>
    public const string DecisionHint =
        "Tell the user the small decision model is unavailable; retry later, or pick the " +
        "codes manually - list_classification_entries shows the table, set_parameters " +
        "writes the chosen code";

    private const int BranchTop = 3;
    private const int MinLexicalLeaves = 5;
    private const int MaxInstructionsLength = 1500;
    private const int SetParameterBatchSize = 500;
    private const int AlreadySetPayloadCap = 50;

    private readonly AgentSemanticRanker _ranker;

    public AgentClassificationMatcher(AgentSemanticRanker ranker) =>
        _ranker = ranker ?? throw new ArgumentNullException(nameof(ranker));

    /// <summary>
    /// Runs the whole pipeline over the grouped snapshot. Decision-layer
    /// failures never throw - they surface as <see cref="ClassifyMatchResult.Error"/>
    /// or as an incomplete result with the unanswered families named.
    /// </summary>
    public async Task<ClassifyMatchResult> MatchAsync(
        IReadOnlyList<ClassifyFamilyGroup> families,
        AgentClassificationTable table,
        ClassifyOptions options,
        CancellationToken ct)
    {
        if (families.Count == 0)
            return new ClassifyMatchResult();

        var run = new RunState();
        var leaves = table.Entries.Where(entry => entry.IsLeaf).ToArray();
        if (leaves.Length == 0)
        {
            run.Warnings.Add(
                "The classification table has no leaf rows, so no code can be assigned. " +
                "[Action: load a table with leaf rows via load_classification_table or " +
                "pick the codes manually with list_classification_entries]");
            return EveryType(families, ClassifyTypeStatus.NoCandidates, run);
        }

        var leafTokens = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var leaf in leaves)
            leafTokens[leaf.Code] = new HashSet<string>(
                AgentSemanticLexicon.Tokenize(leaf.Breadcrumb), StringComparer.Ordinal);

        var candidates = new Dictionary<string, IReadOnlyList<AgentClassificationEntry>>(
            StringComparer.Ordinal);
        var chainScores = new Dictionary<string, double>(StringComparer.Ordinal);
        var precomputed = new List<(ClassifyFamilyGroup Family, IReadOnlyList<SemanticMatch> Codes)>();
        var weak = new List<ClassifyFamilyGroup>();
        foreach (var family in families)
        {
            // A family without any descriptive text (empty category, family
            // name and parameter values) has nothing to match by: asking the
            // model about it would only invite a guess.
            if (string.IsNullOrWhiteSpace(family.FamilyText))
            {
                run.FamilyReasons[family.Key] =
                    "the family carries no descriptive text (empty category, family name and " +
                    "parameter values) - there is nothing to match by";
                continue;
            }

            // Family name tokens weigh double, the rest of the family text single.
            var other = AgentSemanticLexicon.Tokenize(family.FamilyText)
                .Where(token => !family.FamilyNameTokens.Contains(token))
                .ToArray();
            var scored = AgentSemanticLexicon.LexicalTop(
                family.FamilyNameTokens, other, leaves, leafTokens, LexicalTopCount);
            if (scored.Count >= MinLexicalLeaves)
                candidates[family.Key] = scored;
            else
                weak.Add(family);
        }

        if (weak.Count > 0)
        {
            var weakFailure = await ResolveWeakFamilies(
                weak, table, leaves, leafTokens, candidates, chainScores, precomputed,
                run, options, ct).ConfigureAwait(false);
            if (weakFailure is not null)
                return Failed(weakFailure, run);
        }

        var staged = families.Where(family => candidates.ContainsKey(family.Key)).ToList();
        foreach (var family in families)
        {
            if (candidates.ContainsKey(family.Key) || run.FamilyReasons.ContainsKey(family.Key))
                continue;

            run.MarkNotProcessed(family.FamilyName);
        }

        if (staged.Count == 0)
        {
            // A dead channel is a typed failure, never "no candidates" (D1).
            if (run.ChannelFailed)
            {
                return Failed(
                    run.FirstError ?? "decision_call_failed",
                    run.FirstErrorDetail ?? "the decision channel answered no question",
                    run);
            }

            return EveryType(families, ClassifyTypeStatus.NoCandidates, run);
        }

        return await MatchStagedFamiliesAsync(
            families, staged, table, options, candidates, chainScores, precomputed, run, ct)
            .ConfigureAwait(false);
    }

    private async Task<ClassifyMatchResult> MatchStagedFamiliesAsync(
        IReadOnlyList<ClassifyFamilyGroup> families,
        IReadOnlyList<ClassifyFamilyGroup> staged,
        AgentClassificationTable table,
        ClassifyOptions options,
        Dictionary<string, IReadOnlyList<AgentClassificationEntry>> candidates,
        Dictionary<string, double> chainScores,
        List<(ClassifyFamilyGroup Family, IReadOnlyList<SemanticMatch> Codes)> precomputed,
        RunState run,
        CancellationToken ct)
    {
        // Descent families arrive with their single leaf code already chosen:
        // only the lexical families need the stage-1 question.
        var codesByFamily = new Dictionary<string, IReadOnlyList<SemanticMatch>>(StringComparer.Ordinal);
        foreach (var pair in precomputed)
            codesByFamily[pair.Family.Key] = pair.Codes;

        var stage1 = staged.Where(family => !codesByFamily.ContainsKey(family.Key)).ToList();
        if (stage1.Count > 0)
        {
            var familyOutcome = await _ranker.RankQueriesAsync(
                StateOf(options,
                    "Task: assign a classification code (Assembly Code) to the building element " +
                    "described in each question."),
                stage1.Select(family => (
                    family.Key,
                    (IReadOnlyList<SemanticCandidate>)CandidatesOf(candidates[family.Key]),
                    family.FamilyText)).ToList(),
                options.CandidateDepth,
                ct).ConfigureAwait(false);
            run.Observe(familyOutcome);
            if (familyOutcome.Error == "decision_unavailable")
                return Failed(familyOutcome, run);

            for (var index = 0; index < stage1.Count; index++)
            {
                if (familyOutcome.IncompleteQueryIds.Contains(stage1[index].Key))
                {
                    run.MarkNotProcessed(stage1[index].FamilyName);
                    continue;
                }

                codesByFamily[stage1[index].Key] = familyOutcome.Matches[index];
            }

            if (codesByFamily.Count == 0)
                return Failed(familyOutcome, run);
        }

        // Stage 2: one question per TYPE of the multi-code families. Families
        // with a single code (the descent result) are answered directly from
        // the chain score - a one-option question would only invite guesses.
        var typeQueries = new List<(string, IReadOnlyList<SemanticCandidate>, string)>();
        var typesByQuery = new List<ClassifyTypeSnapshot>();
        foreach (var family in staged)
        {
            if (!codesByFamily.TryGetValue(family.Key, out var familyCodes)
                || familyCodes.Count < 2)
            {
                continue;
            }

            var codeOptions = CodesOf(candidates[family.Key], familyCodes);
            for (var index = 0; index < family.Types.Count; index++)
            {
                typeQueries.Add(("type:" + family.Types[index].ElementId,
                    codeOptions, family.TypeQueries[index]));
                typesByQuery.Add(family.Types[index]);
            }
        }

        var typeOutcome = typeQueries.Count == 0
            ? null
            : await _ranker.RankQueriesAsync(
                StateOf(options,
                    "Task: assign a classification code (Assembly Code) to the building element " +
                    "described in each question. Every question of this call describes ONE type " +
                    "(size) of a family whose candidate codes are already shortlisted in the " +
                    "options - pick the code that fits the described type."),
                typeQueries, 3, ct).ConfigureAwait(false);
        run.Observe(typeOutcome);
        if (typeOutcome?.Error == "decision_unavailable")
            return Failed(typeOutcome, run);

        // A full failure of the type stage keeps its typed error (D1).
        if (typeOutcome is not null && typeOutcome.Error is not null
            && typesByQuery.All(type =>
                typeOutcome.IncompleteQueryIds.Contains("type:" + type.ElementId)))
        {
            return Failed(typeOutcome, run);
        }

        // Stage 3: single-code families with types that differ by more than
        // size may earn per-type codes from the siblings of the chosen code.
        // The stage is optional by contract: its calls count, its failures and
        // leftovers are warnings - the family codes already stand.
        var differentiation = await RunTypeDifferentiationAsync(
            staged, table, codesByFamily, chainScores, options, run, ct).ConfigureAwait(false);

        return BuildResult(
            families, staged, table, options, typeOutcome, typesByQuery,
            codesByFamily, chainScores, run, differentiation);
    }

    /// <summary>Per-run bookkeeping shared by every stage.</summary>
    private sealed class RunState
    {
        public List<string> NotProcessed { get; } = new();

        public List<string> Warnings { get; } = new();

        /// <summary>True reason per family key, for families that reached no code.</summary>
        public Dictionary<string, string> FamilyReasons { get; } = new(StringComparer.Ordinal);

        public int DecisionCalls { get; private set; }

        public bool AnswersReceived { get; private set; }

        public int Unanswered { get; private set; }

        public string? FirstError { get; private set; }

        public string? FirstErrorDetail { get; private set; }

        public bool ChannelFailed => FirstError is not null || Unanswered > 0;

        /// <summary>
        /// Bookkeeping of the optional per-type stage: its calls are real and
        /// count, but a failure there never fails the run - the family codes
        /// stand, and the stage reports what was left as a warning.
        /// </summary>
        public void ObserveOptional(SemanticRankOutcome outcome)
        {
            DecisionCalls += outcome.CallsMade;
            if (outcome.Error is null && outcome.Matches.Any(row => row.Count > 0))
                AnswersReceived = true;
        }

        public void Observe(SemanticRankOutcome? outcome)
        {
            if (outcome is null)
                return;

            DecisionCalls += outcome.CallsMade;
            if (outcome.Error is not null)
            {
                FirstError ??= outcome.Error;
                FirstErrorDetail ??= outcome.ErrorDetail;
            }

            Unanswered += outcome.IncompleteQueryIds.Count;
            if (outcome.Error is null && outcome.Matches.Any(row => row.Count > 0))
                AnswersReceived = true;
        }

        public void MarkNotProcessed(string name)
        {
            if (!NotProcessed.Contains(name))
                NotProcessed.Add(name);
        }
    }
}
