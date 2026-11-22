using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

public sealed class AgentClassificationMatcherTests
{
    private static AgentClassificationEntry Row(
        string code, string description, int level, string? parent) =>
        new() { Code = code, Description = description, Level = level, ParentCode = parent };

    private static AgentClassificationTable ServicesTable() =>
        AgentClassificationTable.Build(new[]
        {
            Row("10", "Shell", 1, null),
            Row("10.10", "Walls", 2, "10"),
            Row("10.10.10", "Load-bearing walls", 3, "10.10"),
            Row("10.10.20", "Partitions", 3, "10.10"),
            Row("20", "Services", 1, null),
            Row("20.10", "Pipelines", 2, "20"),
            Row("20.10.10", "Pipes", 3, "20.10"),
            Row("20.20", "Valves", 2, "20"),
            Row("20.20.10", "Ball valves", 3, "20.20"),
            Row("20.20.20", "Gate valves", 3, "20.20"),
            Row("30", "Other", 1, null)
        }, null);

    private static AgentClassificationTable WallTable() =>
        AgentClassificationTable.Build(new[]
        {
            Row("10", "Shell", 1, null),
            Row("10.10", "Walls", 2, "10"),
            Row("10.10.10", "Bearing walls", 3, "10.10"),
            Row("10.10.20", "Partition walls", 3, "10.10"),
            Row("10.10.30", "Curtain walls", 3, "10.10"),
            Row("10.10.40", "Shear walls", 3, "10.10"),
            Row("10.10.50", "Retaining walls", 3, "10.10")
        }, null);

    private static ClassifyTypeSnapshot Type(
        long id,
        string family,
        string typeName,
        string category,
        string? current = null,
        params string[] values) =>
        new(id, category, family, null, typeName, current, values);

    [Fact]
    public void Tokenize_LowerCasesFoldsYoAndSplits()
    {
        var tokens = AgentSemanticLexicon.Tokenize("Ёлки-Палки, Стены 10.20 (несущие)");

        Assert.Equal(new[] { "елки", "палки", "стены", "10", "20", "несущие" }, tokens);
    }

    [Fact]
    public void TokensMatch_RequiresEqualityOrLongPrefix()
    {
        Assert.True(AgentSemanticLexicon.TokensMatch("valve", "valves"));
        Assert.True(AgentSemanticLexicon.TokensMatch("стены", "стены"));
        Assert.False(AgentSemanticLexicon.TokensMatch("wall", "walled"));
        Assert.False(AgentSemanticLexicon.TokensMatch("abc", "abcd"));
        Assert.False(AgentSemanticLexicon.TokensMatch("pipe", "valve"));
    }

    [Fact]
    public void LexicalTop_WeightsFamilyNameTwiceAndKeepsCodeOrder()
    {
        var table = ServicesTable();
        var leaves = table.Entries.Where(entry => entry.IsLeaf).ToArray();
        var leafTokens = leaves.ToDictionary(
            leaf => leaf.Code,
            leaf => new HashSet<string>(AgentSemanticLexicon.Tokenize(leaf.Breadcrumb)),
            StringComparer.Ordinal);

        var scored = AgentSemanticLexicon.LexicalTop(
            new[] { "ball", "valve", "family" },
            new[] { "gate" },
            leaves,
            leafTokens,
            2);

        // ball + valve (name, weight 2 each) + gate (weight 1) -> Ball valves
        // first (4), then Gate valves (3: valve 2 + gate 1).
        Assert.Equal(new[] { "20.20.10", "20.20.20" },
            scored.Select(leaf => leaf.Code).ToArray());
    }

