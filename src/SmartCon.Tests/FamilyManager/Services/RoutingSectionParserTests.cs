using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// <see cref="RoutingSectionParser"/> — file-free backfill of pre-V34
/// versions from stored canonical section strings (ADR-072, Phase 2b).
/// </summary>
public sealed class RoutingSectionParserTests
{
    [Fact]
    public void Parse_ManagerGroups_CriteriaNoPartAndEscapingRoundtrip()
    {
        var sections = new Dictionary<string, string>
        {
            ["ROUTING|Pipe A"] = "ROUTING|1|0|Seg-1|seg rule|PrimarySizeCriterion|0.04|0.49|1|Elbow%7CFam|first|3|NOPART|welded|",
            ["FAMKEY|Pipe A"] = "FAMKEY|Pipe.Types|",
        };

        var parsed = RoutingSectionParser.Parse(sections);

        var type = Assert.Single(parsed);
        Assert.Equal("Pipe A", type.TypeName);
        Assert.Equal("Pipe.Types", type.FamilyKey);
        Assert.Equal(1, type.Routing.PreferredJunctionType);
        Assert.Equal(3, type.Routing.Rules.Count);

        var seg = type.Routing.Rules[0];
        Assert.Equal(0, seg.GroupType);
        Assert.Null(seg.GroupKey);
        Assert.Equal("Seg-1", seg.PartName);
        Assert.Equal("seg rule", seg.Description);
        var criterion = Assert.Single(seg.Criteria);
        Assert.Equal("PrimarySizeCriterion", criterion.CriterionType);
        Assert.Equal(0.04, criterion.MinimumSize);
        Assert.Equal(0.49, criterion.MaximumSize);

        var elbow = type.Routing.Rules[1];
        Assert.Equal(1, elbow.GroupType);
        Assert.Equal("Elbow|Fam", elbow.PartName); // %7C unescaped
        Assert.Empty(elbow.Criteria);

        var cross = type.Routing.Rules[2];
        Assert.Equal(3, cross.GroupType);
        Assert.Null(cross.PartName); // NOPART marker → null
        Assert.Equal("welded", cross.Description);
    }

    [Fact]
    public void Parse_ParamGroup_StringKeyPreserved()
    {
        var sections = new Dictionary<string, string>
        {
            ["ROUTING|Flex A"] = "ROUTING|1|Param:RBS_CURVETYPE_DEFAULT_TEE_PARAM|TeeFam%25X:Std||Param:RBS_CURVETYPE_DEFAULT_UNION_PARAM|NOPART||",
        };

        var parsed = RoutingSectionParser.Parse(sections);

        var type = Assert.Single(parsed);
        Assert.Equal(string.Empty, type.FamilyKey); // no FAMKEY section
        Assert.Equal(2, type.Routing.Rules.Count);
        var tee = type.Routing.Rules[0];
        Assert.Equal(RoutingGroupKeys.ParamGroupType, tee.GroupType);
        Assert.Equal("Param:RBS_CURVETYPE_DEFAULT_TEE_PARAM", tee.GroupKey);
        Assert.Equal("TeeFam%X:Std", tee.PartName); // %25 unescaped
        Assert.Equal(string.Empty, tee.Description);
        var union = type.Routing.Rules[1];
        Assert.Null(union.PartName);
    }

    [Fact]
    public void Parse_EmptyRoutingSection_Skipped()
    {
        var sections = new Dictionary<string, string>
        {
            ["ROUTING|Wall A"] = "ROUTING|-|",
        };

        Assert.Empty(RoutingSectionParser.Parse(sections));
    }

    [Fact]
    public void Parse_TrailingMalformedToken_StopsInsteadOfInventingRules()
    {
        var sections = new Dictionary<string, string>
        {
            ["ROUTING|Pipe A"] = "ROUTING|1|0|Seg-1|good|garbage-tail",
        };

        var type = Assert.Single(RoutingSectionParser.Parse(sections));
        var rule = Assert.Single(type.Routing.Rules);
        Assert.Equal("Seg-1", rule.PartName);
    }
}
