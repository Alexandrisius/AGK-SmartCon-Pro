using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

/// <summary>
/// <see cref="LocalSegmentRuleRepository"/> (FHV21, V38): per-version
/// segment routing rules — CRUD, current-version resolution, replace
/// semantics, NULL criterion bounds round-trip.
/// </summary>
public sealed class LocalSegmentRuleRepositoryTests
{
    [Fact]
    public async Task ReplaceAndRead_RoundTripsRecordsWithCriterionBounds()
    {
        using var fixture = new TempCatalogFixture();
        var (itemId, versionId, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            fixture, "Pipes", familySource: "system", revitCategoryId: RoutingGroupCatalog.PipeCurvesCategoryId);
        var sut = new LocalSegmentRuleRepository(fixture.GetDatabase());

        await sut.ReplaceForVersionAsync(versionId,
        [
            new SegmentRuleRecord("Type A", "Pipe.Types", 0, "Steel", 0.05, 0.15, "main"),
            new SegmentRuleRecord("Type A", "Pipe.Types", 1, "Copper", null, null, "wide"),
        ]);

        var rules = await sut.ReadForVersionAsync(versionId);
        Assert.Equal(2, rules.Count);
        Assert.Equal("Steel", rules[0].SegmentName);
        Assert.Equal(0.05, rules[0].MinSizeFeet);
        Assert.Equal(0.15, rules[0].MaxSizeFeet);
        Assert.Equal("main", rules[0].Description);
        Assert.Equal("Copper", rules[1].SegmentName);
        Assert.Null(rules[1].MinSizeFeet);
        Assert.Null(rules[1].MaxSizeFeet);

        // The current-version reader follows the item pointer.
        var current = await sut.ReadForCurrentVersionAsync(itemId);
        Assert.Equal(rules.Count, current.Count);
    }

    [Fact]
    public async Task Replace_IsFullReplace_PerVersionIsolated()
    {
        using var fixture = new TempCatalogFixture();
        var (itemId, versionId, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            fixture, "Pipes", familySource: "system", revitCategoryId: RoutingGroupCatalog.PipeCurvesCategoryId);
        var sut = new LocalSegmentRuleRepository(fixture.GetDatabase());

        await sut.ReplaceForVersionAsync(versionId,
            [new SegmentRuleRecord("Type A", "K", 0, "Steel", null, null, "")]);
        await sut.ReplaceForVersionAsync(versionId,
            [new SegmentRuleRecord("Type A", "K", 0, "Copper", 0.1, 0.2, "")]);

        var rules = await sut.ReadForVersionAsync(versionId);
        var rule = Assert.Single(rules);
        Assert.Equal("Copper", rule.SegmentName);

        // A different version is untouched.
        var other = await sut.ReadForVersionAsync("no-such-version");
        Assert.Empty(other);
    }

    [Fact]
    public async Task ReplaceForCurrentVersion_NoCurrentVersion_WarnsAndSkips()
    {
        using var fixture = new TempCatalogFixture();
        var sut = new LocalSegmentRuleRepository(fixture.GetDatabase());

        // No item at all — the write is a guarded no-op, never an exception.
        await sut.ReplaceForCurrentVersionAsync("no-such-item",
            [new SegmentRuleRecord("T", "K", 0, "Steel", null, null, "")]);

        Assert.Empty(await sut.ReadForCurrentVersionAsync("no-such-item"));
    }
}
