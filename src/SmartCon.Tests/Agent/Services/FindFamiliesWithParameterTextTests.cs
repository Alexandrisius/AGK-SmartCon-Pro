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

    [Fact]
    public void Card_ExplainsWithFamilyAndProjectScopes()
    {
        // #535: 194 "families with KRTRS_" were project-level bindings, not family
        // parameters — a migration plan built on that count is wrong. The card must
        // tell the model that parameterScope separates family from project.
        Assert.Contains("parameterScope", Card.WhenToUse, StringComparison.Ordinal);
        Assert.Contains("project (проектная привязка", Card.WhenToUse, StringComparison.Ordinal);
        // Live follow-up: the strict family recommendation dropped family+project
        // names; the card must recommend family+ (family OR family+project).
        Assert.Contains("parameterScope=family+", Card.WhenToUse, StringComparison.Ordinal);
    }

    [Fact]
    public void Card_OffersRequireSharedAndByNameInventory()
    {
        // #535: name-based auto-replace corrupts same-name non-shared visibility
        // checkboxes; byParameterName[] is the per-name inventory the migration
        // plan is built on.
        Assert.Contains("requireShared=true", Card.WhenToUse, StringComparison.Ordinal);
        Assert.Contains("byParameterName[]", Card.WhenToUse, StringComparison.Ordinal);
    }

    [Fact]
    public void GetFamilyParameters_Card_TellsBatchValuesAreOffByDefault()
    {
        // #535: batch reads with values blew the tool timeout; the card must say
        // the batch defaults to metadata and where to read one parameter's values.
        var card = AgentToolCards.All.First(c => c.ToolName == "get_family_parameters");
        Assert.Contains("includeValues=true", card.Purpose, StringComparison.Ordinal);
        Assert.Contains("get_family_parameter_values", card.Purpose, StringComparison.Ordinal);
    }

    [Fact]
    public void GetFamilyParameterValues_Card_ExistsInFamiliesPack()
    {
        var card = AgentToolCards.All.First(c => c.ToolName == "get_family_parameter_values");
        Assert.Equal("families", card.Pack);
        Assert.True(card.TimeoutMs >= 60000);
        Assert.Contains("формул", card.Purpose, StringComparison.Ordinal);
    }
}
