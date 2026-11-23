using System.Text;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// The per-type differentiation stage (C5-04): a family that ended with ONE
/// code still gets its types asked apart when their own text differs - the
/// options are the family code, its sibling leaves and an honest none. A
/// confident sibling earns its own code; everything else keeps the family
/// code, and an unconfident sibling lands in uncertain.
/// </summary>
public sealed class AgentClassificationMatcherTypeStageTests
{
    private const string AbstainId = "__none__";

    private static AgentClassificationEntry Row(
        string code, string description, int level, string? parent) =>
        new() { Code = code, Description = description, Level = level, ParentCode = parent };

    /// <summary>
    /// «10 Shell» → «10.10 Walls» → «10.10.10 Load-bearing walls» +
    /// «10.10.20 Partitions»: a Russian family matches nothing lexically, so the
    /// descent ends on the single code 10.10.10 with two sibling leaves under
    /// the same parent.
    /// </summary>
    private static AgentClassificationTable WallTable() =>
        AgentClassificationTable.Build(new[]
        {
            Row("10", "Shell", 1, null),
            Row("10.10", "Walls", 2, "10"),
            Row("10.10.10", "Load-bearing walls", 3, "10.10"),
            Row("10.10.20", "Partitions", 3, "10.10"),
            Row("20", "Services", 1, null),
            Row("20.10", "Pipelines", 2, "20"),
            Row("20.10.10", "Pipes", 3, "20.10"),
            Row("30", "Other", 1, null)
        }, null);

    private static ClassifyTypeSnapshot Type(
        long id, string typeName, params string[] values) =>
        new(id, "Категория", "Семейство Х", null, typeName, null, values);

    private static Task<ClassifyMatchResult> MatchAsync(
        ScriptedClient client, IReadOnlyList<ClassifyTypeSnapshot> types) =>
        new AgentClassificationMatcher(new AgentSemanticRanker(client)).MatchAsync(
            AgentClassificationTexts.GroupTypes(types), WallTable(),
            new ClassifyOptions(), CancellationToken.None);

    private static bool AskedPerType(ScriptedClient client) =>
        client.States.Any(state => state.Contains("SIBLING", StringComparison.Ordinal));

    [Fact]
    public async Task TypeStage_ConfidentSibling_EarnsItsOwnCode()
    {
        var client = new ScriptedClient();
        var types = new List<ClassifyTypeSnapshot>
        {
            Type(1, "Bearing type"),
            Type(2, "Partition type")
        };

        var result = await MatchAsync(client, types);

        Assert.Null(result.Error);
        Assert.True(AskedPerType(client));

        // The sibling leaf and the honest none WERE offered for the types.
        Assert.Contains(client.Requests.SelectMany(q => q.Values),
            question => question.Criteria!.ContainsKey("10.10.20")
                        && question.Criteria!.ContainsKey(AbstainId)
                        && question.Criteria!.ContainsKey("10.10.10"));

        var bearing = result.Types.Single(item => item.Type.ElementId == 1);
        var partition = result.Types.Single(item => item.Type.ElementId == 2);
        Assert.Equal(ClassifyTypeStatus.Proposed, bearing.Status);
        Assert.Equal("10.10.10", bearing.Code);
        Assert.Equal(ClassifyTypeStatus.Proposed, partition.Status);
        Assert.Equal("10.10.20", partition.Code);

        // One family, two codes: two assignment groups and per-type set items.
        Assert.Equal(2, result.Assignments.Count);
        Assert.All(result.Assignments, group => Assert.Equal("Семейство Х", group.Family));
        var batch = Assert.Single(result.SetParameterBatches);
        Assert.Equal(
            new[] { "10.10.10", "10.10.20" },
            batch.OrderBy(item => item.Value, StringComparer.Ordinal)
                .Select(item => item.Value)
                .ToArray());
    }

    [Fact]
    public async Task TypeStage_HonestNone_KeepsTheFamilyCode()
    {
        var client = new ScriptedClient { AbstainPerType = true };
        var types = new List<ClassifyTypeSnapshot>
        {
            Type(1, "Bearing type"),
            Type(2, "Partition type")
        };

        var result = await MatchAsync(client, types);

        Assert.Null(result.Error);
        Assert.False(result.Incomplete);
        Assert.True(AskedPerType(client));
        Assert.All(result.Types, item =>
        {
            Assert.Equal(ClassifyTypeStatus.Proposed, item.Status);
            Assert.Equal("10.10.10", item.Code);
        });
        var group = Assert.Single(result.Assignments);
        Assert.Equal(2, group.TypeIdCount);
        var batch = Assert.Single(result.SetParameterBatches);
        Assert.All(batch, item => Assert.Equal("10.10.10", item.Value));
    }

