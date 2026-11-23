using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// The per-type differentiation stage (C5-04): a family that ended with ONE
/// code gets its types asked apart when they differ by more than size - one
/// question per significant token set, the options being the family code, its
/// sibling leaves and an honest none. A confident sibling earns its own code;
/// whatever else happens keeps the family code. The status honesty and the
/// soft failures live in the Honesty partial.
/// </summary>
public sealed partial class AgentClassificationMatcherTypeStageTests
{
    private const string AbstainId = "__none__";
    private const string FamilyCode = "10.10.10";
    private const string Sibling = "10.10.20";
    private const string ThirdLeaf = "10.10.30";

    private static AgentClassificationEntry Row(
        string code, string description, int level, string? parent) =>
        new() { Code = code, Description = description, Level = level, ParentCode = parent };

    /// <summary>
    /// «10 Shell» → «10.10 Walls» → «10.10.10 Load-bearing walls», «10.10.20
    /// Partitions», «10.10.30 Curtain walls»: a family without a single lexical
    /// hit walks down (by code order) to 10.10.10 and finds two siblings under
    /// the same parent.
    /// </summary>
    private static AgentClassificationTable WallTable(params string[] walls) =>
        AgentClassificationTable.Build(new[]
        {
            Row("10", "Shell", 1, null),
            Row("10.10", "Walls", 2, "10"),
            Row(FamilyCode, walls.Length > 0 ? walls[0] : "Load-bearing walls", 3, "10.10"),
            Row(Sibling, walls.Length > 1 ? walls[1] : "Partitions", 3, "10.10"),
            Row(ThirdLeaf, walls.Length > 2 ? walls[2] : "Curtain walls", 3, "10.10"),
            Row("20", "Services", 1, null),
            Row("20.10", "Pipelines", 2, "20"),
            Row("20.10.10", "Pipes", 3, "20.10"),
            Row("30", "Other", 1, null)
        }, null);

    /// <summary>
    /// A type of the family «Семейство Х» of «Категория»: its text shares no
    /// token with the English rows, so the family is weak by construction
    /// whatever the type names are (the family text never carries them).
    /// </summary>
    private static ClassifyTypeSnapshot Type(
        long id, string typeName, string? current = null, params string[] values) =>
        new(id, "Категория", "Семейство Х", null, typeName, current, values);

    private static ClassifyTypeSnapshot WallType(long id, string family, string typeName) =>
        new(id, "Категория", family, null, typeName, null, Array.Empty<string>());

    private static Task<ClassifyMatchResult> MatchAsync(
        Script client,
        IReadOnlyList<ClassifyTypeSnapshot> types,
        AgentClassificationTable? table = null,
        double minScore = 0.5) =>
        new AgentClassificationMatcher(new AgentSemanticRanker(client)).MatchAsync(
            AgentClassificationTexts.GroupTypes(types), table ?? WallTable(),
            new ClassifyOptions(MinScore: minScore), CancellationToken.None);

    private static ClassifyTypeResult ResultOf(ClassifyMatchResult result, long id) =>
        result.Types.Single(item => item.Type.ElementId == id);

    private static int DescentCalls(Script client) =>
        client.States.Count(state => state.StartsWith("Task: descend the classification table", StringComparison.Ordinal));

    // ── The questions and where they come from ──────────────────────────

