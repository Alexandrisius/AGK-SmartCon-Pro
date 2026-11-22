using System.Text;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// The semantic descent of weak families (D2): a family without a single
/// lexical hit must be walked down the tree level by level - the options are
/// always the children of the previously chosen row, never "the first 40
/// leaves of the table in code order".
/// </summary>
public sealed class AgentClassificationMatcherDescentTests
{
    private const string AbstainId = "__none__";

    private static AgentClassificationEntry Row(
        string code, string description, int level, string? parent) =>
        new() { Code = code, Description = description, Level = level, ParentCode = parent };

    /// <summary>
    /// «20 Services» → «20.01 Piping» (60 leaves) + «20.02 Valves» (60 leaves,
    /// «Ball valves» = 20.02.007 among them) - the validator probe table.
    /// </summary>
    private static AgentClassificationTable ServicesTable()
    {
        var rows = new List<AgentClassificationEntry>
        {
            Row("20", "Services", 1, null),
            Row("20.01", "Piping", 2, "20"),
            Row("20.02", "Valves", 2, "20")
        };
        for (var index = 1; index <= 60; index++)
        {
            rows.Add(Row("20.01." + index.ToString("000", System.Globalization.CultureInfo.InvariantCulture),
                "Pipe run " + index, 3, "20.01"));
            rows.Add(Row("20.02." + index.ToString("000", System.Globalization.CultureInfo.InvariantCulture),
                index == 7 ? "Ball valves" : "Gate valves " + index, 3, "20.02"));
        }

        return AgentClassificationTable.Build(rows, null);
    }

    private static List<ClassifyTypeSnapshot> ValveFamily() => new()
    {
        ClassifyType(1, "Вентиль шаровый", "Вентиль Ду15", "Трубопроводная арматура")
    };

    private static ClassifyTypeSnapshot ClassifyType(
        long id, string family, string typeName, string category) =>
        new(id, category, family, null, typeName, null, Array.Empty<string>());

    [Fact]
    public async Task Descent_ZeroLexical_OffersRightCodeAndProposesIt()
    {
        var client = new TranslatingDecisionClient();
        var matcher = new AgentClassificationMatcher(new AgentSemanticRanker(client));

        var result = await matcher.MatchAsync(
            AgentClassificationTexts.GroupTypes(ValveFamily()), ServicesTable(),
            new ClassifyOptions(), CancellationToken.None);

        // The right code WAS offered among the options of some question.
        var offered = client.Requests
            .SelectMany(questions => questions.Values)
            .Any(question => question.Criteria!.ContainsKey("20.02.007"));
        Assert.True(offered, "the right code 20.02.007 must appear among the offered options");

        Assert.Null(result.Error);
        var proposed = Assert.Single(result.Types);
        Assert.Equal(ClassifyTypeStatus.Proposed, proposed.Status);
        Assert.Equal("20.02.007", proposed.Code);
        var batch = Assert.Single(result.SetParameterBatches);
        Assert.Equal("20.02.007", batch[0].Value);
    }

    [Fact]
    public async Task Descent_WideLevel_IsSplitAndStillOffersTheRightCode()
    {
        var rows = new List<AgentClassificationEntry>
        {
            Row("20", "Services", 1, null),
            Row("20.02", "Valves", 2, "20")
        };
        for (var index = 1; index <= 300; index++)
        {
            rows.Add(Row(
                "20.02." + index.ToString("000", System.Globalization.CultureInfo.InvariantCulture),
                index == 290 ? "Ball valves" : "Gate valves " + index, 3, "20.02"));
        }

        var client = new TranslatingDecisionClient();
        var matcher = new AgentClassificationMatcher(new AgentSemanticRanker(client));

        var result = await matcher.MatchAsync(
            AgentClassificationTexts.GroupTypes(ValveFamily()),
            AgentClassificationTable.Build(rows, null),
            new ClassifyOptions(), CancellationToken.None);

        // The 300-leaf level is split into questions of at most 255 options,
        // and the right code at position 290 is still offered.
        var wideQuestions = client.Requests
            .SelectMany(questions => questions.Values)
            .Where(question => question.Criteria!.ContainsKey("20.02.290"))
            .ToList();
        Assert.NotEmpty(wideQuestions);
        Assert.All(wideQuestions, question => Assert.InRange(question.Criteria!.Count, 2, 255));
        Assert.Null(result.Error);
        Assert.Equal("20.02.290", Assert.Single(result.Types).Code);
    }

    [Fact]
    public async Task Descent_Abstain_EndsFamilyAsNoCandidatesWithoutSetItems()
    {
        var client = new TranslatingDecisionClient { Abstain = true };
        var matcher = new AgentClassificationMatcher(new AgentSemanticRanker(client));

        var result = await matcher.MatchAsync(
            AgentClassificationTexts.GroupTypes(ValveFamily()), ServicesTable(),
            new ClassifyOptions(), CancellationToken.None);

        Assert.Null(result.Error);
        var item = Assert.Single(result.Types);
        Assert.Equal(ClassifyTypeStatus.NoCandidates, item.Status);
        Assert.Null(item.Code);
        Assert.Contains("no option", item.Reason ?? string.Empty, StringComparison.Ordinal);
        Assert.Empty(result.SetParameterBatches);
        Assert.Empty(result.Assignments);
        // The model DID answer (with the abstain) - the run is not incomplete.
        Assert.False(result.Incomplete);
    }

