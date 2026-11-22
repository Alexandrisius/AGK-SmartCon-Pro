using System.Globalization;
using System.Text;
using SmartCon.Agent.Services;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using Xunit;
using Xunit.Abstractions;

namespace SmartCon.Tests.Agent.Routing;

/// <summary>
/// H1-12 prompt budget: what the model actually sees before any pack is loaded
/// (20 pack lines + 9 core cards + top-5 representative schemas) must stay under
/// ~8k tokens. Production input schemas live in SmartCon.Agent, so the schema
/// part is measured on a representative ~800-char schema, not on the real ones.
/// </summary>
public sealed class AgentPromptBudgetTests
{
    private const int PromptTokenGate = 8000;

    private readonly ITestOutputHelper _output;

    public AgentPromptBudgetTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void PromptWithPacks_StaysUnderEightThousandTokens()
    {
        var packs = new StringBuilder();
        foreach (var pack in AgentToolPacks.All)
            packs.Append("- ").Append(pack.Name).Append(": ").AppendLine(pack.Description);

        var core = new StringBuilder();
        // The request always carries the research core and the system plumbing:
        // count both, otherwise the gate under-measures the real prompt.
        foreach (var name in AgentToolPacks.CoreToolNames
                     .Concat(AgentToolPacks.SystemToolNames))
        {
            if (!AgentToolCards.TryGet(name, out var card))
            {
                // The pack loader is always available and described by the prompt
                // section instead of a routing card.
                Assert.Equal(AgentToolPacks.LoadToolPackName, name);
                continue;
            }

            core.Append(name).Append(": ").Append(card!.Purpose).AppendLine();
            core.Append("Когда: ").AppendLine(card.WhenToUse);
        }

        // The runtime no longer retrieves a shortlist on turn start: the request carries
        // exactly the always-available tools (research core + system plumbing) with
        // their real schemas. Estimate those, not a routing sample.
        var alwaysAvailable = AgentToolPacks.CoreToolNames
            .Concat(AgentToolPacks.SystemToolNames)
            .ToList();

        var catalog = RoutingCatalogFactory.RealCatalog();
        var byName = catalog.ToDictionary(tool => tool.Name, StringComparer.Ordinal);

        var schemas = new StringBuilder();
        foreach (var name in alwaysAvailable)
        {
            schemas.Append(name).Append('\n');
            schemas.AppendLine(byName.TryGetValue(name, out var definition)
                ? definition.InputSchemaJson
                : RoutingCatalogFactory.RepresentativeSchema());
        }

        var packTokens = AgentTokenEstimator.EstimateText(packs.ToString());
        var coreTokens = AgentTokenEstimator.EstimateText(core.ToString());
        var schemaTokens = AgentTokenEstimator.EstimateText(schemas.ToString());
        var staticPrompt = AgentSystemPrompt.BuildStatic();
        var staticTokens = AgentTokenEstimator.EstimateText(staticPrompt);
        var parts = packTokens + coreTokens + schemaTokens;

        _output.WriteLine(
            "packs=" + packTokens + " core=" + coreTokens + " alwaysSchemas=" + schemaTokens +
            " partsTotal=" + parts + " staticPrompt=" + staticTokens +
            " full=" + (parts + staticTokens));
        _output.WriteLine(
            "packs=" + AgentToolPacks.All.Count + " core=" +
            AgentToolPacks.CoreToolNames.Count + " schemas=" + alwaysAvailable.Count);

        Assert.Equal(24, AgentToolPacks.All.Count);
        Assert.Equal(11, AgentToolPacks.CoreToolNames.Count);
        Assert.Equal(3, AgentToolPacks.SystemToolNames.Count);
        Assert.True(
            parts <= PromptTokenGate,
            "packs+core+schemas " + parts.ToString(CultureInfo.InvariantCulture) +
            " tokens > " + PromptTokenGate);
        Assert.True(
            parts + staticTokens <= PromptTokenGate,
            "static+packs+core+schemas " + (parts + staticTokens).ToString(CultureInfo.InvariantCulture) +
            " tokens > " + PromptTokenGate);
    }
}