    [Fact]
    public async Task TypeStage_MixedAnswers_SplitOneFamilyIntoTwoAssignmentGroups()
    {
        var client = new Script { Mode = PerTypeMode.Mixed };

        var result = await MatchAsync(client, new[] { Type(1, "External"), Type(2, "Internal") });

        Assert.Null(result.Error);
        Assert.Equal(2, client.PerTypeQuestions);

        // The options were the family code, its two sibling leaves and the honest none.
        var questions = Assert.Single(client.PerTypeRequests).Values.ToList();
        Assert.All(questions, question => Assert.Equal(
            new[] { FamilyCode, Sibling, ThirdLeaf, AbstainId },
            question.Criteria!.Keys.OrderBy(key => key, StringComparer.Ordinal)));

        // The question describes the type inside its family: category, family, type name.
        Assert.Contains(questions, question => question.Instructions.Contains(
            "Match: Категория; Семейство Х; External", StringComparison.Ordinal));

        Assert.Equal(ClassifyTypeStatus.Proposed, ResultOf(result, 1).Status);
        Assert.Equal(FamilyCode, ResultOf(result, 1).Code);
        Assert.Equal(ClassifyTypeStatus.Proposed, ResultOf(result, 2).Status);
        Assert.Equal(Sibling, ResultOf(result, 2).Code);

        // One family, two codes: two assignment rows and per-type set items.
        Assert.Equal(2, result.Assignments.Count);
        Assert.All(result.Assignments, group => Assert.Equal("Семейство Х", group.Family));
        var items = Assert.Single(result.SetParameterBatches);
        Assert.Equal(FamilyCode, items.Single(item => item.ElementId == 1).Value);
        Assert.Equal(Sibling, items.Single(item => item.ElementId == 2).Value);
    }

    [Fact]
    public async Task TypeStage_WalksTheDescentPath_BeforeAskingPerType()
    {
        var client = new Script();

        var result = await MatchAsync(client, new[] { Type(1, "External"), Type(2, "Internal") });

        Assert.Null(result.Error);
        // Branch question, then the descent over two levels, then the per-type
        // questions - never a family question over shortlisted codes (that is
        // the lexical path).
        Assert.Equal(new[] { "branch", "descend", "descend", "per-type" }, client.States.Select(Kind));
        Assert.Equal(4, client.Calls);
        Assert.Equal(4, result.DecisionCalls);
    }

    [Fact]
    public async Task TypeStage_DescentIntoASingleLeafBranch_HasNoSiblingToAskAbout()
    {
        // «05 Frames» → «05.05 Skeleton» → «05.05.10 Columns»: the first root by
        // code order, so the walk lands on the one leaf of its parent.
        var table = AgentClassificationTable.Build(new[]
        {
            Row("05", "Frames", 1, null),
            Row("05.05", "Skeleton", 2, "05"),
            Row("05.05.10", "Columns", 3, "05.05"),
            Row("10", "Shell", 1, null),
            Row("10.10", "Walls", 2, "10"),
            Row(FamilyCode, "Load-bearing walls", 3, "10.10"),
            Row(Sibling, "Partitions", 3, "10.10")
        }, null);
        var client = new Script { Path = new[] { "05", "05.05", "05.05.10" }, Code = "05.05.10" };

        var result = await MatchAsync(client, new[] { Type(1, "Alpha"), Type(2, "Beta") }, table);

        Assert.Null(result.Error);
        Assert.Equal(new[] { "branch", "descend", "descend" }, client.States.Select(Kind));
        Assert.All(result.Types, item => Assert.Equal("05.05.10", item.Code));
        Assert.Equal(2, Assert.Single(result.Assignments).TypeIdCount);
    }

    [Fact]
    public async Task TypeStage_LexicalSingleSurvivor_IsAskedPerTypeToo()
    {
        // Only ONE leaf carries the family token: the family stays weak (one hit
        // is below the strong threshold), the shortlist under the chosen branch
        // holds that single survivor - a one-code family without any descent.
        var table = AgentClassificationTable.Build(new[]
        {
            Row("10", "Shell", 1, null),
            Row("10.10", "Enclosures", 2, "10"),
            Row(FamilyCode, "Walls", 3, "10.10"),
            Row(Sibling, "Partitions", 3, "10.10"),
            Row("20", "Services", 1, null),
            Row("20.10.10", "Pipes", 2, "20")
        }, null);
        var client = new Script { Mode = PerTypeMode.Sibling };

        var result = await MatchAsync(
            client, new[] { WallType(1, "Walls", "Alpha"), WallType(2, "Walls", "Beta") }, table);

        Assert.Null(result.Error);
        Assert.Equal(new[] { "branch", "assign", "per-type" }, client.States.Select(Kind));
        Assert.All(result.Types, item =>
        {
            Assert.Equal(ClassifyTypeStatus.Proposed, item.Status);
            Assert.Equal(Sibling, item.Code);
        });
    }

