using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Agent;

/// <summary>
/// The weak-family half of <see cref="AgentClassificationMatcher"/>: the branch
/// question, the lexical narrowing under the chosen branches and, when the
/// lexical well is dry, the level-by-level semantic descent. The options of
/// every descent level are the CHILDREN of the previously chosen row; the first
/// leaves of the table in code order are never offered as candidates.
/// </summary>
public sealed partial class AgentClassificationMatcher
{
    /// <summary>Id of the abstain option of every descent level.</summary>
    private const string AbstainId = "__none__";

    private const string AbstainText = "None of these fits the element";

    private async Task<SemanticRankOutcome?> ResolveWeakFamilies(
        IReadOnlyList<ClassifyFamilyGroup> weak,
        AgentClassificationTable table,
        IReadOnlyList<AgentClassificationEntry> leaves,
        Dictionary<string, HashSet<string>> leafTokens,
        Dictionary<string, IReadOnlyList<AgentClassificationEntry>> candidates,
        Dictionary<string, double> chainScores,
        List<(ClassifyFamilyGroup Family, IReadOnlyList<SemanticMatch> Codes)> precomputed,
        RunState run,
        ClassifyOptions options,
        CancellationToken ct)
    {
        var roots = table.Entries.Where(entry => entry.Level == 1).ToArray();
        if (roots.Length == 0)
        {
            // No branches to walk and nothing to ask about: the model is not
            // called, the reason is stated plainly.
            run.Warnings.Add(
                "The classification table has no top-level rows, so its branches cannot be " +
                "walked. [Action: load a table with top-level rows via " +
                "load_classification_table or pick the codes manually with " +
                "list_classification_entries]");
            foreach (var family in weak)
            {
                run.FamilyReasons[family.Key] = "the classification table has no top-level rows";
                var scored = AgentSemanticLexicon.LexicalTop(
                    family.FamilyNameTokens, Array.Empty<string>(),
                    leaves, leafTokens, LexicalTopCount);
                if (scored.Count > 0)
                {
                    candidates[family.Key] = scored;
                    run.FamilyReasons.Remove(family.Key);
                }
            }

            return null;
        }

        var children = ChildrenOf(table);
        var outcome = await _ranker.RankQueriesAsync(
            StateOf(options,
                "Task: pick the top-level branch of the classification table the building " +
                "element described in each question belongs to. Options are top-level rows " +
                "of the table."),
            weak.Select(family => (
                family.Key,
                (IReadOnlyList<SemanticCandidate>)roots
                    .Select(root => new SemanticCandidate(root.Code, CandidateText(root)))
                    .ToArray(),
                family.FamilyText)).ToList(),
            BranchTop,
            ct).ConfigureAwait(false);
        run.Observe(outcome);
        if (outcome.Error == "decision_unavailable")
        {
            // The channel is unusable: every weak family stays unanswered.
            foreach (var family in weak)
            {
                run.MarkNotProcessed(family.FamilyName);
                run.FamilyReasons[family.Key] = "the decision layer did not answer the branch question";
            }

            return outcome;
        }

        var byRoot = LeavesByRoot(table, leaves);
        var descending = new List<DescentState>();
        for (var index = 0; index < weak.Count; index++)
        {
            var family = weak[index];
            if (outcome.IncompleteQueryIds.Contains(family.Key))
            {
                run.MarkNotProcessed(family.FamilyName);
                run.FamilyReasons[family.Key] = "the decision layer did not answer the branch question";
                continue;
            }

            var top = outcome.Matches[index].Count > 0 ? outcome.Matches[index][0] : null;
            var root = top is null ? null : table.Find(top.Id);
            if (root is null)
            {
                run.FamilyReasons[family.Key] = "the model returned no branch of the table";
                continue;
            }

            // Lexical hits under the chosen branches still win: they are real
            // matches, not guesses. Only a dry lexical well starts the descent.
            var chosenRoots = new HashSet<string>(
                outcome.Matches[index].Select(match => match.Id),
                StringComparer.OrdinalIgnoreCase);
            var underRoots = byRoot
                .Where(pair => chosenRoots.Contains(pair.Key))
                .SelectMany(pair => pair.Value)
                .ToArray();
            var scored = underRoots.Length == 0
                ? Array.Empty<AgentClassificationEntry>()
                : AgentSemanticLexicon.LexicalTop(
                    family.FamilyNameTokens,
                    AgentSemanticLexicon.Tokenize(family.FamilyText)
                        .Where(token => !family.FamilyNameTokens.Contains(token))
                        .ToArray(),
                    underRoots, leafTokens, LexicalHardCap);
            if (scored.Count > 0)
            {
                candidates[family.Key] = scored;
                continue;
            }

            var state = new DescentState
            {
                Family = family,
                Node = root,
                ChainScore = System.Math.Round(top!.Score, 4),
                BareChain = top.BareChoice,
                Level = 1
            };
            if (TryFinishDescent(state, children, candidates, chainScores, precomputed))
                continue;

            descending.Add(state);
        }

        await DescendAsync(descending, table, children, candidates, chainScores, precomputed, run, options, ct)
            .ConfigureAwait(false);
        return null;
    }

