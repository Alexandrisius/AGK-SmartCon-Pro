using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.Actualization;
using SmartCon.FamilyManager.Services.LocalCatalog;
using SmartCon.Tests.FamilyManager.Repository;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// <see cref="SegmentRulesActualizationTask"/> (FHV21, optional): detects
/// pipe versions without per-version segment-rule rows and backfills them
/// from the staged mini's Segments routing group.
/// </summary>
public sealed class SegmentRulesActualizationTaskTests : IDisposable
{
    private const int PipeCategoryId = RoutingGroupCatalog.PipeCurvesCategoryId;

    private readonly TempCatalogFixture _fixture = new();
    private readonly SegmentRulesActualizationTask _sut;

    public SegmentRulesActualizationTaskTests()
    {
        _sut = new SegmentRulesActualizationTask(_fixture.GetDatabase());
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task CountPending_PipeVersionWithoutRows_Pending()
    {
        var (itemId, versionId, fileId, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "Pipes", familySource: "system", revitCategoryId: PipeCategoryId);
        await CatalogSeedHelper.SeedDataExtractedAsync(_fixture, itemId, versionId, fileId);

        Assert.Equal(1, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task CountPending_PipeVersionWithRows_NotPending()
    {
        var (itemId, versionId, fileId, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "Pipes", familySource: "system", revitCategoryId: PipeCategoryId);
        await CatalogSeedHelper.SeedDataExtractedAsync(_fixture, itemId, versionId, fileId);
        await new LocalSegmentRuleRepository(_fixture.GetDatabase()).ReplaceForVersionAsync(versionId,
            [new SegmentRuleRecord("T", "K", 0, "Steel", null, null, "")]);

        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task CountPending_DuctVersion_NotPending()
    {
        // Segments are pipe-only content — duct versions never need rows.
        await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "Ducts", familySource: "system",
            revitCategoryId: RoutingGroupCatalog.DuctCurvesCategoryId);

        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task Apply_WritesPerVersionRows_FromSnapshot()
    {
        var (itemId, versionId, fileId, relativePath) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "Pipes", familySource: "system", revitCategoryId: PipeCategoryId);
        await CatalogSeedHelper.SeedDataExtractedAsync(_fixture, itemId, versionId, fileId);
        var systemSnapshot = new SystemFamilySnapshot("Pipes", PipeCategoryId,
        [
            new SystemTypeSnapshot("Type A", [], Routing: new RoutingPreferencesSnapshot(0,
            [
                new RoutingRuleSnapshot(0, "Steel", "main",
                    [new RoutingCriterionSnapshot("PrimarySizeCriterion", 0.05, 0.15)]),
                new RoutingRuleSnapshot(0, "Copper", "wide", []),
                new RoutingRuleSnapshot(1, "Elbow:DN50", "elbow", []),
            ]), FamilyKey: "Pipe.Types"),
        ]);
        var variant = new ActualizationVariant(versionId, fileId, 2025, relativePath, "Pipes.rvt");
        var context = new FamilyActualizationContext(
            new ActualizationGroup(itemId, "Pipes", "v1", true, [variant]),
            variant,
            "C:\\fake\\Pipes.rvt",
            CatalogSeedHelper.CreateSnapshot(),
            Geometry: null,
            SystemSnapshot: systemSnapshot);
        await _sut.ApplyAsync(context);

        var rules = await new LocalSegmentRuleRepository(_fixture.GetDatabase())
            .ReadForVersionAsync(versionId);
        Assert.Equal(2, rules.Count);
        Assert.Equal("Steel", rules[0].SegmentName);
        Assert.Equal(0.05, rules[0].MinSizeFeet);
        Assert.Equal(0.15, rules[0].MaxSizeFeet);
        Assert.Equal("main", rules[0].Description);
        Assert.Equal("Copper", rules[1].SegmentName);
        Assert.Null(rules[1].MinSizeFeet);
        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task Apply_NullSnapshot_StaysPending()
    {
        var (itemId, versionId, fileId, relativePath) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "Pipes", familySource: "system", revitCategoryId: PipeCategoryId);
        await CatalogSeedHelper.SeedDataExtractedAsync(_fixture, itemId, versionId, fileId);
        var variant = new ActualizationVariant(versionId, fileId, 2025, relativePath, "Pipes.rvt");
        var context = new FamilyActualizationContext(
            new ActualizationGroup(itemId, "Pipes", "v1", true, [variant]),
            variant,
            "C:\\fake\\Pipes.rvt",
            CatalogSeedHelper.CreateSnapshot(),
            Geometry: null,
            SystemSnapshot: null);
        await _sut.ApplyAsync(context);

        Assert.Equal(1, await _sut.CountPendingAsync(2025));
    }
}