    [Fact]
    public async Task TypeStage_MultiCodeFamily_IsLeftToTheShortlistQuestions()
    {
        // Every leaf of the branch carries the family token: the family keeps
        // several codes, and its types are asked by the shortlist stage - the
        // differentiation stage never touches a multi-code family.
        var client = new Script { BothLeafCodes = true, Mode = PerTypeMode.Sibling };

        var result = await MatchAsync(
            client, new[] { WallType(1, "Walls", "Alpha"), WallType(2, "Walls", "Beta") });

        Assert.Null(result.Error);
        Assert.Equal(new[] { "branch", "assign", "assign" }, client.States.Select(Kind));
        Assert.All(result.Types, item => Assert.Equal(FamilyCode, item.Code));
    }

    // ── What counts as a difference ─────────────────────────────────────

    [Theory]
    [InlineData("Клапан PN16", "Клапан PN25")]
    [InlineData("Короб 100x100", "Короб 150x150")]
    [InlineData("Вентиль Ду15", "Вентиль Ду25")]
    [InlineData("Стена 200mm", "Стена 300mm")]
    [InlineData("Труба AISI 304", "Труба AISI 316")]
    [InlineData("Тип 1", "Тип 2")]
    [InlineData("Регулятор e3b0c442-98fc-1c14-9b1f-83a5f3d0a110", "Регулятор 7c9e6679-7425-40de-944b-e07fc1f90ae7")]
    public async Task TypeStage_SizeOrArticleNumberOnly_IsNotADifference(string first, string second)
    {
        var client = new Script();

        var result = await MatchAsync(client, new[] { Type(1, first), Type(2, second) });

        Assert.Null(result.Error);
        Assert.Equal(new[] { "branch", "descend", "descend" }, client.States.Select(Kind));
        Assert.All(result.Types, item =>
        {
            Assert.Equal(ClassifyTypeStatus.Proposed, item.Status);
            Assert.Equal(FamilyCode, item.Code);
        });
        Assert.Equal(2, Assert.Single(result.Assignments).TypeIdCount);
    }

    [Fact]
    public async Task TypeStage_SizeOnlyValueDifference_IsNotADifference()
    {
        var client = new Script();

        var result = await MatchAsync(
            client,
            new[] { Type(1, "Тип", null, "Длина 2000", "Масса 15"), Type(2, "Тип", null, "Длина 2500", "Масса 20") });

        Assert.Null(result.Error);
        Assert.Equal(new[] { "branch", "descend", "descend" }, client.States.Select(Kind));
        Assert.Equal(2, Assert.Single(result.Assignments).TypeIdCount);
    }

    [Fact]
    public async Task TypeStage_ValueDifferenceAlone_IsADifference()
    {
        // The names are the same; only the values not shared by both types tell
        // the types apart - and the answers tell the types apart too.
        var client = new Script();

        var result = await MatchAsync(
            client, new[] { Type(1, "Тип", null, "partition"), Type(2, "Тип", null, "curtain") });

        Assert.Null(result.Error);
        Assert.Equal(2, client.PerTypeQuestions);
        Assert.NotEqual(ResultOf(result, 1).Code, ResultOf(result, 2).Code);
    }

    [Fact]
    public async Task TypeStage_FifteenTypesThreeMeanings_ThreeQuestionsAnswerForAllOfThem()
    {
        var client = new Script();
        var types = Enumerable.Range(1, 15)
            .Select(index => Type(index, (index <= 5 ? "Chimney " : index <= 10 ? "Foundation " : "Generic ") + (15 * index)))
            .Cast<ClassifyTypeSnapshot>()
            .ToList();

        var result = await MatchAsync(
            client, types, WallTable("Generic walls", "Chimney walls", "Foundation walls"));

        Assert.Null(result.Error);
        Assert.Equal(3, client.PerTypeQuestions);

        // One question per meaning: the first type names the group, the rest is a count.
        var queries = client.PerTypeRequests
            .SelectMany(request => request.Values)
            .Select(question => question.Instructions)
            .ToList();
        Assert.Contains(queries, query => query.Contains(
            "Chimney 15; same for 4 more type(s) that differ only in size or article number", StringComparison.Ordinal));
        // ...and no further type of a group heads a question of its own.
        Assert.Equal(3, queries.Count);
        Assert.DoesNotContain(queries, query => query.Contains("Семейство Х; Chimney 30", StringComparison.Ordinal));

        Assert.Equal(5, result.Types.Count(item => item.Code == Sibling));
        Assert.Equal(5, result.Types.Count(item => item.Code == ThirdLeaf));
        Assert.Equal(5, result.Types.Count(item => item.Code == FamilyCode));
        Assert.Equal(3, result.Assignments.Count);
        Assert.Equal(15, Assert.Single(result.SetParameterBatches).Count);
    }

