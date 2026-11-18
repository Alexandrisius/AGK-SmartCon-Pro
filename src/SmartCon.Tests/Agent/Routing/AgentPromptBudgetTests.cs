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
        var catalog = RoutingCatalogFactory.RealCatalog();

        var packs = new StringBuilder();
        foreach (var pack in AgentToolPacks.All)
            packs.Append("- ").Append(pack.Name).Append(": ").AppendLine(pack.Description);

        var core = new StringBuilder();
        foreach (var name in AgentToolPacks.CoreToolNames)
        {
            Assert.True(AgentToolCards.TryGet(name, out var card));
            core.Append(name).Append(": ").Append(card!.Purpose).AppendLine();
            core.Append("Когда: ").AppendLine(card.WhenToUse);
        }

        var top = ToolCardRouter.Rank(
            "переименуй виды и примени шаблон", catalog, 5);
        Assert.Equal(5, top.Count);

        var schemas = new StringBuilder();
        foreach (var tool in top)
        {
            schemas.Append(tool.Name).Append('\n');
            schemas.AppendLine(RoutingCatalogFactory.RepresentativeSchema());
        }

        var packTokens = AgentTokenEstimator.EstimateText(packs.ToString());
        var coreTokens = AgentTokenEstimator.EstimateText(core.ToString());
        var schemaTokens = AgentTokenEstimator.EstimateText(schemas.ToString());
        var staticPrompt = AgentSystemPrompt.BuildStatic(AgentApprovalMode.ConfirmWrites);
        var staticTokens = AgentTokenEstimator.EstimateText(staticPrompt);
        var parts = packTokens + coreTokens + schemaTokens;

        _output.WriteLine(
            "packs=" + packTokens + " core9=" + coreTokens + " top5Schemas=" + schemaTokens +
            " partsTotal=" + parts + " staticPrompt=" + staticTokens +
            " full=" + (parts + staticTokens));
        _output.WriteLine(
            "packs=" + AgentToolPacks.All.Count + " core=" +
            AgentToolPacks.CoreToolNames.Count + " schemas=" + top.Count);

        Assert.Equal(20, AgentToolPacks.All.Count);
        Assert.Equal(9, AgentToolPacks.CoreToolNames.Count);
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
