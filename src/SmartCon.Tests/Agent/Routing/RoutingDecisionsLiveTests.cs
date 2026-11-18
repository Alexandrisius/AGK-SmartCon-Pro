using System.Diagnostics;
using System.Globalization;
using SmartCon.Agent.Services.Decisions;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using Xunit;
using Xunit.Abstractions;

namespace SmartCon.Tests.Agent.Routing;

/// <summary>
/// Opt-in H1-11 live decision reranker check: 20 real routing queries through
/// the real <see cref="HttpDecisionClient"/>. Skipped silently unless
/// SMARTCON_DECIDER_LIVE=1; the key comes from secrets.env, never from the test.
/// </summary>
public sealed class RoutingDecisionsLiveTests
{
    private const int SampleSize = 20;

    private readonly ITestOutputHelper _output;

    public RoutingDecisionsLiveTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task LiveDecisions_RealQueries_RecordsTop1Top5()
    {
        if (Environment.GetEnvironmentVariable("SMARTCON_DECIDER_LIVE") != "1")
            return;

        var settings = AgentDecisionsSettings.Load();
        Assert.True(
            settings.IsAvailable,
            "Set SMARTCON_DECIDER_ENABLED=1 and an API key (secrets.env) before a live run.");

        var all = RoutingFixtures.RealQueries();
        var queries = Enumerable.Range(0, SampleSize)
            .Select(index => all[index * all.Count / SampleSize])
            .ToList();
        var catalog = RoutingCatalogFactory.RealCatalog();

        using var client = new HttpDecisionClient(settings);
        var router = new DecisionsToolRouter(new RuleToolRouter(), client);

        var visible = 0;
        var top1 = 0;
        var top5 = 0;
        var durations = new List<double>(queries.Count);

        foreach (var query in queries)
        {
            var watch = Stopwatch.StartNew();
            var decision = await router.RouteAsync(query.Query, catalog, maxTools: 14);
            watch.Stop();
            durations.Add(watch.Elapsed.TotalMilliseconds);

            if (query.ExpectedTools.Any(name =>
                    decision.Tools.Any(tool => tool.Name == name)))
                visible++;

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

        var sorted = durations.OrderBy(value => value).ToList();
        var p95 = sorted[(int)Math.Ceiling(sorted.Count * 0.95) - 1];

        _output.WriteLine(
            "live decisions: model=" + settings.Model + " provider=" + settings.Provider +
            " queries=" + queries.Count + " visible=" + visible + " top1(non-core)=" + top1 +
            " top5(non-core)=" + top5 + " p95=" +
            p95.ToString("F0", CultureInfo.InvariantCulture) + "ms");

        Assert.Equal(queries.Count, visible);
    }
}
