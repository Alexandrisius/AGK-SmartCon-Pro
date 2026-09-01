using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.ViewModels;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

/// <summary>
/// Audit H3: the flex <c>RBS_CURVETYPE_PREFERRED_BRANCH_PARAM</c> int is
/// INVERTED against the <c>PreferredJunctionType</c> enum (param: 0=Tap,
/// 1=Tee; enum: Tee=0, Tap=1). Storage keeps the raw value (round-trip
/// stable) — the combo display mapping must differ per category.
/// </summary>
[Collection("Localization")]
public sealed class PreferredJunctionOptionsTests
{
    [Fact]
    public void FlexCategories_SwapTeeTapLabels()
    {
        foreach (var categoryId in new[]
        {
            RoutingGroupCatalog.FlexPipeCurvesCategoryId,
            RoutingGroupCatalog.FlexDuctCurvesCategoryId,
        })
        {
            var options = FamilyPropertiesViewModel.BuildPreferredJunctionOptions(categoryId);

            Assert.Equal(2, options.Count);
            Assert.Equal(0, options[0].Value);
            Assert.Equal(1, options[1].Value);
            // Raw 0 must DISPLAY as Tap, raw 1 as Tee (inverted param
            // convention — probe: template flex shows Tee while reading 1).
            Assert.Equal(options[0].Label,
                FamilyPropertiesViewModel.BuildPreferredJunctionOptions(
                    RoutingGroupCatalog.PipeCurvesCategoryId)[1].Label);
            Assert.Equal(options[1].Label,
                FamilyPropertiesViewModel.BuildPreferredJunctionOptions(
                    RoutingGroupCatalog.PipeCurvesCategoryId)[0].Label);
        }
    }

    [Fact]
    public void ManagerCategories_KeepEnumConvention()
    {
        foreach (var categoryId in new[]
        {
            RoutingGroupCatalog.PipeCurvesCategoryId,
            RoutingGroupCatalog.DuctCurvesCategoryId,
        })
        {
            var first = FamilyPropertiesViewModel.BuildPreferredJunctionOptions(categoryId);
            var flex = FamilyPropertiesViewModel.BuildPreferredJunctionOptions(
                RoutingGroupCatalog.FlexDuctCurvesCategoryId);

            // Enum Tee=0 must NOT be swapped on manager categories.
            Assert.Equal(first[0].Label, flex[1].Label);
            Assert.Equal(first[1].Label, flex[0].Label);
        }
    }
}