    [Fact]
    public async Task Descent_ChainScore_IsMinimumOverLevels()
    {
        // The branch level matches by one word (penalty 0.1), the leaf level by
        // two: chain = min(0.9, 0.7 - 0.1, 0.95) = 0.6.
        var client = new TranslatingDecisionClient { ScoreByCall = { 0.9, 0.7, 0.95 } };
        var matcher = new AgentClassificationMatcher(new AgentSemanticRanker(client));

        var result = await matcher.MatchAsync(
            AgentClassificationTexts.GroupTypes(ValveFamily()), ServicesTable(),
            new ClassifyOptions(), CancellationToken.None);

        var proposed = Assert.Single(result.Types);
        Assert.Equal(ClassifyTypeStatus.Proposed, proposed.Status);
        Assert.Equal(0.6, proposed.Score, 5);

        var weak = new TranslatingDecisionClient { ScoreByCall = { 0.9, 0.5, 0.9 } };
        var weakResult = await new AgentClassificationMatcher(new AgentSemanticRanker(weak)).MatchAsync(
            AgentClassificationTexts.GroupTypes(ValveFamily()), ServicesTable(),
            new ClassifyOptions(), CancellationToken.None);

        var uncertain = Assert.Single(weakResult.Types);
        Assert.Equal(ClassifyTypeStatus.LowConfidence, uncertain.Status);
        Assert.Equal(0.4, uncertain.Score, 5);
        Assert.Empty(weakResult.SetParameterBatches);
    }

    /// <summary>
    /// A deterministic stand-in for a bilingual decision model: Russian query
    /// words map to English option words (вентиль→valve, шаровый→ball …), the
    /// best option gets the call's score. Without any overlap the first option
    /// by code order wins - exactly what the model must NOT be left alone with.
    /// </summary>
    private sealed class TranslatingDecisionClient : IAgentDecisionClient
    {
        private static readonly Dictionary<string, string> Vocabulary =
            new(StringComparer.Ordinal)
            {
                ["вентиль"] = "valve",
                ["шаровый"] = "ball",
                ["труба"] = "pipe",
                ["стена"] = "wall",
                ["насос"] = "pump"
            };

        public double Score { get; set; } = 0.9;

        /// <summary>Score of the Nth call (branch first, then each level).</summary>
        public List<double> ScoreByCall { get; } = new();

        /// <summary>Answer every descent level with the abstain option.</summary>
        public bool Abstain { get; set; }

        public bool Enabled { get; set; } = true;

        public List<IReadOnlyDictionary<string, AgentDecisionQuestion>> Requests { get; } = new();

        private int Calls { get; set; }

        public string ProviderName => "test";

        public string Model => "test-model";

        public bool IsEnabled => Enabled;

        public Task<IReadOnlyDictionary<string, AgentDecisionAnswer>> AskAsync(
            string state,
            IReadOnlyDictionary<string, AgentDecisionQuestion> questions,
            CancellationToken ct)
        {
            Calls++;
            var score = ScoreByCall.Count > 0
                ? ScoreByCall[System.Math.Min(Calls - 1, ScoreByCall.Count - 1)]
                : Score;
            Requests.Add(questions);
            var answers = new Dictionary<string, AgentDecisionAnswer>(StringComparer.Ordinal);
            foreach (var pair in questions)
            {
                var marker = pair.Value.Instructions.IndexOf("Match: ", StringComparison.Ordinal);
                var query = marker >= 0
                    ? pair.Value.Instructions.Substring(marker + "Match: ".Length)
                    : pair.Value.Instructions;

                if (Abstain && pair.Value.Criteria!.ContainsKey(AbstainId))
                {
                    answers[pair.Key] = new AgentDecisionAnswer
                    {
                        Type = "choice",
                        Probabilities = new Dictionary<string, double> { [AbstainId] = score }
                    };
                    continue;
                }

                var targets = Translate(query);
                var ranked = pair.Value.Criteria!
                    .Select(option => (
                        Id: option.Key,
                        Overlap: Tokens(option.Value ?? string.Empty)
                            .Count(token => targets.Any(target => Matches(target, token)))))
                    .OrderByDescending(option => option.Overlap)
                    .ThenBy(option => option.Id, StringComparer.Ordinal)
                    .ToList();

                var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
                var rank = 0;
                foreach (var option in ranked.Where(option => option.Overlap > 0).Take(5))
                {
                    // A two-word hit is a better match than a one-word hit: the
                    // score must differ across split batches too, or the merge
                    // tie-breaks by index and picks the wrong leaf.
                    var penalty = option.Overlap >= 2 ? 0 : 0.1;
                    probabilities[option.Id] = System.Math.Max(0.05, score - penalty - rank * 0.05);
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

        private static HashSet<string> Translate(string query)
        {
            var tokens = Tokens(query);
            var targets = new HashSet<string>(tokens, StringComparer.Ordinal);
            foreach (var pair in Vocabulary)
            {
                if (tokens.Contains(pair.Key))
                    targets.Add(pair.Value);
            }

            return targets;
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