    [Fact]
    public async Task TypeStage_LowScoreSibling_KeepsFamilyCodeInUncertain()
    {
        var client = new ScriptedClient { PerTypeScore = 0.3 };
        var types = new List<ClassifyTypeSnapshot>
        {
            Type(1, "Bearing type"),
            Type(2, "Partition type")
        };

        var result = await MatchAsync(client, types);

        Assert.Null(result.Error);
        var partition = result.Types.Single(item => item.Type.ElementId == 2);
        Assert.Equal(ClassifyTypeStatus.LowConfidence, partition.Status);
        // The family code stays on the type; the sibling is only a candidate.
        Assert.Equal("10.10.10", partition.Code);
        Assert.Equal("10.10.20", partition.Candidates[0].Code);
        Assert.Contains("below the confidence threshold", partition.Reason ?? string.Empty, StringComparison.Ordinal);
        var batch = Assert.Single(result.SetParameterBatches);
        Assert.Single(batch);
        Assert.Equal(1, batch[0].ElementId);
    }

    [Fact]
    public async Task TypeStage_BareSiblingChoice_KeepsFamilyCodeAsNoConfidence()
    {
        var client = new ScriptedClient { BarePerType = true };
        var types = new List<ClassifyTypeSnapshot>
        {
            Type(1, "Bearing type"),
            Type(2, "Partition type")
        };

        var result = await MatchAsync(client, types);

        Assert.Null(result.Error);
        var partition = result.Types.Single(item => item.Type.ElementId == 2);
        Assert.Equal(ClassifyTypeStatus.NoConfidence, partition.Status);
        Assert.Equal("10.10.10", partition.Code);
        // The sibling was a bare guess, so only the family code of type 1 is
        // proposed; the bare sibling type stays out of the write payload.
        var batch = Assert.Single(result.SetParameterBatches);
        Assert.Single(batch);
        Assert.Equal(1, batch[0].ElementId);
        var group = Assert.Single(result.Assignments);
        Assert.Equal(1, group.TypeIdCount);
    }

    [Fact]
    public async Task TypeStage_IdenticalTypeTexts_AskNoPerTypeQuestions()
    {
        var client = new ScriptedClient();
        // The token sets of both types are equal after the family tokens are
        // removed - the digits vanish in the tokenizer - so there is nothing to
        // ask the model about.
        var types = new List<ClassifyTypeSnapshot>
        {
            Type(1, "Тип 1"),
            Type(2, "Тип 2")
        };

        var result = await MatchAsync(client, types);

        Assert.Null(result.Error);
        Assert.False(AskedPerType(client));
        Assert.All(result.Types, item => Assert.Equal("10.10.10", item.Code));
        var group = Assert.Single(result.Assignments);
        Assert.Equal(2, group.TypeIdCount);
    }

    [Fact]
    public async Task TypeStage_SingleTypeFamily_AskNoPerTypeQuestions()
    {
        var client = new ScriptedClient();
        var types = new List<ClassifyTypeSnapshot> { Type(1, "Partition type") };

        var result = await MatchAsync(client, types);

        Assert.Null(result.Error);
        Assert.False(AskedPerType(client));
        var item = Assert.Single(result.Types);
        Assert.Equal("10.10.10", item.Code);
    }

    [Fact]
    public async Task TypeStage_NoSiblingLeaves_AskNoPerTypeQuestions()
    {
        // «40 Pumps» has a single leaf descendant: the descent ends on 40.10.10
        // and its parent branch offers no second leaf to ask about.
        var rows = new List<AgentClassificationEntry>
        {
            Row("10", "Shell", 1, null),
            Row("10.10", "Walls", 2, "10"),
            Row("10.10.10", "Load-bearing walls", 3, "10.10"),
            Row("10.10.20", "Partitions", 3, "10.10"),
            Row("20", "Services", 1, null),
            Row("20.10", "Pipelines", 2, "20"),
            Row("20.10.10", "Pipes", 3, "20.10"),
            Row("30", "Other", 1, null),
            // The rows repeat the family words exactly, so the branch question
            // deterministically walks this family into the single-leaf branch.
            Row("40", "Pump family group", 1, null),
            Row("40.10", "Pump family branch", 2, "40"),
            Row("40.10.10", "Pump family one", 3, "40.10")
        };
        var table = AgentClassificationTable.Build(rows, null);
        var client = new ScriptedClient();
        var types = new List<ClassifyTypeSnapshot>
        {
            new(1, "Категория", "Pump family", null, "Alpha type", null, Array.Empty<string>()),
            new(2, "Категория", "Pump family", null, "Beta type", null, Array.Empty<string>())
        };

        var result = await new AgentClassificationMatcher(new AgentSemanticRanker(client)).MatchAsync(
            AgentClassificationTexts.GroupTypes(types), table,
            new ClassifyOptions(), CancellationToken.None);

        Assert.Null(result.Error);
        Assert.DoesNotContain(
            client.States, state => state.Contains("SIBLING", StringComparison.Ordinal));
        Assert.All(result.Types, item => Assert.Equal("40.10.10", item.Code));
    }