    // ── The question budget ─────────────────────────────────────────────

    [Fact]
    public async Task TypeStage_Budget_AsksExactlyTheLimit_AndNamesTheRestWithIds()
    {
        Assert.Equal(100, AgentClassificationMatcher.MaxTypeDifferentiationQuestions);

        var client = new Script();
        // 102 letter-only names: 102 meanings, all asked for - two too many.
        var types = Enumerable.Range(1, 102)
            .Select(index => Type(index, "Тип " + (char)('a' + index % 26) + (char)('a' + index / 26)))
            .Cast<ClassifyTypeSnapshot>()
            .ToList();

        var result = await MatchAsync(client, types);

        Assert.Null(result.Error);
        Assert.Equal(100, client.PerTypeQuestions);
        // Branch + two descent levels + five calls of twenty questions.
        Assert.Equal(1 + 2 + 5, client.Calls);
        Assert.Equal(client.Calls, result.DecisionCalls);

        // The two types beyond the limit keep the family code and are named
        // with what a re-run needs.
        Assert.All(result.Types, item =>
        {
            Assert.Equal(ClassifyTypeStatus.Proposed, item.Status);
            Assert.Equal(FamilyCode, item.Code);
        });
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("2 type(s) of 1 family stay with the family code", warning, StringComparison.Ordinal);
        Assert.Contains("per-type question budget of 100 per classify_types call was reached", warning, StringComparison.Ordinal);
        Assert.Contains("'Семейство Х' (typeIds 101, 102)", warning, StringComparison.Ordinal);
        Assert.Contains(
            "[Action: accept the family codes, or to tell their types apart re-run classify_types " +
            "with typeIds set to the listed ids and onlyEmpty=false when their codes are already written]",
            warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TypeStage_Budget_CountsQuestionsNotTypes()
    {
        var client = new Script();
        // 60 types, 30 meanings of two sizes each: thirty questions, far from the limit.
        var types = Enumerable.Range(1, 60)
            .Select(index => Type(index, "Ряд " + (char)('a' + ((index - 1) / 2) % 26) +
                (char)('a' + (index - 1) / 52) + " " + index))
            .Cast<ClassifyTypeSnapshot>()
            .ToList();

        var result = await MatchAsync(client, types);

        Assert.Null(result.Error);
        Assert.Equal(30, client.PerTypeQuestions);
        Assert.Empty(result.Warnings);
        Assert.Equal(60, Assert.Single(result.Assignments).TypeIdCount);
    }

    [Fact]
    public async Task TypeStage_BudgetWarning_IsBoundedAndCountsTheUnlistedRest()
    {
        var client = new Script();
        // Far more leftovers than one warning may list: the ids are capped, the rest is counted.
        var types = Enumerable.Range(1, 500)
            .Select(index => Type(index, "Тип " + (char)('a' + index % 26) + (char)('a' + (index / 26) % 26)))
            .Cast<ClassifyTypeSnapshot>()
            .ToList();

        var result = await MatchAsync(client, types);

        Assert.Null(result.Error);
        Assert.Equal(100, client.PerTypeQuestions);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("400 type(s) of 1 family stay with the family code", warning, StringComparison.Ordinal);
        Assert.Contains("(typeIds 101, 102, 103", warning, StringComparison.Ordinal);
        Assert.Contains(", …); and 200 more type(s) not listed - select them by the categories or familyIds", warning, StringComparison.Ordinal);
        Assert.True(warning.Length < 3000, "the warning must stay a warning, not a dump: " + warning.Length);
    }

    // ── When the family itself is not trusted ───────────────────────────

    [Theory]
    [InlineData(0.4)]
    [InlineData(0.2)]
    public async Task TypeStage_UnconfidentFamilyChain_AsksNoPerTypeQuestions(double chainScore)
    {
        var client = new Script { ChainScore = chainScore, Mode = PerTypeMode.Sibling };

        var result = await MatchAsync(client, new[] { Type(1, "External"), Type(2, "Internal") });

        Assert.Null(result.Error);
        Assert.Equal(new[] { "branch", "descend", "descend" }, client.States.Select(Kind));
        Assert.All(result.Types, item =>
        {
            Assert.Equal(ClassifyTypeStatus.LowConfidence, item.Status);
            Assert.Equal(FamilyCode, item.Code);
        });
    }

    [Fact]
    public async Task TypeStage_BareFamilyChain_AsksNoPerTypeQuestions()
    {
        var client = new Script { BareChain = true, Mode = PerTypeMode.Sibling };

        var result = await MatchAsync(client, new[] { Type(1, "External"), Type(2, "Internal") });

        Assert.Null(result.Error);
        Assert.Equal(new[] { "branch", "descend", "descend" }, client.States.Select(Kind));
        Assert.All(result.Types, item => Assert.Equal(ClassifyTypeStatus.NoConfidence, item.Status));
    }

    /// <summary>The kind of a recorded state: branch, descend, assign (a code question) or per-type.</summary>
    private static string Kind(string state)
    {
        if (state == "per-type")
            return "per-type";
        if (state.StartsWith("Task: pick the top-level branch", StringComparison.Ordinal))
            return "branch";
        if (state.StartsWith("Task: descend", StringComparison.Ordinal))
            return "descend";
        if (state.StartsWith("Task: assign", StringComparison.Ordinal))
            return "assign";
        throw new InvalidOperationException("unknown state: " + state);
    }

    // ── The scripted decision client ────────────────────────────────────

    /// <summary>How the scripted client answers the differentiation questions.</summary>
    public enum PerTypeMode
    {
        /// <summary>Token overlap with the question; the first option by code order without any.</summary>
        Keyword,

        Sibling,
        None,
        FamilyLow,
        LowSibling,
        BareSibling,
        Mixed,
        Empty,
        Throw
    }

    /// <summary>
    /// A deterministic stand-in for the decision model. Outside the
    /// differentiation stage it walks a fixed path of codes (the first code of
    /// the path an option carries, else the first option), so the route a
    /// family takes does not depend on any text. The differentiation questions
    /// are recognized by their SIBLING state and answered by
    /// <see cref="PerTypeMode"/>; the honest none is never picked by token
    /// overlap - a real model does not choose "none" because of shared words.
    /// </summary>
    private sealed class Script : IAgentDecisionClient
    {
        public double ChainScore { get; set; } = 0.9;

        public bool BareChain { get; set; }

        public string[] Path { get; set; } = { "10", "10.10", FamilyCode };

        /// <summary>The code the walk ends on - the family code the per-type answers refer to.</summary>
        public string Code { get; set; } = FamilyCode;

        public PerTypeMode Mode { get; set; } = PerTypeMode.Keyword;

        /// <summary>Answer every differentiation question from this ordinal (1-based) on with a refusal.</summary>
        public int RefuseFromQuestion { get; set; } = int.MaxValue;

        /// <summary>Answer a shortlist question carrying both codes with both of them (a multi-code family).</summary>
        public bool BothLeafCodes { get; set; }

        public int PerTypeQuestions { get; private set; }

        public int Calls { get; private set; }

        /// <summary>The state of every call; differentiation calls are recorded as "per-type".</summary>
        public List<string> States { get; } = new();

        public List<IReadOnlyDictionary<string, AgentDecisionQuestion>> PerTypeRequests { get; } = new();

        public string ProviderName => "test";

        public string Model => "test-model";

        public bool IsEnabled => true;

        public Task<IReadOnlyDictionary<string, AgentDecisionAnswer>> AskAsync(
            string state,
            IReadOnlyDictionary<string, AgentDecisionQuestion> questions,
            CancellationToken ct)
        {
            Calls++;
            var perType = state.Contains("SIBLING", StringComparison.Ordinal);
            States.Add(perType ? "per-type" : state);
            var answers = new Dictionary<string, AgentDecisionAnswer>(StringComparer.Ordinal);
            var ordinal = PerTypeQuestions;
            if (perType)
            {
                PerTypeQuestions += questions.Count;
                PerTypeRequests.Add(questions);
                if (Mode == PerTypeMode.Empty)
                    return Task.FromResult<IReadOnlyDictionary<string, AgentDecisionAnswer>>(answers);
                if (Mode == PerTypeMode.Throw)
                    throw new InvalidOperationException("HttpClient timeout");
            }

            var number = 0;
            foreach (var pair in questions)
            {
                number++;
                answers[pair.Key] = perType
                    ? PerTypeAnswer(pair.Value, ordinal + number)
                    : PathAnswer(pair.Value);
            }

            return Task.FromResult<IReadOnlyDictionary<string, AgentDecisionAnswer>>(answers);
        }

        private AgentDecisionAnswer PerTypeAnswer(AgentDecisionQuestion question, int ordinal)
        {
            switch (Mode)
            {
                case PerTypeMode.None:
                    return Prob(AbstainId, 0.9);
                case PerTypeMode.FamilyLow:
                    return Prob(Code, 0.2);
                case PerTypeMode.LowSibling:
                    return new AgentDecisionAnswer
                    {
                        Type = "choice",
                        Probabilities = new Dictionary<string, double> { [Sibling] = 0.45, [Code] = 0.3 }
                    };
                case PerTypeMode.BareSibling:
                    return new AgentDecisionAnswer { Type = "choice", Choice = Sibling };
                case PerTypeMode.Sibling:
                    return Prob(Sibling, 0.9);
                case PerTypeMode.Mixed:
                    return ordinal >= RefuseFromQuestion
                        ? new AgentDecisionAnswer { Type = "choice", Error = "refused" }
                        : Prob(ordinal == 1 ? Code : Sibling, 0.9);
                default:
                    return Keyword(question);
            }
        }

        private AgentDecisionAnswer PathAnswer(AgentDecisionQuestion question)
        {
            var criteria = question.Criteria!;
            if (BothLeafCodes && criteria.ContainsKey(FamilyCode) && criteria.ContainsKey(Sibling)
                && !criteria.ContainsKey("10.10"))
            {
                return new AgentDecisionAnswer
                {
                    Type = "choice",
                    Probabilities = new Dictionary<string, double> { [FamilyCode] = 0.9, [Sibling] = 0.6 }
                };
            }

            var pick = Path.FirstOrDefault(criteria.ContainsKey) ?? criteria.Keys.First();
            return BareChain
                ? new AgentDecisionAnswer { Type = "choice", Choice = pick }
                : Prob(pick, ChainScore);
        }

        private static AgentDecisionAnswer Keyword(AgentDecisionQuestion question)
        {
            var marker = question.Instructions.IndexOf("Match: ", StringComparison.Ordinal);
            var query = marker >= 0
                ? question.Instructions.Substring(marker + "Match: ".Length)
                : question.Instructions;
            var queryTokens = AgentSemanticLexicon.Tokenize(query);
            var ranked = question.Criteria!
                .Where(option => !string.Equals(option.Key, AbstainId, StringComparison.Ordinal))
                .Select(option => (
                    Id: option.Key,
                    Overlap: AgentSemanticLexicon.Tokenize(option.Value ?? string.Empty)
                        .Count(token => queryTokens.Any(queryToken => AgentSemanticLexicon.TokensMatch(queryToken, token)))))
                .OrderByDescending(option => option.Overlap)
                .ThenBy(option => option.Id, StringComparer.Ordinal)
                .ToList();

            var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
            var rank = 0;
            foreach (var option in ranked.Where(option => option.Overlap > 0))
                probabilities[option.Id] = System.Math.Max(0.05, 0.9 - rank++ * 0.1);

            if (probabilities.Count == 0)
                probabilities[ranked[0].Id] = 0.9;

            return new AgentDecisionAnswer { Type = "choice", Probabilities = probabilities };
        }

        private static AgentDecisionAnswer Prob(string id, double score) => new()
        {
            Type = "choice",
            Probabilities = new Dictionary<string, double> { [id] = score }
        };
    }
}
