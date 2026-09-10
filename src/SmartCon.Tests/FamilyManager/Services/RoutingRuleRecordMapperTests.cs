using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// <see cref="RoutingRuleRecordMapper"/> roundtrip tests (ADR-072, V34):
/// snapshot → storage records → snapshot preserves the canonical ROUTING
/// content (group identity, in-group order, criteria, no-part rules,
/// per-type settings) for both manager- and parameter-based groups.
/// </summary>
public sealed class RoutingRuleRecordMapperTests
{
    [Fact]
    public void Roundtrip_ManagerGroups_PreservesOrderCriteriaAndSettings()
    {
        var type = new SystemTypeSnapshot(
            "Pipe A",
            [],
            Routing: new RoutingPreferencesSnapshot(1,
            [
                new RoutingRuleSnapshot(0, "Seg-1", "seg rule",
                    [new RoutingCriterionSnapshot("PrimarySizeCriterion", 0.04, 0.49)]),
                new RoutingRuleSnapshot(1, "ElbowFam:DN50", "first", []),
                new RoutingRuleSnapshot(1, "ElbowFam:DN80", "second", []),
                new RoutingRuleSnapshot(3, null, "welded", []),
            ]),
            FamilyKey: "Pipe.Types");

        var rules = new List<FamilyRoutingRuleInfo>();
        var settings = new List<FamilyRoutingTypeSettings>();
        RoutingRuleRecordMapper.ToRecords(type, rules, settings);

        Assert.Equal(4, rules.Count);
        var setting = Assert.Single(settings);
        Assert.Equal(1, setting.PreferredJunctionType);
        Assert.Equal("Pipe.Types", setting.FamilyKey);

        // In-group zero-based order: Elbows rules get 0 and 1.
        Assert.Equal(0, rules[1].RuleOrder);
        Assert.Equal(1, rules[2].RuleOrder);
        Assert.Equal("Elbows", rules[1].GroupKey);
        Assert.Equal("Segments", rules[0].GroupKey);

        var restored = RoutingRuleRecordMapper.ToSnapshot("Pipe A", "Pipe.Types", rules, settings);
        Assert.NotNull(restored);
        Assert.Equal(1, restored!.PreferredJunctionType);
        Assert.Equal(type.Routing!.Rules, restored.Rules);
    }

    [Fact]
    public void Roundtrip_ParamGroups_PreservesStringKeysAndSortsAfterManager()
    {
        var type = new SystemTypeSnapshot(
            "Flex A",
            [],
            Routing: new RoutingPreferencesSnapshot(1,
            [
                new RoutingRuleSnapshot(RoutingGroupKeys.ParamGroupType, "TeeFam:Std", string.Empty, [],
                    GroupKey: RoutingGroupKeys.ForParam("RBS_CURVETYPE_DEFAULT_TEE_PARAM")),
                new RoutingRuleSnapshot(RoutingGroupKeys.ParamGroupType, null, string.Empty, [],
                    GroupKey: RoutingGroupKeys.ForParam("RBS_CURVETYPE_DEFAULT_UNION_PARAM")),
            ]),
            FamilyKey: "FlexPipe.Round");

        var rules = new List<FamilyRoutingRuleInfo>();
        var settings = new List<FamilyRoutingTypeSettings>();
        RoutingRuleRecordMapper.ToRecords(type, rules, settings);

        var restored = RoutingRuleRecordMapper.ToSnapshot("Flex A", "FlexPipe.Round", rules, settings);
        Assert.NotNull(restored);
        Assert.Equal(type.Routing!.Rules, restored!.Rules);
        Assert.All(restored.Rules, r => Assert.Equal(RoutingGroupKeys.ParamGroupType, r.GroupType));
    }

    [Fact]
    public void ToRecords_NullRouting_WritesNothing()
    {
        var type = new SystemTypeSnapshot("Wall A", [], FamilyKey: "Wall.Basic");

        var rules = new List<FamilyRoutingRuleInfo>();
        var settings = new List<FamilyRoutingTypeSettings>();
        RoutingRuleRecordMapper.ToRecords(type, rules, settings);

        Assert.Empty(rules);
        Assert.Empty(settings);
    }

    [Fact]
    public void ToSnapshot_NoSettingsRow_ReturnsNull()
    {
        var restored = RoutingRuleRecordMapper.ToSnapshot(
            "Pipe A", "Pipe.Types",
            [new FamilyRoutingRuleInfo("Pipe A", "Pipe.Types", "Elbows", 0, "E:S", "", [])],
            []);
        Assert.Null(restored);
    }

    [Fact]
    public void ToSnapshot_OtherTypesRows_AreIgnored()
    {
        var rules = new List<FamilyRoutingRuleInfo>
        {
            new("Pipe A", "Pipe.Types", "Elbows", 0, "E:S", "", []),
            new("Pipe B", "Pipe.Types", "Elbows", 0, "X:Y", "", []),
        };
        var settings = new List<FamilyRoutingTypeSettings>
        {
            new("Pipe A", "Pipe.Types", 1),
            new("Pipe B", "Pipe.Types", 2),
        };

        var restored = RoutingRuleRecordMapper.ToSnapshot("Pipe A", "Pipe.Types", rules, settings);

        Assert.NotNull(restored);
        Assert.Equal(1, restored!.PreferredJunctionType);
        var rule = Assert.Single(restored.Rules);
        Assert.Equal("E:S", rule.PartName);
    }

    [Fact]
    public void GroupSortKey_ManagerGroupsKeepEnumOrder_ParamGroupsLast()
    {
        var type = new SystemTypeSnapshot(
            "Duct A",
            [],
            Routing: new RoutingPreferencesSnapshot(0,
            [
                new RoutingRuleSnapshot(3, "CrossFam:Std", string.Empty, []),
                new RoutingRuleSnapshot(1, "ElbowFam:Std", string.Empty, []),
                new RoutingRuleSnapshot(RoutingGroupKeys.ParamGroupType, "T:U", string.Empty, [],
                    GroupKey: RoutingGroupKeys.ForParam("RBS_CURVETYPE_DEFAULT_TEE_PARAM")),
            ]),
            FamilyKey: "Duct.Rectangular");

        var rules = new List<FamilyRoutingRuleInfo>();
        var settings = new List<FamilyRoutingTypeSettings>();
        RoutingRuleRecordMapper.ToRecords(type, rules, settings);

        var restored = RoutingRuleRecordMapper.ToSnapshot("Duct A", "Duct.Rectangular", rules, settings);

        Assert.NotNull(restored);
        // Canonical order: manager groups by enum ordinal (Elbows=1 before
        // Crosses=3), parameter groups last.
        Assert.Equal(1, restored!.Rules[0].GroupType);
        Assert.Equal(3, restored.Rules[1].GroupType);
        Assert.Equal(RoutingGroupKeys.ParamGroupType, restored.Rules[2].GroupType);
    }
}
