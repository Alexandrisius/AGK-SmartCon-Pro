using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Agent;

/// <summary>
/// Tuning of <see cref="AgentClassificationMatcher"/>: the confidence cutoff,
/// how many codes survive the family stage and extra selection context for the
/// decision model.
/// </summary>
public sealed record ClassifyOptions(
    double MinScore = 0.5,
    int CandidateDepth = 6,
    string? Instructions = null)
{
    public const double DefaultMinScore = 0.5;

    public const int DefaultCandidateDepth = 6;

    public const int MinCandidateDepth = 2;

    public const int MaxCandidateDepth = 15;
}

/// <summary>
/// One element type selected for classification, copied off the Revit thread:
/// names, the current Assembly Code and the non-empty text values that describe
/// the type (the category display name, family name and type name are composed
/// by the matcher, not carried here).
/// </summary>
public sealed record ClassifyTypeSnapshot(
    long ElementId,
    string Category,
    string FamilyName,
    long? FamilyId,
    string TypeName,
    string? CurrentCode,
    IReadOnlyList<string> Values);

/// <summary>One family of the selection with its composed matching texts.</summary>
public sealed class ClassifyFamilyGroup
{
    public required string Key { get; init; }

    public required string Category { get; init; }

    public required string FamilyName { get; init; }

    public long? FamilyId { get; init; }

    public string FamilyText { get; internal set; } = string.Empty;

    public IReadOnlyList<string> FamilyNameTokens { get; internal set; } = Array.Empty<string>();

    public IReadOnlyList<string> TypeQueries { get; internal set; } = Array.Empty<string>();

    public IReadOnlyList<ClassifyTypeSnapshot> Types { get; internal set; } =
        Array.Empty<ClassifyTypeSnapshot>();
}

/// <summary>What happened to one type: its proposed code or why there is none.</summary>
public enum ClassifyTypeStatus
{
    Proposed = 0,
    LowConfidence = 1,
    NoCandidates = 2,
    AlreadySet = 3
}

/// <summary>An alternative code of an uncertain type, with its score and path.</summary>
public sealed record ClassifyCodeCandidate(string Code, double Score, string Breadcrumb);

/// <summary>The classification outcome of one type.</summary>
public sealed record ClassifyTypeResult(
    ClassifyTypeSnapshot Type,
    ClassifyTypeStatus Status,
    string? Code,
    double Score,
    IReadOnlyList<ClassifyCodeCandidate> Candidates);

/// <summary>One (family, code) group of proposed types: the report row.</summary>
public sealed record ClassifyAssignmentGroup(
    long? FamilyId,
    string Family,
    string Category,
    string Code,
    string Breadcrumb,
    double Score,
    int TypeIdCount,
    IReadOnlyList<long> TypeIds,
    IReadOnlyList<string> TypeNames);

/// <summary>One ready set_parameters item of the proposed codes.</summary>
public sealed record ClassifySetParameterItem(long ElementId, string Parameter, string Value, string Scope);

/// <summary>The aggregate result of a classify_types run.</summary>
public sealed class ClassifyMatchResult
{
    public IReadOnlyList<ClassifyTypeResult> Types { get; init; } =
        Array.Empty<ClassifyTypeResult>();

    public IReadOnlyList<ClassifyAssignmentGroup> Assignments { get; init; } =
        Array.Empty<ClassifyAssignmentGroup>();

    /// <summary>set_parameters items of the proposed codes, packed into calls of at most 500.</summary>
    public IReadOnlyList<IReadOnlyList<ClassifySetParameterItem>> SetParameterBatches { get; init; } =
        Array.Empty<IReadOnlyList<ClassifySetParameterItem>>();

    /// <summary>null, or the decision-level error when nothing was answered at all.</summary>
    public string? Error { get; init; }

    public string? ErrorDetail { get; init; }

    public bool Incomplete { get; init; }