    private async Task DescendAsync(
        List<DescentState> descending,
        AgentClassificationTable table,
        Dictionary<string, List<AgentClassificationEntry>> children,
        Dictionary<string, IReadOnlyList<AgentClassificationEntry>> candidates,
        Dictionary<string, double> chainScores,
        List<(ClassifyFamilyGroup Family, IReadOnlyList<SemanticMatch> Codes)> precomputed,
        RunState run,
        ClassifyOptions options,
        CancellationToken ct)
    {
        while (descending.Count > 0)
        {
            var round = new List<DescentState>();
            var queries = new List<(string, IReadOnlyList<SemanticCandidate>, string)>();
            foreach (var state in descending)
            {
                if (state.Level >= AgentClassificationFile.MaxLevel)
                {
                    run.FamilyReasons[state.Family.Key] =
                        "the descent did not reach a leaf within " + AgentClassificationFile.MaxLevel +
                        " levels of the table";
                    continue;
                }

                if (!children.TryGetValue(state.Node.Code, out var kids) || kids.Count == 0)
                {
                    TryFinishDescent(state, children, candidates, chainScores, precomputed);
                    continue;
                }

                var descentOptions = new List<SemanticCandidate>(kids.Count + 1);
                foreach (var kid in kids)
                    descentOptions.Add(new SemanticCandidate(kid.Code, CandidateText(kid)));
                descentOptions.Add(new SemanticCandidate(AbstainId, AbstainText));
                queries.Add((state.Family.Key, descentOptions, state.Family.FamilyText));
                round.Add(state);
            }

            if (round.Count == 0)
                break;

            var outcome = await _ranker.RankQueriesAsync(
                DescentStateOf(options), queries, 1, ct).ConfigureAwait(false);
            run.Observe(outcome);
            if (outcome.Error == "decision_unavailable")
            {
                foreach (var state in round)
                {
                    run.MarkNotProcessed(state.Family.FamilyName);
                    run.FamilyReasons[state.Family.Key] = "the decision layer became unavailable during the descent";
                }

                descending.Clear();
                break;
            }

            var next = new List<DescentState>();
            for (var index = 0; index < round.Count; index++)
            {
                var state = round[index];
                if (outcome.IncompleteQueryIds.Contains(state.Family.Key))
                {
                    run.MarkNotProcessed(state.Family.FamilyName);
                    run.FamilyReasons[state.Family.Key] =
                        "the decision layer did not answer level " + (state.Level + 1) + " of the descent";
                    continue;
                }

                var top = outcome.Matches[index].Count > 0 ? outcome.Matches[index][0] : null;
                if (top is null)
                {
                    run.FamilyReasons[state.Family.Key] =
                        "the model returned no option at level " + (state.Level + 1) + " of the descent";
                    continue;
                }

                if (string.Equals(top.Id, AbstainId, StringComparison.Ordinal))
                {
                    run.FamilyReasons[state.Family.Key] =
                        "the model answered that no option under the chosen branch fits the family";
                    continue;
                }

                var child = table.Find(top.Id);
                if (child is null)
                {
                    run.FamilyReasons[state.Family.Key] =
                        "the chosen option '" + top.Id + "' is not a row of the table";
                    continue;
                }

                state.Node = child;
                state.ChainScore = System.Math.Min(state.ChainScore, System.Math.Round(top.Score, 4));
                state.BareChain |= top.BareChoice;
                state.Level++;
                if (!TryFinishDescent(state, children, candidates, chainScores, precomputed))
                    next.Add(state);
            }

            descending = next;
        }
    }

