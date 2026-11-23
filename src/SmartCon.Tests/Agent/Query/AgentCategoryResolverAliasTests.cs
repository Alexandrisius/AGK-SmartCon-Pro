using Xunit;
using SmartCon.Revit.Agent.Tools;

namespace SmartCon.Tests.Agent.Query;

/// <summary>
/// Pure alias matching of AgentCategoryResolver (C6-06/C10-04): enum-shaped
/// guesses like OST_Pipes must resolve to the category whose DISPLAY name is
/// "Pipes" (OST_PipeCurves); an ambiguous normalized match must be reported,
/// not silently picked. The document side is pinned by AgentCategoryResolver
/// integration tests.
/// </summary>
public sealed class AgentCategoryResolverAliasTests
{
    private static readonly string[] SampleDisplayNames =
    {
        "Pipes", "Walls", "Pipe Accessories", "Pipe Fittings", "Rooms"
    };

    [Fact]
    public void OstShapedGuess_MatchesUniqueDisplayName()
    {
        var match = AgentCategoryResolver.MatchAliasNames("OST_Pipes", SampleDisplayNames);

        Assert.Equal(AgentCategoryResolver.AliasMatchKind.Unique, match.Kind);
        Assert.Equal("Pipes", match.Matches[0]);
    }

    [Fact]
    public void OstPrefixUnderscoresAndSpaces_AreIgnoredOnBothSides()
    {
        var match = AgentCategoryResolver.MatchAliasNames(
            "OST_Pipe Accessories",
            new[] { "Pipe Accessories", "Walls" });

        Assert.Equal(AgentCategoryResolver.AliasMatchKind.Unique, match.Kind);
        Assert.Equal("Pipe Accessories", match.Matches[0]);
    }

    [Fact]
    public void EnumNameWithUnderscores_MatchesSpacedDisplayName()
    {
        var match = AgentCategoryResolver.MatchAliasNames(
            "OST_PipeAccessories",
            new[] { "Pipe Accessories", "Walls" });

        Assert.Equal(AgentCategoryResolver.AliasMatchKind.Unique, match.Kind);
        Assert.Equal("Pipe Accessories", match.Matches[0]);
    }

    [Fact]
    public void NoNormalizedMatch_IsNone()
    {
        var match = AgentCategoryResolver.MatchAliasNames("OST_Pipe", SampleDisplayNames);

        Assert.Equal(AgentCategoryResolver.AliasMatchKind.None, match.Kind);
    }

    [Fact]
    public void TwoNamesNormalizingEqually_AreAmbiguous()
    {
        var match = AgentCategoryResolver.MatchAliasNames(
            "OST_Air Terminals",
            new[] { "Walls", "Air Terminals", "Air_Terminals" });

        Assert.Equal(AgentCategoryResolver.AliasMatchKind.Ambiguous, match.Kind);
        Assert.Equal(2, match.Matches.Count);
        Assert.Contains("Air Terminals", match.Matches);
        Assert.Contains("Air_Terminals", match.Matches);
    }

    [Fact]
    public void ExactDisplayName_IsLeftToTheDirectMatch()
    {
        // The caller resolves exact display names before the alias step; the
        // alias matcher must not shadow that by returning the name itself.
        var match = AgentCategoryResolver.MatchAliasNames("Pipes", SampleDisplayNames);

        Assert.Equal(AgentCategoryResolver.AliasMatchKind.None, match.Kind);
    }

    [Fact]
    public void EmptySuffix_IsNone()
    {
        var match = AgentCategoryResolver.MatchAliasNames("OST_", SampleDisplayNames);

        Assert.Equal(AgentCategoryResolver.AliasMatchKind.None, match.Kind);
    }
}
