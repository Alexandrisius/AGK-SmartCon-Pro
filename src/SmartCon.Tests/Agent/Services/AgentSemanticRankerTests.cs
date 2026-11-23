using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

public sealed class AgentSemanticRankerTests
{
    private static SemanticCandidate Candidate(int index) =>
        new("id-" + index, "Строка " + index);

    private static AgentDecisionAnswer One(IReadOnlyDictionary<string, double> probabilities) =>
        new() { Type = "choice", Probabilities = probabilities };

    private static IReadOnlyDictionary<string, AgentDecisionAnswer> MatchAnswer(
        IReadOnlyDictionary<string, double> probabilities) =>
        new Dictionary<string, AgentDecisionAnswer>(StringComparer.Ordinal)
        {
            ["match"] = new AgentDecisionAnswer { Type = "choice", Probabilities = probabilities }
        };

    [Fact]
    public async Task Rank_SingleList_AsksOneChoiceAndRanksTopK()
    {
        var client = new ScriptedClient
        {
            Answer = (_, questions) => MatchAnswer(new Dictionary<string, double>
            {
                ["id-3"] = 0.9,
                ["id-1"] = 0.5,
                ["id-7"] = 0.7
            })
        };
        var ranker = new AgentSemanticRanker(client);

        var outcome = await ranker.RankAsync(
            "state", Enumerable.Range(1, 10).Select(Candidate).ToList(),
            "query", 2, CancellationToken.None);

        Assert.True(outcome.Ok);
        var request = Assert.Single(client.Requests);
        var question = Assert.Single(request.Questions.Values);
        Assert.Equal(10, question.Criteria!.Count);
        Assert.Equal(AgentSemanticRanker.MatchInstructions, question.Instructions);
        var matches = outcome.First;
        Assert.Equal("id-3", matches[0].Id);
        Assert.Equal(0.9, matches[0].Score, 5);
        Assert.Equal(1, matches[0].Rank);
        Assert.Equal("id-7", matches[1].Id);
        Assert.Equal(2, matches[1].Rank);
    }

    [Fact]
    public async Task Rank_MoreThan255Options_SplitsBatchesAndMergesByMax()
    {
        var client = new ScriptedClient
        {
            Answer = (_, questions) =>
            {
                var criteria = questions["match"].Criteria!;
                // The first batch scores id-3 low; the second batch (which also
                // contains id-3 via a duplicate entry) scores it higher - the
                // merge must keep the max of the two.
                return criteria.ContainsKey("id-256")
                    ? MatchAnswer(new Dictionary<string, double> { ["id-3"] = 0.6, ["id-256"] = 0.5 })
                    : MatchAnswer(new Dictionary<string, double> { ["id-3"] = 0.4 });
            }
        };
        var ranker = new AgentSemanticRanker(client);
        var candidates = Enumerable.Range(1, 260).Select(Candidate).ToList();
        candidates.Add(new SemanticCandidate("id-3", "Повтор в другом батче"));

        var outcome = await ranker.RankAsync("state", candidates, "q", 3, CancellationToken.None);

        Assert.True(outcome.Ok);
        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(255, client.Requests[0].Questions["match"].Criteria!.Count);
        Assert.Equal(6, client.Requests[1].Questions["match"].Criteria!.Count);
        var matches = outcome.First;
        Assert.Equal("id-3", matches[0].Id);
        Assert.Equal(0.6, matches[0].Score, 5);
        Assert.Equal("id-256", matches[1].Id);
        Assert.Equal(2, matches.Count);
    }