    /// <summary>Family (or family / type) names the decision layer did not answer.</summary>
    public IReadOnlyList<string> NotProcessed { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// The classification pipeline of <c>classify_types</c>, pure data in and pure
/// data out: groups the selected types into families, narrows the leaf codes of
/// the table with a lexical prefilter (plus a branch question for weak
/// families), then asks the decision model one question per family and one
/// question per type. The Revit side only copies the snapshot and serializes
/// the result.
/// </summary>
public sealed class AgentClassificationMatcher
{
    /// <summary>Default pick rules of every classify_types decision state.</summary>
    public const string PickInstructions =
        "Pick the single most specific classification entry that describes the element. " +
        "Prefer an entry of the same discipline. If nothing fits well, choose the closest " +
        "anyway - the score will show the doubt.";

    /// <summary>Leaves the lexical prefilter keeps per family.</summary>
    public const int LexicalTopCount = 30;

    /// <summary>Hard cap of a family candidate list (branch expansion).</summary>
    public const int LexicalHardCap = 40;

    /// <summary>Parameter name of the set_parameters items, stable on every Revit version.</summary>
    public const string AssemblyCodeParameter = "UNIFORMAT_CODE";

    private const int BranchTop = 3;
    private const int MinLexicalLeaves = 5;
    private const int MinPrefixLength = 5;
    private const int MaxInstructionsLength = 1500;
    private const int SetParameterBatchSize = 500;
    private const int MaxFamilyTextLength = 600;

    private readonly AgentSemanticRanker _ranker;

    public AgentClassificationMatcher(AgentSemanticRanker ranker) =>
        _ranker = ranker ?? throw new ArgumentNullException(nameof(ranker));

    /// <summary>
    /// Runs the whole pipeline over the grouped snapshot: lexical prefilter,
    /// branch questions for weak families, one question per family, one question
    /// per type. Decision-layer failures never throw - they surface as
    /// <see cref="ClassifyMatchResult.Error"/> or as an incomplete result.
    /// </summary>
    public async Task<ClassifyMatchResult> MatchAsync(
        IReadOnlyList<ClassifyFamilyGroup> families,
        AgentClassificationTable table,
        ClassifyOptions options,
        CancellationToken ct)
    {
        if (families.Count == 0)
            return new ClassifyMatchResult();

        var leaves = table.Entries.Where(entry => entry.IsLeaf).ToArray();
        if (leaves.Length == 0)
            return EveryType(families, ClassifyTypeStatus.NoCandidates);

        var leafTokens = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var leaf in leaves)
            leafTokens[leaf.Code] = new HashSet<string>(AgentSemanticLexicon.Tokenize(leaf.Breadcrumb), StringComparer.Ordinal);

        var notProcessed = new List<string>();
        var warnings = new List<string>();
        var candidates = new Dictionary<string, IReadOnlyList<AgentClassificationEntry>>(
            StringComparer.Ordinal);
        var weak = new List<ClassifyFamilyGroup>();
        foreach (var family in families)
        {
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
            var failure = await ExpandWeakFamilies(
                weak, table, leaves, leafTokens, candidates,
                notProcessed, options, ct).ConfigureAwait(false);
            if (failure is not null)
                return Failed(failure, notProcessed, warnings);
        }

        var staged = families.Where(family => candidates.ContainsKey(family.Key)).ToList();
        foreach (var family in families)
        {
            if (!candidates.ContainsKey(family.Key) && !notProcessed.Contains(family.FamilyName))
                notProcessed.Add(family.FamilyName);
        }

        if (staged.Count == 0)
            return EveryType(families, ClassifyTypeStatus.NoCandidates, notProcessed, warnings);

        // Stage 1: one question per family narrows the candidates to the best
        // candidateDepth codes.
        var familyOutcome = await _ranker.RankQueriesAsync(
            StateOf(options,
                "Task: assign a classification code (Assembly Code) to the building element " +
                "described in each question."),
            staged.Select(family => (
                family.Key,
                (IReadOnlyList<SemanticCandidate>)CandidatesOf(candidates[family.Key]),
                family.FamilyText)).ToList(),
            options.CandidateDepth,
            ct).ConfigureAwait(false);
        if (familyOutcome.Error == "decision_unavailable")
            return Failed(familyOutcome, notProcessed, warnings);

        var codesByFamily = new Dictionary<string, IReadOnlyList<SemanticMatch>>(StringComparer.Ordinal);
        for (var index = 0; index < staged.Count; index++)
        {
            if (familyOutcome.IncompleteQueryIds.Contains(staged[index].Key))
            {
                if (!notProcessed.Contains(staged[index].FamilyName))
                    notProcessed.Add(staged[index].FamilyName);
                continue;
            }

            codesByFamily[staged[index].Key] = familyOutcome.Matches[index];
        }

        if (codesByFamily.Count == 0)
            return Failed(familyOutcome, notProcessed, warnings);

        // Stage 2: one question per TYPE picks the final code out of the family
        // shortlist; the top 3 survive for the uncertain list.
        var typeQueries = new List<(string, IReadOnlyList<SemanticCandidate>, string)>();
        var typesByQuery = new List<ClassifyTypeSnapshot>();
        foreach (var family in staged)
        {
            if (!codesByFamily.TryGetValue(family.Key, out var familyCodes))
                continue;

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
        if (typeOutcome?.Error == "decision_unavailable")
            return Failed(typeOutcome, notProcessed, warnings);

        return BuildResult(
            families, table, options, typeOutcome, typesByQuery, notProcessed, warnings);
    }

    /// <summary>
    /// Weak families (fewer lexical hits than the threshold) get a branch
    /// question first: the 3 best top-level branches, then the lexical top of
    /// the leaves under them (or the first 40 when nothing scores).
    /// </summary>
    private async Task<SemanticRankOutcome?> ExpandWeakFamilies(
        IReadOnlyList<ClassifyFamilyGroup> weak,
        AgentClassificationTable table,
        IReadOnlyList<AgentClassificationEntry> leaves,
        Dictionary<string, HashSet<string>> leafTokens,
        Dictionary<string, IReadOnlyList<AgentClassificationEntry>> candidates,
        List<string> notProcessed,
        ClassifyOptions options,
        CancellationToken ct)
    {
        var roots = table.Entries.Where(entry => entry.Level == 1).ToArray();
        if (roots.Length == 0)
        {
            // A flat table has no branches to ask about: the lexical hits stay.
            foreach (var family in weak)
            {
                var scored = AgentSemanticLexicon.LexicalTop(
                    family.FamilyNameTokens, Array.Empty<string>(),
                    leaves, leafTokens, LexicalTopCount);
                if (scored.Count > 0)
                    candidates[family.Key] = scored;
            }

            return null;
        }

        var outcome = await _ranker.RankQueriesAsync(
            StateOf(options,
                "Task: pick the top-level branch of the classification table the building " +
                "element described in each question belongs to."),
            weak.Select(family => (
                family.Key,
                (IReadOnlyList<SemanticCandidate>)roots
                    .Select(root => new SemanticCandidate(root.Code, root.Breadcrumb))
                    .ToArray(),
                family.FamilyText)).ToList(),
            BranchTop,
            ct).ConfigureAwait(false);
        if (outcome.Error == "decision_unavailable")
            return outcome;

        var byRoot = new Dictionary<string, List<AgentClassificationEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var leaf in leaves)
        {
            var root = RootOf(table, leaf);
            if (root is null)
                continue;

            if (!byRoot.TryGetValue(root, out var bucket))
                byRoot[root] = bucket = new List<AgentClassificationEntry>();
            bucket.Add(leaf);
        }

        for (var index = 0; index < weak.Count; index++)
        {
            if (outcome.IncompleteQueryIds.Contains(weak[index].Key))
                continue;

            var chosenRoots = new HashSet<string>(
                outcome.Matches[index].Select(match => match.Id), StringComparer.OrdinalIgnoreCase);
            var underRoots = byRoot
                .Where(pair => chosenRoots.Contains(pair.Key))
                .SelectMany(pair => pair.Value)
                .ToArray();
            if (underRoots.Length == 0)
                continue;

            var family = weak[index];
            var scored = AgentSemanticLexicon.LexicalTop(
                family.FamilyNameTokens,
                AgentSemanticLexicon.Tokenize(family.FamilyText)
                    .Where(token => !family.FamilyNameTokens.Contains(token))
                    .ToArray(),
                underRoots, leafTokens, LexicalHardCap);
            candidates[family.Key] = scored.Count > 0
                ? scored
                : underRoots.Take(LexicalHardCap).ToArray();
        }

        return null;
    }

    private static ClassifyMatchResult BuildResult(
        IReadOnlyList<ClassifyFamilyGroup> families,
        AgentClassificationTable table,
        ClassifyOptions options,
        SemanticRankOutcome? typeOutcome,
        List<ClassifyTypeSnapshot> typesByQuery,
        List<string> notProcessed,
        List<string> warnings)
    {
        var results = new List<ClassifyTypeResult>(typesByQuery.Count);
        var incomplete = false;
        var processed = new HashSet<long>();
        for (var index = 0; index < typesByQuery.Count; index++)
        {
            var type = typesByQuery[index];
            processed.Add(type.ElementId);
            if (typeOutcome is null
                || typeOutcome.IncompleteQueryIds.Contains("type:" + type.ElementId))
            {
                incomplete = true;
                if (!notProcessed.Contains(type.FamilyName + " / " + type.TypeName))
                    notProcessed.Add(type.FamilyName + " / " + type.TypeName);
                continue;
            }

            var matches = typeOutcome.Matches[index];
            if (matches.Count == 0)
            {
                results.Add(new ClassifyTypeResult(
                    type, ClassifyTypeStatus.NoCandidates, null, 0, Array.Empty<ClassifyCodeCandidate>()));
                continue;
            }

            var best = matches[0];
            var candidates = matches
                .Take(3)
                .Select(match => (Match: match, Entry: table.Find(match.Id)))
                .Where(pair => pair.Entry is not null)
                .Select(pair => new ClassifyCodeCandidate(
                    pair.Match.Id, pair.Match.Score, pair.Entry!.Breadcrumb))
                .ToArray();
            var status = best.Score >= options.MinScore
                ? ClassifyTypeStatus.Proposed
                : ClassifyTypeStatus.LowConfidence;
            if (status == ClassifyTypeStatus.Proposed
                && string.Equals(
                    type.CurrentCode?.Trim(), best.Id.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                status = ClassifyTypeStatus.AlreadySet;
            }

            results.Add(new ClassifyTypeResult(type, status, best.Id, best.Score, candidates));
        }

        // Families that never reached stage 2 (no candidates or not processed)
        // are still reported, honestly as no_candidates.
        foreach (var type in families.SelectMany(family => family.Types))
        {
            if (processed.Add(type.ElementId))
            {
                results.Add(new ClassifyTypeResult(
                    type, ClassifyTypeStatus.NoCandidates, null, 0, Array.Empty<ClassifyCodeCandidate>()));
            }
        }

        if (notProcessed.Count > 0)
        {
            warnings.Add(
                "The decision layer did not answer " + notProcessed.Count + " question(s): " +
                string.Join("; ", notProcessed.Take(10)) +
                (notProcessed.Count > 10 ? " …" : string.Empty) +
                ". [Action: re-run classify_types narrowed to the missing part " +
                "or match those entries manually]");
        }

        var assignments = results
            .Where(result => result.Status == ClassifyTypeStatus.Proposed && result.Code is not null)
            .GroupBy(result => new { Family = result.Type.FamilyName, Code = result.Code! })
            .Select(group =>
            {
                var items = group.OrderBy(item => item.Type.TypeName, StringComparer.OrdinalIgnoreCase).ToList();
                return new ClassifyAssignmentGroup(
                    items[0].Type.FamilyId,
                    group.Key.Family,
                    items[0].Type.Category,
                    group.Key.Code,
                    table.Find(group.Key.Code)?.Breadcrumb ?? string.Empty,
                    items.Min(item => item.Score),
                    items.Count,
                    items.Select(item => item.Type.ElementId).Take(50).ToArray(),
                    items.Select(item => item.Type.TypeName).Take(5).ToArray());
            })
            .OrderBy(group => group.Family, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Code, AgentClassificationTable.CodeComparer)
            .ToArray();

        var setItems = results
            .Where(result => result.Status == ClassifyTypeStatus.Proposed && result.Code is not null)
            .Select(result => new ClassifySetParameterItem(
                result.Type.ElementId, AssemblyCodeParameter, result.Code!, "type"))
            .ToArray();
        var batches = new List<IReadOnlyList<ClassifySetParameterItem>>();
        for (var start = 0; start < setItems.Length; start += SetParameterBatchSize)
            batches.Add(setItems.Skip(start).Take(SetParameterBatchSize).ToArray());

        return new ClassifyMatchResult
        {
            Types = results,
            Assignments = assignments,
            SetParameterBatches = batches,
            Incomplete = incomplete,
            NotProcessed = notProcessed,
            Warnings = warnings
        };
    }

    private static string? RootOf(AgentClassificationTable table, AgentClassificationEntry entry)
    {
        var current = entry;
        var guard = 0;
        while (current.ParentCode is not null && guard++ <= table.Entries.Count)
        {
            var parent = table.Find(current.ParentCode);
            if (parent is null)
                return current.ParentCode;

            if (parent.Level <= 1)
                return parent.Code;

            current = parent;
        }

        return current.Level <= 1 ? current.Code : null;
    }

    private static IReadOnlyList<SemanticCandidate> CandidatesOf(
        IReadOnlyList<AgentClassificationEntry> leaves) =>
        leaves.Select(leaf => new SemanticCandidate(leaf.Code, CandidateText(leaf))).ToArray();

    private static IReadOnlyList<SemanticCandidate> CodesOf(
        IReadOnlyList<AgentClassificationEntry> leaves,
        IReadOnlyList<SemanticMatch> ranked)
    {
        var byCode = new Dictionary<string, AgentClassificationEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var leaf in leaves)
            byCode[leaf.Code] = leaf;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return ranked
            .Where(match => byCode.TryGetValue(match.Id, out _) && seen.Add(match.Id))
            .Select(match => new SemanticCandidate(match.Id, CandidateText(byCode[match.Id])))
            .ToArray();
    }

    private static string CandidateText(AgentClassificationEntry leaf) =>
        leaf.Breadcrumb + " (" + leaf.Code + ")";

    private static string StateOf(ClassifyOptions options, string task)
    {
        var state = task +
            " Options are rows of the classification table loaded in the document, " +
            "formatted 'breadcrumb (code)'." + PickInstructions;
        if (!string.IsNullOrWhiteSpace(options.Instructions))
        {
            var trimmed = options.Instructions!.Trim();
            if (trimmed.Length > MaxInstructionsLength)
                trimmed = trimmed.Substring(0, MaxInstructionsLength);
            state += "\nSelection context:\n" + trimmed;
        }

        return state;
    }

    private static ClassifyMatchResult EveryType(
        IReadOnlyList<ClassifyFamilyGroup> families,
        ClassifyTypeStatus status,
        IReadOnlyList<string>? notProcessed = null,
        IReadOnlyList<string>? warnings = null) => new()
    {
        Types = families
            .SelectMany(family => family.Types)
            .Select(type => new ClassifyTypeResult(
                type, status, null, 0, Array.Empty<ClassifyCodeCandidate>()))
            .ToArray(),
        NotProcessed = notProcessed ?? Array.Empty<string>(),
        Warnings = warnings ?? Array.Empty<string>()
    };

    private static ClassifyMatchResult Failed(
        SemanticRankOutcome outcome,
        List<string> notProcessed,
        List<string> warnings) => new()
    {
        Error = outcome.Error ?? "decision_call_failed",
        ErrorDetail = outcome.ErrorDetail,
        Incomplete = true,
        NotProcessed = notProcessed,
        Warnings = warnings
    };
}
