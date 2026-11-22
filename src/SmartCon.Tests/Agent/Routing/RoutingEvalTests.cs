using System.Diagnostics;
using System.Globalization;
using SmartCon.Agent.Services.Decisions;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;
using Xunit.Abstractions;

namespace SmartCon.Tests.Agent.Routing;

/// <summary>
/// H1-11 routing eval: real Russian BIM intents on the real 43-card catalog
/// (BM25 top-1/top-5/false-tool/p95) and 100 synthetic intents on a generated
/// 1000-card catalog (top-5 recall). Decisions mode is exercised deterministically
/// through the production <see cref="DecisionsToolRouter"/> plumbing.
/// </summary>
public sealed class RoutingEvalTests
{
    private const int RealQueryCount = 383;
    private const int SyntheticQueryCount = 100;
    private const double Top1Gate = 0.80;
    private const double Top5Gate = 0.95;
    private const double P95GateMilliseconds = 100.0;

    private readonly ITestOutputHelper _output;

    public RoutingEvalTests(ITestOutputHelper output) => _output = output;

    private static readonly IReadOnlyList<AgentToolDefinition> RealCatalog =
        RoutingCatalogFactory.RealCatalog();

    [Fact]
    public void Fixtures_MatchRoutingContract()
    {
        var real = RoutingFixtures.RealQueries();
        var synthetic = RoutingFixtures.SyntheticQueries();

        Assert.Equal(RealQueryCount, real.Count);
        Assert.Equal(SyntheticQueryCount, synthetic.Count);
        Assert.Equal(132, RealCatalog.Count);

        var realNames = RealCatalog.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var query in real)
        {
            Assert.False(string.IsNullOrWhiteSpace(query.Id));
            Assert.False(string.IsNullOrWhiteSpace(query.Query));
            Assert.InRange(query.ExpectedTools.Count, 1, 3);
            Assert.Contains(query.Kind, new[] { "read", "write", "meta" });
            Assert.All(query.ExpectedTools, name => Assert.Contains(name, realNames));
        }

        foreach (var tool in RealCatalog)
        {
            var covered = real.Count(query =>
                query.ExpectedTools.Contains(tool.Name, StringComparer.Ordinal));
            Assert.True(
                covered >= 2,
                "Tool " + tool.Name + " is covered by only " + covered + " real queries.");
        }

        var syntheticCatalog = RoutingCatalogFactory.SyntheticCatalog();
        Assert.Equal(RoutingCatalogFactory.SyntheticCardCount, syntheticCatalog.Count);
        Assert.Equal(RoutingCatalogFactory.Packs, syntheticCatalog
            .GroupBy(tool => tool.Pack, StringComparer.Ordinal)
            .Count());
        Assert.All(
            syntheticCatalog.GroupBy(tool => tool.Pack, StringComparer.Ordinal),
            group => Assert.Equal(RoutingCatalogFactory.CardsPerPack, group.Count()));

