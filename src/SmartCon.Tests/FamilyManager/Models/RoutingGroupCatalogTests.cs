using SmartCon.Core.Models.FamilyManager;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Models;

/// <summary>
/// ADR-072 Phase 3: the routing editor's per-category group matrix and
/// part filters — the UI offers exactly the groups the Revit routing
/// dialog shows (ADR-072 §2.7 storage matrix), and every editable group's
/// picker is constrained by fitting category + part_type (param.Set /
/// AddRule reject incompatible parts — the filter is mandatory).
/// </summary>
public class RoutingGroupCatalogTests
{
    [Theory]
    [InlineData(RoutingGroupCatalog.PipeCurvesCategoryId, true)]
    [InlineData(RoutingGroupCatalog.FlexPipeCurvesCategoryId, true)]
    [InlineData(RoutingGroupCatalog.DuctCurvesCategoryId, true)]
    [InlineData(RoutingGroupCatalog.FlexDuctCurvesCategoryId, true)]
    [InlineData(RoutingGroupCatalog.ConduitCategoryId, true)]
    [InlineData(RoutingGroupCatalog.CableTrayCategoryId, true)]
    [InlineData(-2000011, false)] // OST_Walls
    [InlineData(RoutingGroupCatalog.PipeFittingCategoryId, false)]
    [InlineData(null, false)]
    public void IsMepCurveCategory_MatchesTheSixSupportedCategories(int? categoryId, bool expected)
        => Assert.Equal(expected, RoutingGroupCatalog.IsMepCurveCategory(categoryId));

    [Fact]
    public void PipeGroups_MirrorRevitDialog_WithReadOnlySegments()
    {
        var groups = RoutingGroupCatalog.GetGroups(RoutingGroupCatalog.PipeCurvesCategoryId);

        Assert.Equal(
            ["Segments", "Elbows", "Junctions", "Crosses", "Transitions", "Unions", "MechanicalJoints", "Caps"],
            groups.Select(g => g.GroupKey));

        var segments = groups[0];
        Assert.True(segments.IsReadOnly);
        Assert.False(segments.AllowMultipleRules);
        Assert.False(segments.HasCriteria);

        foreach (var group in groups.Skip(1))
        {
            Assert.True(group.AllowMultipleRules);
            Assert.True(group.HasCriteria);
            Assert.Equal(RoutingGroupCatalog.PipeFittingCategoryId, group.FittingCategoryId);
            Assert.NotEmpty(group.PartTypeOrdinals);
        }
    }

    [Fact]
    public void DuctGroups_IncludeMultiShapeTransitions_ButNoMechanicalJoints()
    {
        var groups = RoutingGroupCatalog.GetGroups(RoutingGroupCatalog.DuctCurvesCategoryId);

        Assert.Contains("TransitionsRectangularToRound", groups.Select(g => g.GroupKey));
        Assert.Contains("TransitionsRectangularToOval", groups.Select(g => g.GroupKey));
        Assert.Contains("TransitionsOvalToRound", groups.Select(g => g.GroupKey));
        Assert.DoesNotContain("MechanicalJoints", groups.Select(g => g.GroupKey));
        Assert.All(groups.Skip(1), g =>
            Assert.Equal(RoutingGroupCatalog.DuctFittingCategoryId, g.FittingCategoryId));
    }