    [Fact]
    public async Task Match_StrongFamily_GoesStraightToStagesAndProposes()
    {
        var client = new KeywordDecisionClient();
        var matcher = new AgentClassificationMatcher(new AgentSemanticRanker(client));
        var types = new List<ClassifyTypeSnapshot>
        {
            Type(1, "Walls family", "Wall type A", "Стены"),
            Type(2, "Walls family", "Wall type B", "Стены")
        };

        var result = await matcher.MatchAsync(
            AgentClassificationTexts.GroupTypes(types), WallTable(),
            new ClassifyOptions(), CancellationToken.None);

        Assert.Null(result.Error);
        Assert.False(result.Incomplete);
        Assert.All(result.Types, item => Assert.Equal(ClassifyTypeStatus.Proposed, item.Status));
        var assignment = Assert.Single(result.Assignments);
        Assert.Equal("Walls family", assignment.Family);
        Assert.Equal("10.10.10", assignment.Code);
        Assert.Equal("Shell › Walls › Bearing walls", assignment.Breadcrumb);
        Assert.Equal(2, assignment.TypeIdCount);
        Assert.Equal(new long[] { 1, 2 }, assignment.TypeIds);

        var batch = Assert.Single(result.SetParameterBatches);
        Assert.Equal(2, batch.Count);
        Assert.All(batch, item =>
        {
            Assert.Equal("UNIFORMAT_CODE", item.Parameter);
            Assert.Equal("10.10.10", item.Value);
            Assert.Equal("type", item.Scope);
        });
    }

    [Fact]
    public async Task Match_WeakFamily_AsksTheBranchQuestionFirst()
    {
        var client = new KeywordDecisionClient();
        var matcher = new AgentClassificationMatcher(new AgentSemanticRanker(client));
        var types = new List<ClassifyTypeSnapshot>
        {
            Type(5, "Ball valve family", "Ball valve DN100", "Services equipment")
        };

        var result = await matcher.MatchAsync(
            AgentClassificationTexts.GroupTypes(types), ServicesTable(),
            new ClassifyOptions(), CancellationToken.None);

        Assert.Null(result.Error);
        var proposed = Assert.Single(result.Types);
        Assert.Equal(ClassifyTypeStatus.Proposed, proposed.Status);
        Assert.Equal("20.20.10", proposed.Code);

        // The first call carried the three top-level branches as options.
        var branchCall = client.Requests[0];
        var branchQuestion = Assert.Single(branchCall.Values);
        Assert.Equal(new[] { "10", "20", "30" }, branchQuestion.Criteria!.Keys.OrderBy(key => key));
    }

    [Fact]
    public async Task Match_LowScore_LandsInUncertainWithoutSetItems()
    {
        var client = new KeywordDecisionClient { Score = 0.3 };
        var matcher = new AgentClassificationMatcher(new AgentSemanticRanker(client));
        var types = new List<ClassifyTypeSnapshot> { Type(1, "Walls family", "Wall type A", "Стены") };

        var result = await matcher.MatchAsync(
            AgentClassificationTexts.GroupTypes(types), WallTable(),
            new ClassifyOptions(), CancellationToken.None);

        var uncertain = Assert.Single(result.Types);
        Assert.Equal(ClassifyTypeStatus.LowConfidence, uncertain.Status);
        Assert.Equal("10.10.10", uncertain.Code);
        Assert.NotEmpty(uncertain.Candidates);
        Assert.Empty(result.SetParameterBatches);
        Assert.Empty(result.Assignments);
    }

    [Fact]
    public async Task Match_CurrentCodeEqualsProposal_IsAlreadySet()
    {
        var client = new KeywordDecisionClient();
        var matcher = new AgentClassificationMatcher(new AgentSemanticRanker(client));
        var types = new List<ClassifyTypeSnapshot>
        {
            Type(1, "Walls family", "Wall type A", "Стены", current: "10.10.10")
        };

        var result = await matcher.MatchAsync(
            AgentClassificationTexts.GroupTypes(types), WallTable(),
            new ClassifyOptions(), CancellationToken.None);

        var item = Assert.Single(result.Types);
        Assert.Equal(ClassifyTypeStatus.AlreadySet, item.Status);
        Assert.Empty(result.SetParameterBatches);
    }