        var syntheticNames = syntheticCatalog.Select(tool => tool.Name)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var query in synthetic)
        {
            Assert.NotNull(query.Token);
            Assert.Single(query.ExpectedTools);
            Assert.Contains(query.ExpectedTools[0], syntheticNames);

            var tokenMatches = syntheticCatalog.Count(tool =>
                tool.Purpose.Contains(query.Token!, StringComparison.Ordinal) ||
                tool.Synonyms.Contains(query.Token!, StringComparer.Ordinal));
            Assert.Equal(1, tokenMatches);
        }
    }

    [Fact]
    public void RulesMode_RealQueries_MeetsGates()
    {
        var queries = RoutingFixtures.RealQueries();
        var risks = RealCatalog.ToDictionary(
            tool => tool.Name, tool => tool.Risk, StringComparer.Ordinal);

        for (var index = 0; index < 10 && index < queries.Count; index++)
            ToolCardRouter.Rank(queries[index].Query, RealCatalog, 5);

        var durations = new List<double>(queries.Count);
        var misses = new List<string>();
        var top1Misses = new List<string>();
        var top1 = 0;
        var top5 = 0;
        var top14 = 0;
        var falseTool = 0;
        var empty = 0;

        foreach (var query in queries)
        {
            var watch = Stopwatch.StartNew();
            var ranked = ToolCardRouter.Rank(query.Query, RealCatalog, 14);
            watch.Stop();
            durations.Add(watch.Elapsed.TotalMilliseconds);

            var names = ranked.Select(tool => tool.Name).ToList();
            if (names.Count == 0)
                empty++;

            var hitTop1 = names.Count > 0 &&
                          query.ExpectedTools.Contains(names[0], StringComparer.Ordinal);
            var hitTop5 = names.Take(5)
                .Any(name => query.ExpectedTools.Contains(name, StringComparer.Ordinal));

            if (hitTop1)
                top1++;
            else
                top1Misses.Add(query.Id + " [" + string.Join("|", query.ExpectedTools) + "] got " +
                               string.Join("|", names));
            if (hitTop5)
                top5++;
            else
                misses.Add(query.Id + " [" + string.Join("|", query.ExpectedTools) + "] got " +
                           string.Join("|", names));
            if (names.Take(14)
                .Any(name => query.ExpectedTools.Contains(name, StringComparer.Ordinal)))
                top14++;

            var allExpectedRead = query.ExpectedTools.All(name => risks[name] == AgentToolRisk.Read);
            if (names.Count > 0 && allExpectedRead && risks[names[0]] != AgentToolRisk.Read)
                falseTool++;
        }

        var top1Rate = top1 / (double)queries.Count;
        var top5Rate = top5 / (double)queries.Count;
        var top14Rate = top14 / (double)queries.Count;
        var falseToolRate = falseTool / (double)queries.Count;
        var p95 = Percentile(durations, 0.95);

        _output.WriteLine("rules real: top1=" + Format(top1Rate) + " top5=" + Format(top5Rate) +
                          " top14=" + Format(top14Rate) + " falseTool=" +
                          Format(falseToolRate) + " p95=" +
                          p95.ToString("F2", CultureInfo.InvariantCulture) + "ms empty=" + empty);
        foreach (var miss in top1Misses)
            _output.WriteLine("top1-miss " + miss);
        foreach (var miss in misses)
            _output.WriteLine("top5-miss " + miss);

        Assert.True(
            top1Rate >= Top1Gate,
            "top-1 " + Format(top1Rate) + " < " + Format(Top1Gate) + "; misses: " +
            string.Join("; ", misses));
        Assert.True(
            top5Rate >= Top5Gate,
            "top-5 recall " + Format(top5Rate) + " < " + Format(Top5Gate) + "; misses: " +
            string.Join("; ", misses));
        // ADR-075 §3: BM25 top-14 recall below 95 % is the forced-vector trigger.
        Assert.True(
            top14Rate >= Top5Gate,
            "top-14 recall " + Format(top14Rate) + " < " + Format(Top5Gate));
        Assert.True(
            p95 < P95GateMilliseconds,
            "p95 " + p95.ToString("F2", CultureInfo.InvariantCulture) + "ms >= 100ms");
        Assert.Equal(0, empty);
    }

    [Fact]
    public async Task DecisionsMode_RealQueries_FixedChoicePlumbing()
    {
        var queries = RoutingFixtures.RealQueries();
        var visible = 0;
        var top1 = 0;
        var top5 = 0;

        foreach (var query in queries)
        {
            var expectedFirst = query.ExpectedTools[0];
            var client = new ExpectedChoiceClient(expectedFirst);
            var router = new DecisionsToolRouter(new RuleToolRouter(), client);

            var decision = await router.RouteAsync(query.Query, RealCatalog, maxTools: 14);

            Assert.Contains(decision.Tools, tool => tool.Name == expectedFirst);
            if (decision.Tools.Any(tool => tool.Name == expectedFirst))
                visible++;

            // ADR-075 §2 pins the core read tools first. The routing signal of the
            // decision layer is measured on non-core slots; expected core tools are
            // kept because they are legitimate hits.
            var relevant = decision.Tools
                .Select(tool => tool.Name)
                .Where(name => query.ExpectedTools.Contains(name, StringComparer.Ordinal) ||
                               !RuleToolRouter.CoreToolNames.Contains(name))
                .ToList();

            if (relevant.Count > 0 &&
                query.ExpectedTools.Contains(relevant[0], StringComparer.Ordinal))
                top1++;
            if (relevant.Take(5)
                .Any(name => query.ExpectedTools.Contains(name, StringComparer.Ordinal)))
                top5++;
        }

        _output.WriteLine("decisions fake: visible=" + visible + "/" + queries.Count +
                          " top1(non-core)=" + top1 + " top5(non-core)=" + top5);

        Assert.Equal(queries.Count, visible);
        Assert.Equal(queries.Count, top1);
        Assert.Equal(queries.Count, top5);
    }

    [Fact]
    public async Task DecisionsMode_ClientFailure_FallsBackToRules()
    {
        const string query = "Переименуй вид 101 в «План 1 этажа»";
        var ruleDecision = await new RuleToolRouter().RouteAsync(query, RealCatalog, 14);
        var expected = ruleDecision.Tools.Select(tool => tool.Name).ToList();

        var client = new StubDecisionClient { Throw = true };
        var router = new DecisionsToolRouter(new RuleToolRouter(), client);
        var decision = await router.RouteAsync(query, RealCatalog, 14);

        Assert.Equal(expected, decision.Tools.Select(tool => tool.Name).ToList());
    }

    [Fact]
    public void Synthetic1000Catalog_Top5Recall_MeetsGate()
    {
        var catalog = RoutingCatalogFactory.SyntheticCatalog();
        var queries = RoutingFixtures.SyntheticQueries();
        var durations = new List<double>(queries.Count);
        var misses = new List<string>();
        var top1 = 0;
        var top5 = 0;

        for (var index = 0; index < 10 && index < queries.Count; index++)
            ToolCardRouter.Rank(queries[index].Query, catalog, 5);

        foreach (var query in queries)
        {
            var watch = Stopwatch.StartNew();
            var ranked = ToolCardRouter.Rank(query.Query, catalog, 5);
            watch.Stop();
            durations.Add(watch.Elapsed.TotalMilliseconds);

            var names = ranked.Select(tool => tool.Name).ToList();
            if (names.Count > 0 && names[0] == query.ExpectedTools[0])
                top1++;
            if (names.Take(5).Contains(query.ExpectedTools[0], StringComparer.Ordinal))
                top5++;
            else
                misses.Add(query.Id + " [" + query.ExpectedTools[0] + "] got " +
                           string.Join("|", names));
        }

        var top1Rate = top1 / (double)queries.Count;
        var top5Rate = top5 / (double)queries.Count;
        var p95 = Percentile(durations, 0.95);

        _output.WriteLine("synthetic 1000: top1=" + Format(top1Rate) + " top5=" +
                          Format(top5Rate) + " p95=" +
                          p95.ToString("F2", CultureInfo.InvariantCulture) + "ms");
        foreach (var miss in misses)
            _output.WriteLine("miss " + miss);

        Assert.True(
            top5Rate >= Top5Gate,
            "synthetic top-5 recall " + Format(top5Rate) + " < " + Format(Top5Gate) +
            "; misses: " + string.Join("; ", misses));
    }

    private static double Percentile(List<double> values, double percentile)
    {
        var sorted = values.OrderBy(value => value).ToList();
        var index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[Math.Max(0, Math.Min(index, sorted.Count - 1))];
    }

    private static string Format(double rate) =>
        (rate * 100).ToString("F1", CultureInfo.InvariantCulture) + "%";

    private sealed class ExpectedChoiceClient : IAgentDecisionClient
    {
        private readonly string _expected;

        public ExpectedChoiceClient(string expected) => _expected = expected;

        public string ProviderName => "fake";

        public string Model => "fixed-choice";

        public bool IsEnabled => true;

        public Task<IReadOnlyDictionary<string, AgentDecisionAnswer>> AskAsync(
            string state,
            IReadOnlyDictionary<string, AgentDecisionQuestion> questions,
            CancellationToken ct)
        {
            var answers = new Dictionary<string, AgentDecisionAnswer>(StringComparer.Ordinal)
            {
                ["catalog_match"] = new AgentDecisionAnswer { Type = "noul", Noul = 0.99 },
                ["tool"] = new AgentDecisionAnswer
                {
                    Type = "choice",
                    Choice = _expected,
                    Confidence = 0.9
                }
            };

            return Task.FromResult<IReadOnlyDictionary<string, AgentDecisionAnswer>>(answers);
        }
    }
}
