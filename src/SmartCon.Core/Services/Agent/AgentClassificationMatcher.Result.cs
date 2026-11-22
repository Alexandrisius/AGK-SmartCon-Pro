using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Agent;

/// <summary>
/// Result assembly of <see cref="AgentClassificationMatcher"/>: per-type
/// statuses (with the bare-choice and chain-score honesty rules), the
/// assignment groups, the set_parameters batches and the typed failures.
/// </summary>
public sealed partial class AgentClassificationMatcher
{
    private static ClassifyMatchResult BuildResult(
        IReadOnlyList<ClassifyFamilyGroup> families,
        IReadOnlyList<ClassifyFamilyGroup> staged,
        AgentClassificationTable table,
        ClassifyOptions options,
        SemanticRankOutcome? typeOutcome,
        List<ClassifyTypeSnapshot> typesByQuery,
        Dictionary<string, IReadOnlyList<SemanticMatch>> codesByFamily,
        Dictionary<string, double> chainScores,
        RunState run)
    {
        var rowIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < typesByQuery.Count; index++)
            rowIndex["type:" + typesByQuery[index].ElementId] = index;

        var results = new List<ClassifyTypeResult>();
        var alreadySet = new List<ClassifyTypeResult>();
        var incomplete = false;
        var stagedKeys = new HashSet<string>(
            staged.Select(family => family.Key), StringComparer.Ordinal);

        foreach (var family in families)
        {
            foreach (var type in family.Types)
            {
                if (!stagedKeys.Contains(family.Key))
                {
                    // Never staged: an honest reason beats a bare no_candidates.
                    results.Add(new ClassifyTypeResult(
                        type, ClassifyTypeStatus.NoCandidates, null, 0,
                        Array.Empty<ClassifyCodeCandidate>())
                    {
                        Reason = run.FamilyReasons.TryGetValue(family.Key, out var familyReason)
                            ? familyReason
                            : null
                    });
                    continue;
                }

                if (!codesByFamily.TryGetValue(family.Key, out var familyCodes))
                {
                    incomplete = true;
                    run.MarkNotProcessed(type.FamilyName + " / " + type.TypeName);
                    continue;
                }

                // A single code (the descent result) answers its types directly
                // from the chain score; a one-option question would only invite
                // guesses.
                if (familyCodes.Count == 1)
                {
                    AddResult(type, familyCodes[0], familyCodes,
                        chainScores.TryGetValue(family.Key, out var chain)
                            ? System.Math.Min(familyCodes[0].Score, chain)
                            : familyCodes[0].Score,
                        table, options, results, alreadySet);
                    continue;
                }

                if (!rowIndex.TryGetValue("type:" + type.ElementId, out var row))
                {
                    incomplete = true;
                    run.MarkNotProcessed(type.FamilyName + " / " + type.TypeName);
                    continue;
                }

                if (typeOutcome is null
                    || typeOutcome.IncompleteQueryIds.Contains("type:" + type.ElementId))
                {
                    incomplete = true;
                    run.MarkNotProcessed(type.FamilyName + " / " + type.TypeName);
                    continue;
                }

                var matches = typeOutcome.Matches[row];
                if (matches.Count == 0)
                {
                    results.Add(new ClassifyTypeResult(
                        type, ClassifyTypeStatus.NoCandidates, null, 0,
                        Array.Empty<ClassifyCodeCandidate>())
                    {
                        Reason = "the model returned no code for the type"
                    });
                    continue;
                }

                var chainApplied = chainScores.TryGetValue(family.Key, out var familyChain)
                    ? System.Math.Min(matches[0].Score, familyChain)
                    : matches[0].Score;
                AddResult(type, matches[0], matches, chainApplied, table, options, results, alreadySet);
            }
        }

        if (run.NotProcessed.Count > 0)
        {
            run.Warnings.Add(
                "The decision layer did not answer " + run.NotProcessed.Count + " question(s): " +
                string.Join("; ", run.NotProcessed.Take(10)) +
                (run.NotProcessed.Count > 10 ? " …" : string.Empty) +
                ". [Action: re-run classify_types narrowed to the missing part " +
                "or match those entries manually]");
        }

        AddReasonWarnings(run, families);