    [Fact]
    public async Task Match_TwoFamilies_GroupsAssignmentsAndBatchesItems()
    {
        var client = new KeywordDecisionClient();
        var matcher = new AgentClassificationMatcher(new AgentSemanticRanker(client));
        var types = new List<ClassifyTypeSnapshot>
        {
            Type(1, "Valves family", "Gate type", "Services equipment"),
            Type(2, "Walls family", "Wall type A", "Стены"),
            Type(3, "Walls family", "Wall type B", "Стены")
        };

        var result = await matcher.MatchAsync(
            AgentClassificationTexts.GroupTypes(types), ServicesTable(),
            new ClassifyOptions(), CancellationToken.None);

        // Both families are lexically weak against this small table, so both
        // take the branch path; what this test pins is the grouping and the
        // batching shape: two assignment groups ordered by family name, one
        // set_parameters batch of three items.
        Assert.Null(result.Error);
        Assert.All(result.Types, item => Assert.Equal(ClassifyTypeStatus.Proposed, item.Status));
        Assert.Equal(2, result.Assignments.Count);
        Assert.Equal("Valves family", result.Assignments[0].Family);
        Assert.Equal("Walls family", result.Assignments[1].Family);
        var batch = Assert.Single(result.SetParameterBatches);
        Assert.Equal(3, batch.Count);
    }

    [Fact]
    public async Task Match_ManyProposedTypes_SplitsSetParametersIntoBatchesOf500()
    {
        var client = new KeywordDecisionClient();
        var matcher = new AgentClassificationMatcher(new AgentSemanticRanker(client));
        var types = Enumerable.Range(1, 501)
            .Select(index => Type(index, "Walls family", "Walls type x" + index, "Стены", "vx" + index))
            .Cast<ClassifyTypeSnapshot>()
            .ToList();

        var result = await matcher.MatchAsync(
            AgentClassificationTexts.GroupTypes(types), WallTable(),
            new ClassifyOptions(), CancellationToken.None);

        Assert.All(result.Types, item => Assert.Equal(ClassifyTypeStatus.Proposed, item.Status));
        Assert.Equal(2, result.SetParameterBatches.Count);
        Assert.Equal(500, result.SetParameterBatches[0].Count);
        Assert.Single(result.SetParameterBatches[1]);
        var group = Assert.Single(result.Assignments);
        Assert.Equal(501, group.TypeIdCount);
        Assert.Equal(50, group.TypeIds.Count);
        Assert.Equal(5, group.TypeNames.Count);
    }

    [Fact]
    public async Task Match_RefusedFamily_IsReportedNotProcessed()
    {
        var client = new KeywordDecisionClient
        {
            FailWhen = (questionId, instructions) => instructions.Contains("Match: ")
                && instructions.Contains("Ball valve")
        };
        var matcher = new AgentClassificationMatcher(new AgentSemanticRanker(client));
        var types = new List<ClassifyTypeSnapshot>
        {
            Type(1, "Walls family", "Wall type A", "Стены"),
            Type(2, "Ball valve family", "Ball valve DN100", "Services equipment")
        };

        var result = await matcher.MatchAsync(
            AgentClassificationTexts.GroupTypes(types), ServicesTable(),
            new ClassifyOptions(), CancellationToken.None);

        Assert.True(result.Incomplete);
        Assert.Contains("Ball valve family", result.NotProcessed);
        Assert.Contains(result.Warnings, warning => warning.Contains("[Action:"));
        var wallItem = result.Types.Single(item => item.Type.ElementId == 1);
        Assert.Equal(ClassifyTypeStatus.Proposed, wallItem.Status);
        var refusedType = result.Types.Single(item => item.Type.ElementId == 2);
        Assert.Equal(ClassifyTypeStatus.NoCandidates, refusedType.Status);
    }

