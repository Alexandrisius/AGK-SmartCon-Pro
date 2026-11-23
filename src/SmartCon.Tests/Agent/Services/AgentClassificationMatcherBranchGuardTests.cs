using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// Owner feedback 2026-11-24: for a floor-heating manifold the decision model
/// picked a leaf of the conveying branch (D10) with score 0.73 and the code went
/// into setParameters. The branch guard demotes a code whose top-level branch
/// shares no word with the family name while another branch does.
/// </summary>
public sealed class AgentClassificationMatcherBranchGuardTests
{
    private static AgentClassificationEntry Row(
        string code, string description, int level, string? parent) =>
        new() { Code = code, Description = description, Level = level, ParentCode = parent };

    private static AgentClassificationTable HeatingTable() =>
        AgentClassificationTable.Build(new[]
        {
            Row("D10", "Другие конвейерные системы", 1, null),
            Row("D10.10", "Другие системы транспортировки", 2, "D10"),
            Row("D30", "Отопление, вентиляция и кондиционирование", 1, null),
            Row("D30.10", "Нагревательное оборудование", 2, "D30"),
            Row("D30.10.10", "Коллекторы напольного отопления", 3, "D30.10")
        }, null);

    private static ClassifyTypeSnapshot Manifold() =>
        new(1, "Оборудование", "Коллектор напольного отопления", null,
            "Коллектор К-100", null, new[] { "система транспортировки" });

    [Fact]
    public async Task WrongDisciplineCode_IsDemotedToUncertain()
    {
        var client = new FixedCodeDecisionClient("D10.10", 0.73);
        var matcher = new AgentClassificationMatcher(new AgentSemanticRanker(client));

        var result = await matcher.MatchAsync(
            AgentClassificationTexts.GroupTypes(new[] { Manifold() }), HeatingTable(),
            new ClassifyOptions(), CancellationToken.None);

        Assert.Null(result.Error);
        var item = Assert.Single(result.Types);
        Assert.Equal(ClassifyTypeStatus.LowConfidence, item.Status);
        Assert.Equal("D10.10", item.Code);
        Assert.Contains("another discipline", item.Reason);
        Assert.Empty(result.Assignments);
        Assert.Empty(result.SetParameterBatches);
    }

    [Fact]
    public async Task CodeOfTheFamilyOwnBranch_StaysProposed()
    {
        var client = new FixedCodeDecisionClient("D30.10.10", 0.73);
        var matcher = new AgentClassificationMatcher(new AgentSemanticRanker(client));

        var result = await matcher.MatchAsync(
            AgentClassificationTexts.GroupTypes(new[] { Manifold() }), HeatingTable(),
            new ClassifyOptions(), CancellationToken.None);

        var item = Assert.Single(result.Types);
        Assert.Equal(ClassifyTypeStatus.Proposed, item.Status);
        Assert.Equal("D30.10.10", item.Code);
        Assert.Single(result.Assignments);
        Assert.Single(result.SetParameterBatches);
    }

    /// <summary>
    /// Answers every Choice with one fixed code when it is among the options (the
    /// first option otherwise), so a test can force a disciplined-looking wrong
    /// pick without a live model.
    /// </summary>
    private sealed class FixedCodeDecisionClient : IAgentDecisionClient
    {
        private readonly string _code;
        private readonly double _score;

        public FixedCodeDecisionClient(string code, double score)
        {
            _code = code;
            _score = score;
        }

        public string ProviderName => "test";

        public string Model => "test-model";

        public bool IsEnabled => true;

        public Task<IReadOnlyDictionary<string, AgentDecisionAnswer>> AskAsync(
            string state,
            IReadOnlyDictionary<string, AgentDecisionQuestion> questions,
            CancellationToken ct)
        {
            var answers = new Dictionary<string, AgentDecisionAnswer>(StringComparer.Ordinal);
            foreach (var pair in questions)
            {
                var criteria = pair.Value.Criteria;
                var answer = criteria is null || criteria.Count == 0
                    ? "10"
                    : criteria.ContainsKey(_code)
                        ? _code
                        : criteria.Keys.FirstOrDefault(key =>
                              _code.StartsWith(key + ".", StringComparison.OrdinalIgnoreCase))
                          ?? criteria.Keys.OrderBy(key => key, StringComparer.Ordinal).First();
                answers[pair.Key] = new AgentDecisionAnswer
                {
                    Type = "choice",
                    Probabilities = new Dictionary<string, double>(StringComparer.Ordinal)
                    {
                        [answer] = _score
                    }
                };
            }

            return Task.FromResult<IReadOnlyDictionary<string, AgentDecisionAnswer>>(answers);
        }
    }
}