        var proposed = results.Where(result => result.Status == ClassifyTypeStatus.Proposed).ToList();
        return new ClassifyMatchResult
        {
            Types = results,
            Assignments = AssignmentsOf(proposed, table),
            SetParameterBatches = BatchesOf(proposed),
            Incomplete = incomplete || run.NotProcessed.Count > 0,
            NotProcessed = run.NotProcessed.ToArray(),
            Warnings = run.Warnings.ToArray(),
            DecisionUsed = run.AnswersReceived,
            DecisionCalls = run.DecisionCalls,
            FamiliesProcessed = staged.Count,
            AlreadySetItems = alreadySet.Take(AlreadySetPayloadCap).ToArray(),
            Error = run.FirstError,
            ErrorDetail = run.FirstErrorDetail
        };
    }

    private static void AddResult(
        ClassifyTypeSnapshot type,
        SemanticMatch best,
        IReadOnlyList<SemanticMatch> matches,
        double score,
        AgentClassificationTable table,
        ClassifyOptions options,
        List<ClassifyTypeResult> results,
        List<ClassifyTypeResult> alreadySet)
    {
        var candidates = matches
            .Take(3)
            .Select(match => (Match: match, Entry: table.Find(match.Id)))
            .Where(pair => pair.Entry is not null)
            .Select(pair => new ClassifyCodeCandidate(
                pair.Match.Id, pair.Match.Score, pair.Entry!.Breadcrumb))
            .ToArray();

        // A bare choice carries no confidence: at the default minScore it is a
        // guess and lands in uncertain, never in set_parameters.
        var status = best.BareChoice && options.MinScore >= ClassifyOptions.DefaultMinScore
            ? ClassifyTypeStatus.NoConfidence
            : score >= options.MinScore
                ? ClassifyTypeStatus.Proposed
                : ClassifyTypeStatus.LowConfidence;
        if (status == ClassifyTypeStatus.Proposed
            && string.Equals(
                type.CurrentCode?.Trim(), best.Id.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            status = ClassifyTypeStatus.AlreadySet;
        }

        var result = new ClassifyTypeResult(
            type, status, best.Id, System.Math.Round(score, 4), candidates)
        {
            Reason = status == ClassifyTypeStatus.NoConfidence
                ? "chosen without a confidence"
                : null
        };
        results.Add(result);
        if (status == ClassifyTypeStatus.AlreadySet)
            alreadySet.Add(result);
    }

    private static void AddReasonWarnings(RunState run, IReadOnlyList<ClassifyFamilyGroup> families)
    {
        if (run.FamilyReasons.Count == 0)
            return;

        var keys = new HashSet<string>(run.FamilyReasons.Keys, StringComparer.Ordinal);
        var lines = families
            .Where(family => keys.Contains(family.Key))
            .Select(family => "'" + family.FamilyName + "' - " + run.FamilyReasons[family.Key])
            .ToList();
        if (lines.Count == 0)
            return;

        run.Warnings.Add(
            "No code was reached for " + lines.Count + " famil" +
            (lines.Count == 1 ? "y" : "ies") + ": " + string.Join("; ", lines.Take(10)) +
            (lines.Count > 10 ? " …" : string.Empty) +
            ". [Action: show these families to the user and let them pick the codes]");
    }

    private static IReadOnlyList<ClassifyAssignmentGroup> AssignmentsOf(
        List<ClassifyTypeResult> proposed,
        AgentClassificationTable table) =>
        proposed
            .Where(result => result.Code is not null)
            .GroupBy(result => new { Family = result.Type.FamilyName, Code = result.Code! })
            .Select(group =>
            {
                var items = group
                    .OrderBy(item => item.Type.TypeName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
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

    private static IReadOnlyList<IReadOnlyList<ClassifySetParameterItem>> BatchesOf(
        List<ClassifyTypeResult> proposed)
    {
        var items = proposed
            .Where(result => result.Code is not null)
            .Select(result => new ClassifySetParameterItem(
                result.Type.ElementId, AssemblyCodeParameter, result.Code!, "type"))
            .ToArray();
        var batches = new List<IReadOnlyList<ClassifySetParameterItem>>();
        for (var start = 0; start < items.Length; start += SetParameterBatchSize)
            batches.Add(items.Skip(start).Take(SetParameterBatchSize).ToArray());
        return batches;
    }

    private static ClassifyMatchResult EveryType(
        IReadOnlyList<ClassifyFamilyGroup> families,
        ClassifyTypeStatus status,
        RunState run)
    {
        AddReasonWarnings(run, families);
        return new ClassifyMatchResult
        {
            Types = families
                .SelectMany(family => family.Types)
                .Select(type => new ClassifyTypeResult(
                    type, status, null, 0, Array.Empty<ClassifyCodeCandidate>())
                {
                    Reason = run.FamilyReasons.TryGetValue(FamilyKeyOf(families, type), out var reason)
                        ? reason
                        : null
                })
                .ToArray(),
            Incomplete = run.NotProcessed.Count > 0,
            NotProcessed = run.NotProcessed.ToArray(),
            Warnings = run.Warnings.ToArray(),
            DecisionUsed = run.AnswersReceived,
            DecisionCalls = run.DecisionCalls,
            Error = run.FirstError,
            ErrorDetail = run.FirstErrorDetail
        };
    }

    private static string FamilyKeyOf(
        IReadOnlyList<ClassifyFamilyGroup> families,
        ClassifyTypeSnapshot type) =>
        families.FirstOrDefault(family => family.Types.Contains(type))?.Key ?? string.Empty;

    private static ClassifyMatchResult Failed(SemanticRankOutcome outcome, RunState run) =>
        Failed(
            outcome.Error ?? "decision_call_failed",
            outcome.ErrorDetail,
            run);

    private static ClassifyMatchResult Failed(string error, string? detail, RunState run)
    {
        run.Warnings.Add(
            "The decision layer failed (" + error +
            (string.IsNullOrEmpty(detail) ? string.Empty : ": " + detail) + "). " +
            "[Action: " + DecisionHint + "]");
        return new ClassifyMatchResult
        {
            Error = error,
            ErrorDetail = detail,
            Incomplete = true,
            NotProcessed = run.NotProcessed.ToArray(),
            Warnings = run.Warnings.ToArray(),
            DecisionUsed = run.AnswersReceived,
            DecisionCalls = run.DecisionCalls
        };
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

    private static string StateOf(ClassifyOptions options, string task) =>
        AppendInstructions(
            task +
            " Options are rows of the classification table loaded in the document, " +
            "formatted 'breadcrumb (code)'." + PickInstructions,
            options.Instructions);

    private static string AppendInstructions(string state, string? instructions)
    {
        if (string.IsNullOrWhiteSpace(instructions))
            return state;

        var trimmed = instructions.Trim();
        if (trimmed.Length > MaxInstructionsLength)
            trimmed = trimmed.Substring(0, MaxInstructionsLength);
        return state + "\nSelection context:\n" + trimmed;
    }
}