    private static bool TryFinishDescent(
        DescentState state,
        Dictionary<string, List<AgentClassificationEntry>> children,
        Dictionary<string, IReadOnlyList<AgentClassificationEntry>> candidates,
        Dictionary<string, double> chainScores,
        List<(ClassifyFamilyGroup Family, IReadOnlyList<SemanticMatch> Codes)> precomputed)
    {
        if (!state.Node.IsLeaf && children.ContainsKey(state.Node.Code))
            return false;

        // The chain is as weak as its weakest link: the family score is the
        // minimum over the branch and every descent level.
        candidates[state.Family.Key] = new[] { state.Node };
        chainScores[state.Family.Key] = state.ChainScore;
        precomputed.Add((state.Family,
            new[] { new SemanticMatch(state.Node.Code, state.ChainScore, 1) { BareChoice = state.BareChain } }));
        return true;
    }

    private static Dictionary<string, List<AgentClassificationEntry>> ChildrenOf(
        AgentClassificationTable table)
    {
        var children = new Dictionary<string, List<AgentClassificationEntry>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var entry in table.Entries)
        {
            if (entry.ParentCode is null)
                continue;

            if (!children.TryGetValue(entry.ParentCode, out var bucket))
                children[entry.ParentCode] = bucket = new List<AgentClassificationEntry>();
            bucket.Add(entry);
        }

        return children;
    }

    private static Dictionary<string, List<AgentClassificationEntry>> LeavesByRoot(
        AgentClassificationTable table,
        IReadOnlyList<AgentClassificationEntry> leaves)
    {
        var byRoot = new Dictionary<string, List<AgentClassificationEntry>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var leaf in leaves)
        {
            var root = RootOf(table, leaf);
            if (root is null)
                continue;

            if (!byRoot.TryGetValue(root, out var bucket))
                byRoot[root] = bucket = new List<AgentClassificationEntry>();
            bucket.Add(leaf);
        }

        return byRoot;
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

    /// <summary>
    /// The descent question allows an honest none: the instructions never
    /// demand "choose the closest anyway".
    /// </summary>
    private static string DescentStateOf(ClassifyOptions options)
    {
        var state =
            "Task: descend the classification table for the building element described in " +
            "each question. The options are the CHILDREN of the row chosen on the previous " +
            "level - the code and the full path of each child. Pick the child row the " +
            "element belongs to. Choose the option '" + AbstainText + "' when no option " +
            "fits: an honest none is better than a wrong code.";
        return AppendInstructions(state, options.Instructions);
    }

    private sealed class DescentState
    {
        public required ClassifyFamilyGroup Family { get; init; }

        public required AgentClassificationEntry Node { get; set; }

        /// <summary>Minimum score over the branch question and every level so far.</summary>
        public double ChainScore { get; set; }

        /// <summary>True when the branch or any level was a bare choice without a confidence.</summary>
        public bool BareChain { get; set; }

        public int Level { get; set; }
    }
}