    [Fact]
    public void FlexDuctGroups_AreSingleValueParamRows()
    {
        var groups = RoutingGroupCatalog.GetGroups(RoutingGroupCatalog.FlexDuctCurvesCategoryId);

        Assert.All(groups, g =>
        {
            Assert.Null(g.ManagerGroupType);
            Assert.True(RoutingGroupKeys.IsParamGroup(g.GroupKey));
            Assert.False(g.AllowMultipleRules);
            Assert.False(g.HasCriteria);
            Assert.False(g.IsReadOnly);
        });
        Assert.Contains(
            RoutingGroupKeys.ForParam("RBS_CURVETYPE_MULTISHAPE_TRANSITION_PARAM"),
            groups.Select(g => g.GroupKey));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CableTrayGroups_TeeAndCrossOnlyWithFittings(bool withFittings)
    {
        var groups = RoutingGroupCatalog.GetGroups(RoutingGroupCatalog.CableTrayCategoryId, withFittings);
        var keys = groups.Select(g => g.GroupKey).ToList();

        Assert.Equal(withFittings,
            keys.Contains(RoutingGroupKeys.ForParam("RBS_CURVETYPE_DEFAULT_TEE_PARAM")));
        Assert.Equal(withFittings,
            keys.Contains(RoutingGroupKeys.ForParam("RBS_CURVETYPE_DEFAULT_CROSS_PARAM")));
        Assert.Contains(RoutingGroupKeys.ForParam("RBS_CURVETYPE_DEFAULT_HORIZONTAL_BEND_PARAM"), keys);
        Assert.Contains(RoutingGroupKeys.ForParam("RBS_CURVETYPE_DEFAULT_ELBOWUP_PARAM"), keys);
        Assert.Contains(RoutingGroupKeys.ForParam("RBS_CURVETYPE_DEFAULT_ELBOWDOWN_PARAM"), keys);
        Assert.All(groups, g =>
            Assert.Equal(RoutingGroupCatalog.CableTrayFittingCategoryId, g.FittingCategoryId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConduitGroups_TeeAndCrossOnlyWithFittings(bool withFittings)
    {
        var groups = RoutingGroupCatalog.GetGroups(RoutingGroupCatalog.ConduitCategoryId, withFittings);
        var keys = groups.Select(g => g.GroupKey).ToList();

        Assert.Equal(withFittings,
            keys.Contains(RoutingGroupKeys.ForParam("RBS_CURVETYPE_DEFAULT_TEE_PARAM")));
        Assert.Contains(RoutingGroupKeys.ForParam("RBS_CURVETYPE_DEFAULT_BEND_PARAM"), keys);
    }

    [Theory]
    [InlineData(RoutingGroupCatalog.PipeCurvesCategoryId, RoutingGroupCatalog.PipeFittingCategoryId)]
    [InlineData(RoutingGroupCatalog.FlexPipeCurvesCategoryId, RoutingGroupCatalog.PipeFittingCategoryId)]
    [InlineData(RoutingGroupCatalog.DuctCurvesCategoryId, RoutingGroupCatalog.DuctFittingCategoryId)]
    [InlineData(RoutingGroupCatalog.FlexDuctCurvesCategoryId, RoutingGroupCatalog.DuctFittingCategoryId)]
    [InlineData(RoutingGroupCatalog.ConduitCategoryId, RoutingGroupCatalog.ConduitFittingCategoryId)]
    [InlineData(RoutingGroupCatalog.CableTrayCategoryId, RoutingGroupCatalog.CableTrayFittingCategoryId)]
    public void FittingCategoryOf_MapsHostToFittingCategory(int host, int expected)
        => Assert.Equal(expected, RoutingGroupCatalog.FittingCategoryOf(host));

    [Theory]
    [InlineData(RoutingGroupCatalog.PipeCurvesCategoryId, true)]
    [InlineData(RoutingGroupCatalog.FlexPipeCurvesCategoryId, true)]
    [InlineData(RoutingGroupCatalog.DuctCurvesCategoryId, true)]
    [InlineData(RoutingGroupCatalog.FlexDuctCurvesCategoryId, true)]
    [InlineData(RoutingGroupCatalog.ConduitCategoryId, false)]
    [InlineData(RoutingGroupCatalog.CableTrayCategoryId, false)]
    public void HasPreferredJunction_OnlyPipeAndDuctFamilies(int categoryId, bool expected)
        => Assert.Equal(expected, RoutingGroupCatalog.HasPreferredJunction(categoryId));

    [Fact]
    public void JunctionGroup_AcceptsTeeTapAndFlangeParts()
    {
        var junctions = RoutingGroupCatalog.GetGroups(RoutingGroupCatalog.PipeCurvesCategoryId)
            .First(g => g.GroupKey == "Junctions");

        // Tee=6, Tap-Perpendicular=10, Tap-Adjustable=11, Pipe-Flange=32
        // (PartType ordinals pinned by PartTypeLabelMap).
        Assert.Equal([6, 10, 11, 32], junctions.PartTypeOrdinals);
    }
}