    [Fact]
    public async Task TypeStage_QuestionBudget_LeftOutTypesKeepTheFamilyCode()
    {
        var client = new ScriptedClient();
        // Letter-only type names: a digit token would collide with the numeric
        // tail of the offered codes.
        var types = Enumerable.Range(1, 102)
            .Select(index => Type(
                index,
                "Тип " + (char)('a' + index % 26) + (char)('a' + index / 26),
                "vx" + index))
            .Cast<ClassifyTypeSnapshot>()
            .ToList();

        var result = await MatchAsync(client, types);

        Assert.Null(result.Error);
        Assert.True(AskedPerType(client));
        // 100 of the 102 types fit the per-type question budget; the two left
        // out are named with an action instead of being silently dropped.
        Assert.Contains(result.Warnings, warning => warning.Contains(
            "the per-type question budget", StringComparison.Ordinal));
        Assert.All(result.Types, item =>
        {
            Assert.Equal(ClassifyTypeStatus.Proposed, item.Status);
            Assert.Equal("10.10.10", item.Code);
        });
        var group = Assert.Single(result.Assignments);
        Assert.Equal(102, group.TypeIdCount);
    }

    /// <summary>
    /// The keyword client of the matcher tests with the per-type knobs: the
    /// differentiation questions are recognized by their SIBLING state and can
    /// be answered with the abstain, a low score or a bare choice.
    /// </summary>
    private sealed class ScriptedClient : IAgentDecisionClient
    {
        public double Score { get; set; } = 0.9;

        /// <summary>Answer every differentiation question with the honest none.</summary>
        public bool AbstainPerType { get; set; }

        /// <summary>Score override of the differentiation questions.</summary>
        public double? PerTypeScore { get; set; }

        /// <summary>Answer every differentiation question with a bare Choice.</summary>
        public bool BarePerType { get; set; }

        public bool Enabled { get; set; } = true;

        public List<IReadOnlyDictionary<string, AgentDecisionQuestion>> Requests { get; } = new();

        public List<string> States { get; } = new();

        public string ProviderName => "test";

        public string Model => "test-model";

        public bool IsEnabled => Enabled;

        public Task<IReadOnlyDictionary<string, AgentDecisionAnswer>> AskAsync(
            string state,
            IReadOnlyDictionary<string, AgentDecisionQuestion> questions,
            CancellationToken ct)
        {
            States.Add(state);
            Requests.Add(questions);
            var perType = state.Contains("SIBLING", StringComparison.Ordinal);
            var score = perType && PerTypeScore.HasValue ? PerTypeScore.Value : Score;
            var answers = new Dictionary<string, AgentDecisionAnswer>(StringComparer.Ordinal);
            foreach (var pair in questions)
            {
                if (perType && AbstainPerType)
                {
                    answers[pair.Key] = new AgentDecisionAnswer
                    {
                        Type = "choice",
                        Probabilities = new Dictionary<string, double> { [AbstainId] = score }
                    };
                    continue;
                }

                var marker = pair.Value.Instructions.IndexOf("Match: ", StringComparison.Ordinal);
                var query = marker >= 0
                    ? pair.Value.Instructions.Substring(marker + "Match: ".Length)
                    : pair.Value.Instructions;
                var queryTokens = Tokens(query);
                var ranked = pair.Value.Criteria!
                    .Select(option => (
                        Id: option.Key,
                        Overlap: Tokens(option.Value ?? string.Empty)
                            .Count(token => queryTokens.Any(q => Matches(q, token)))))
                    .OrderByDescending(option => option.Overlap)
                    .ThenBy(option => option.Id, StringComparer.Ordinal)
                    .ToList();

                if (perType && BarePerType)
                {
                    answers[pair.Key] = new AgentDecisionAnswer
                    {
                        Type = "choice",
                        Choice = ranked[0].Id,
                        Probabilities = null
                    };
                    continue;
                }

                var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
                var rank = 0;
                foreach (var option in ranked.Where(option => option.Overlap > 0).Take(5))
                {
                    probabilities[option.Id] = System.Math.Max(0.05, score - rank * 0.1);
                    rank++;
                }

                if (probabilities.Count == 0)
                    probabilities[ranked[0].Id] = score;

                answers[pair.Key] = new AgentDecisionAnswer
                {
                    Type = "choice",
                    Probabilities = probabilities
                };
            }

            return Task.FromResult<IReadOnlyDictionary<string, AgentDecisionAnswer>>(answers);
        }

        private static HashSet<string> Tokens(string text)
        {
            var tokens = new HashSet<string>(StringComparer.Ordinal);
            var builder = new StringBuilder();
            foreach (var character in text)
            {
                if (char.IsLetterOrDigit(character))
                {
                    builder.Append(character);
                    continue;
                }

                Flush(builder, tokens);
            }

            Flush(builder, tokens);
            return tokens;
        }

        private static void Flush(StringBuilder builder, HashSet<string> tokens)
        {
            if (builder.Length > 0)
                tokens.Add(builder.ToString().ToLowerInvariant());
            builder.Clear();
        }

        private static bool Matches(string left, string right) =>
            string.Equals(left, right, StringComparison.Ordinal)
            || (System.Math.Min(left.Length, right.Length) >= 5
                && (left.StartsWith(right, StringComparison.Ordinal)
                    || right.StartsWith(left, StringComparison.Ordinal)));
    }
}