    [Fact]
    public async Task Rank_RefusedAnswer_IsTypedErrorNotEmptyTopK()
    {
        var client = new ScriptedClient
        {
            Answer = (_, _) => new Dictionary<string, AgentDecisionAnswer>(StringComparer.Ordinal)
            {
                ["match"] = new AgentDecisionAnswer { Type = "choice", Error = "refused" }
            }
        };
        var ranker = new AgentSemanticRanker(client);

        var outcome = await ranker.RankAsync(
            "state", new[] { Candidate(1) }, "q", 3, CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.Equal("decision_refused", outcome.Error);
        Assert.Empty(outcome.First);
    }

    [Fact]
    public async Task Rank_NullAnswerDictionary_DoesNotThrowAndFailsTyped()
    {
        var client = new ScriptedClient { NullAnswers = true };
        var ranker = new AgentSemanticRanker(client);

        var outcome = await ranker.RankAsync(
            "state", new[] { Candidate(1) }, "q", 3, CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.NotNull(outcome.Error);
        Assert.Empty(outcome.First);
    }

    [Fact]
    public async Task Rank_DisabledClient_ReturnsDecisionUnavailableWithoutCalls()
    {
        var client = new ScriptedClient { Enabled = false };
        var ranker = new AgentSemanticRanker(client);

        var outcome = await ranker.RankAsync(
            "state", new[] { Candidate(1) }, "q", 3, CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.Equal("decision_unavailable", outcome.Error);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task Rank_TransportFailure_IsTypedError()
    {
        var client = new ScriptedClient { Throw = new InvalidOperationException("down") };
        var ranker = new AgentSemanticRanker(client);

        var outcome = await ranker.RankAsync(
            "state", new[] { Candidate(1) }, "q", 3, CancellationToken.None);

        Assert.Equal("decision_call_failed", outcome.Error);
        Assert.Contains("down", outcome.ErrorDetail);
    }

    [Fact]
    public async Task RankQueries_ManyQueries_TravelInOneCallWithUniqueIds()
    {
        var client = new ScriptedClient
        {
            Answer = (_, questions) =>
            {
                var answers = new Dictionary<string, AgentDecisionAnswer>(StringComparer.Ordinal);
                foreach (var pair in questions)
                {
                    var best = pair.Value.Criteria!.Keys.First();
                    answers[pair.Key] = One(new Dictionary<string, double> { [best] = 0.8 });
                }

                return answers;
            }
        };
        var ranker = new AgentSemanticRanker(client);
        var queries = Enumerable.Range(0, 15)
            .Select(index => ("q" + index,
                (IReadOnlyList<SemanticCandidate>)new[] { Candidate(index), Candidate(100 + index) },
                "query " + index))
            .ToList();

        var outcome = await ranker.RankQueriesAsync("state", queries, 2, CancellationToken.None);

        Assert.True(outcome.Ok);
        var request = Assert.Single(client.Requests);
        Assert.Equal(15, request.Questions.Count);
        Assert.All(request.Questions.Keys, id => Assert.Matches("^q\\d+$", id));
        Assert.Equal(15, outcome.Matches.Count);
        for (var index = 0; index < 15; index++)
        {
            var match = Assert.Single(outcome.Matches[index]);
            Assert.Equal("id-" + index, match.Id);
            Assert.Equal(0.8, match.Score, 5);
        }
    }

    [Fact]
    public async Task RankQueries_MoreThan20Queries_SplitIntoCalls()
    {
        var client = new ScriptedClient
        {
            Answer = (_, questions) =>
            {
                var answers = new Dictionary<string, AgentDecisionAnswer>(StringComparer.Ordinal);
                foreach (var pair in questions)
                    answers[pair.Key] = One(new Dictionary<string, double> { [pair.Value.Criteria!.Keys.First()] = 0.9 });
                return answers;
            }
        };
        var ranker = new AgentSemanticRanker(client);
        var queries = Enumerable.Range(0, 25)
            .Select(index => ("q" + index,
                (IReadOnlyList<SemanticCandidate>)new[] { Candidate(index) }, "query"))
            .ToList();

        var outcome = await ranker.RankQueriesAsync("state", queries, 1, CancellationToken.None);

        Assert.True(outcome.Ok);
        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(20, client.Requests[0].Questions.Count);
        Assert.Equal(5, client.Requests[1].Questions.Count);
        Assert.Equal(25, outcome.Matches.Count);
        Assert.All(outcome.Matches, row => Assert.Single(row));
    }

    [Fact]
    public async Task RankQueries_CallFailure_MarksOnlyItsQueriesIncomplete()
    {
        var calls = 0;
        var client = new ScriptedClient
        {
            Answer = (_, questions) =>
            {
                var answers = new Dictionary<string, AgentDecisionAnswer>(StringComparer.Ordinal);
                foreach (var pair in questions)
                    answers[pair.Key] = One(new Dictionary<string, double> { [pair.Value.Criteria!.Keys.First()] = 0.9 });
                return answers;
            },
            BeforeAsk = () =>
            {
                calls++;
                if (calls == 2)
                    throw new InvalidOperationException("flaky");
            }
        };
        var ranker = new AgentSemanticRanker(client);
        var queries = Enumerable.Range(0, 25)
            .Select(index => ("q" + index,
                (IReadOnlyList<SemanticCandidate>)new[] { Candidate(index) }, "query"))
            .ToList();

        var outcome = await ranker.RankQueriesAsync("state", queries, 1, CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.Equal("decision_call_failed", outcome.Error);
        // The first call's queries were answered; the second call's five are incomplete.
        Assert.Equal(5, outcome.IncompleteQueryIds.Count);
        Assert.All(outcome.Matches.Take(20), row => Assert.Single(row));
        Assert.All(outcome.Matches.Skip(20), row => Assert.Empty(row));
    }

    private sealed class ScriptedClient : IAgentDecisionClient
    {
        public Func<string, IReadOnlyDictionary<string, AgentDecisionQuestion>, IReadOnlyDictionary<string, AgentDecisionAnswer>> Answer
        {
            get;
            set;
        } = (_, questions) =>
        {
            var answers = new Dictionary<string, AgentDecisionAnswer>(StringComparer.Ordinal);
            foreach (var pair in questions)
                answers[pair.Key] = new AgentDecisionAnswer { Type = "choice" };
            return answers;
        };

        public Action? BeforeAsk { get; set; }

        public bool Enabled { get; set; } = true;

        public Exception? Throw { get; set; }

        /// <summary>A broken client: AskAsync returns a null dictionary.</summary>
        public bool NullAnswers { get; set; }

        public List<(string State, IReadOnlyDictionary<string, AgentDecisionQuestion> Questions)> Requests
        {
            get;
        } = new();

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

            if (NullAnswers)
                return Task.FromResult<IReadOnlyDictionary<string, AgentDecisionAnswer>>(null!);

            Requests.Add((state, questions));
            return Task.FromResult(Answer(state, questions));
        }
    }
}
