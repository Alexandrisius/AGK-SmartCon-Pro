using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// #523: matchedParameterCount and matchedTypeCount answer different questions
/// (distinct parameter names vs type-level matches). A description that leaves
/// them ambiguous lets the model report the wrong number to the user.
/// The Revit tool description is pinned by an integration test — this project
/// has no RevitAPI runtime and cannot construct the tool.
/// </summary>
public sealed class FindFamiliesWithParameterTextTests
{
    private static AgentToolCard Card => AgentToolCards.All.First(
        card => card.ToolName == "find_families_with_parameter");

    [Fact]
    public void Card_DistinguishesParameterCountFromTypeCount()
    {
        Assert.Contains(
            "matchedParameterCount — число уникальных имён параметров",
            Card.Purpose,
            StringComparison.Ordinal);
        Assert.Contains(
            "matchedTypeCount — число совпадений на уровне типов",
            Card.Purpose,
            StringComparison.Ordinal);
    }
}