    [Fact]
    public async Task Match_DisabledClient_ReturnsDecisionUnavailable()
    {
        var client = new KeywordDecisionClient { Enabled = false };
        var matcher = new AgentClassificationMatcher(new AgentSemanticRanker(client));
        var types = new List<ClassifyTypeSnapshot> { Type(1, "Walls family", "Wall type A", "Стены") };

        var result = await matcher.MatchAsync(
            AgentClassificationTexts.GroupTypes(types), WallTable(),
            new ClassifyOptions(), CancellationToken.None);

        Assert.Equal("decision_unavailable", result.Error);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public void GroupTypes_ComposesFamilyTextAndTypeQueries()
    {
        var types = new List<ClassifyTypeSnapshot>
        {
            Type(1, "Family", "Type A", "Cat", null, "common", "only-a"),
            Type(2, "Family", "Type B", "Cat", null, "common")
        };

        var groups = AgentClassificationTexts.GroupTypes(types);

        var group = Assert.Single(groups);
        Assert.Equal(2, group.Types.Count);
        Assert.StartsWith("Cat; Family", group.FamilyText);
        Assert.Contains("common", group.FamilyText);
        Assert.Contains("only-a", group.FamilyText);
        // Multi-type families: the query keeps the family and type name plus the
        // DISTINCTIVE values only; a single-type family would keep everything.
        Assert.Contains("Type A", group.TypeQueries[0]);
        Assert.Contains("only-a", group.TypeQueries[0]);
        Assert.DoesNotContain("common", group.TypeQueries[0]);
        Assert.Equal("Family; Type B", group.TypeQueries[1]);
    }

    // ── D1: a failed decision channel is never dressed up as no_candidates ──

    /// <summary>
    /// Two Russian families against an English table: zero lexical overlap, so
    /// the whole run depends on the decision channel - the D1 probe scenario.
    /// </summary>
    private static List<ClassifyTypeSnapshot> RussianFamilies() => new()
    {
        Type(1, "Вентиль шаровый", "Вентиль Ду15", "Арматура"),
        Type(2, "Труба стальная", "Труба Ду25", "Трубопроводы")
    };

    [Fact]
    public async Task Match_AllWeak_ChannelException_IsTypedFailureNotNoCandidates()
    {
        var client = new KeywordDecisionClient
        {
            Throw = new InvalidOperationException("HTTP 503")
        };
        var matcher = new AgentClassificationMatcher(new AgentSemanticRanker(client));

        var result = await matcher.MatchAsync(
            AgentClassificationTexts.GroupTypes(RussianFamilies()), ServicesTable(),
            new ClassifyOptions(), CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Equal("decision_call_failed", result.Error);
        Assert.Contains("503", result.ErrorDetail);
    }

    [Fact]
    public async Task Match_AllWeak_EmptyAnswers_IsTypedFailureNotNoCandidates()
    {
        var client = new KeywordDecisionClient { EmptyAnswers = true };
        var matcher = new AgentClassificationMatcher(new AgentSemanticRanker(client));

        var result = await matcher.MatchAsync(
            AgentClassificationTexts.GroupTypes(RussianFamilies()), ServicesTable(),
            new ClassifyOptions(), CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.NotEmpty(result.NotProcessed);
    }

    [Fact]
    public async Task Match_AllWeak_Refusal_IsTypedFailureNotNoCandidates()
    {
        var client = new KeywordDecisionClient
        {
            FailWhen = (_, _) => true
        };
        var matcher = new AgentClassificationMatcher(new AgentSemanticRanker(client));

        var result = await matcher.MatchAsync(
            AgentClassificationTexts.GroupTypes(RussianFamilies()), ServicesTable(),
            new ClassifyOptions(), CancellationToken.None);

        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Match_TypeStageFullyFailed_KeepsTypedError()
    {
        var calls = 0;
        var client = new KeywordDecisionClient
        {
            BeforeAsk = () =>
            {
                calls++;
                if (calls >= 2)
                    throw new InvalidOperationException("flaky type stage");
            }
        };
        var matcher = new AgentClassificationMatcher(new AgentSemanticRanker(client));
        var types = new List<ClassifyTypeSnapshot> { Type(1, "Walls family", "Walls type A", "Стены") };

        var result = await matcher.MatchAsync(
            AgentClassificationTexts.GroupTypes(types), WallTable(),
            new ClassifyOptions(), CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Equal("decision_call_failed", result.Error);
    }

    /// <summary>
    /// Deterministic stand-in for the decision model: every option is scored by
    /// the tokens it shares with the question's Match line (equal or ≥5-char
    /// prefix), the best option gets <see cref="Score"/>, the runner-ups less.
    /// </summary>
    private sealed class KeywordDecisionClient : IAgentDecisionClient
    {
        public double Score { get; set; } = 0.9;

        public bool Enabled { get; set; } = true;

        public Func<string, string, bool>? FailWhen { get; set; }

        /// <summary>Transport failure of every call (D1 probe: HTTP 503).</summary>
        public Exception? Throw { get; set; }

        /// <summary>Every call returns an EMPTY answer dictionary (D1 probe).</summary>
        public bool EmptyAnswers { get; set; }

        public Action? BeforeAsk { get; set; }

        public List<IReadOnlyDictionary<string, AgentDecisionQuestion>> Requests { get; } = new();

        public string ProviderName => "test";

        public string Model => "test-model";

        public bool IsEnabled => Enabled;

        public Task<IReadOnlyDictionary<string, AgentDecisionAnswer>> AskAsync(
            string state,
            IReadOnlyDictionary<string, AgentDecisionQuestion> questions,
            CancellationToken ct)
        {
            BeforeAsk?.Invoke();
            if (Throw is not null)
                throw Throw;

            Requests.Add(questions);
            if (EmptyAnswers)
            {
                return Task.FromResult<IReadOnlyDictionary<string, AgentDecisionAnswer>>(
                    new Dictionary<string, AgentDecisionAnswer>(StringComparer.Ordinal));
            }

            var answers = new Dictionary<string, AgentDecisionAnswer>(StringComparer.Ordinal);
            foreach (var pair in questions)
            {
                if (FailWhen is not null && FailWhen(pair.Key, pair.Value.Instructions))
                {
                    answers[pair.Key] = new AgentDecisionAnswer { Type = "choice", Error = "refused" };
                    continue;
                }

                answers[pair.Key] = Answer(pair.Value);
            }

            return Task.FromResult<IReadOnlyDictionary<string, AgentDecisionAnswer>>(answers);
        }

        private AgentDecisionAnswer Answer(AgentDecisionQuestion question)
        {
            var marker = question.Instructions.IndexOf("Match: ", StringComparison.Ordinal);
            var query = marker >= 0
                ? question.Instructions.Substring(marker + "Match: ".Length)
                : question.Instructions;
            var queryTokens = AgentSemanticLexicon.Tokenize(query);

            var ranked = question.Criteria!
                .Select(pair => (
                    Id: pair.Key,
                    Overlap: AgentSemanticLexicon.Tokenize(pair.Value ?? string.Empty)
                        .Count(token => queryTokens.Any(q => AgentSemanticLexicon.TokensMatch(q, token)))))
                .OrderByDescending(pair => pair.Overlap)
                .ThenBy(pair => pair.Id, StringComparer.Ordinal)
                .ToList();

            var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
            var rank = 0;
            foreach (var pair in ranked.Where(pair => pair.Overlap > 0).Take(5))
            {
                probabilities[pair.Id] = System.Math.Max(0.05, Score - rank * 0.1);
                rank++;
            }

            if (probabilities.Count == 0)
            {
                // Nothing overlaps: the first option by code order wins with a
                // low-ish score, like a model guessing.
                probabilities[ranked[0].Id] = Score;
            }

            return new AgentDecisionAnswer
            {
                Type = "choice",
                Probabilities = probabilities
            };
        }
    }
}
