using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.Actualization;
using SmartCon.Tests.FamilyManager.Repository;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// Tests for <see cref="FamilyFactsActualizationTask"/> (ADR-054/ADR-055):
/// registry-driven detection of items with NULL <c>revit_category_id</c> or
/// a missing required fact on the ACTIVE label, and the idempotent apply
/// that writes the snapshot's category id + facts (including the
/// evaluated-but-absent sentinel) and clears its own detection.
/// </summary>
public sealed class FamilyFactsActualizationTaskTests : IDisposable
{
    private const int PipeFittingCategoryId = -2008049;   // OST_PipeFitting
    private const int PipeCurvesCategoryId = -2008044;    // OST_PipeCurves (no rules)
    private const int UnknownCategoryId = -1;

    private readonly TempCatalogFixture _fixture;
    private readonly FamilyFactsActualizationTask _sut;

    public FamilyFactsActualizationTaskTests()
    {
        _fixture = new TempCatalogFixture();
        _sut = new FamilyFactsActualizationTask(_fixture.GetDatabase());
    }

    public void Dispose() => _fixture.Dispose();

    private static FamilyActualizationContext ContextFor(
        string itemId, string label, FamilySnapshot snapshot, params ActualizationVariant[] variants)
    {
        return new FamilyActualizationContext(
            new ActualizationGroup(itemId, "FamA", label, true, variants),
            variants[0],
            "C:\\fake\\path.rfa",
            snapshot,
            Geometry: null);
    }

    private static ActualizationVariant VariantFor(string versionId, string fileId, int revit = 2025)
        => new(versionId, fileId, revit, "files/x.rfa", "x.rfa");

    private static FamilySnapshot SnapshotWith(int? categoryId, params FamilyFact[] facts)
    {
        return new FamilySnapshot(
            FamilyName: "FamA",
            Category: "Pipe Fittings",
            Parameters: [],
            Types: [],
            Geometry: new GeometryMetrics(0, []),
            SharedNestedFamilyNames: [],
            CategoryId: categoryId,
            Facts: facts.Length > 0 ? facts : null);
    }

    [Fact]
    public async Task CountPending_NullCategoryId_Pending()
    {
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA");

        Assert.Equal(1, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task CountPending_UnknownSentinel_NotPending()
    {
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA", revitCategoryId: UnknownCategoryId);

        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task CountPending_RuleCategoryWithoutFact_Pending()
    {
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA", revitCategoryId: PipeFittingCategoryId);

        Assert.Equal(1, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task CountPending_RuleCategoryWithFact_NotPending()
    {
        var (itemId, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "FamA", revitCategoryId: PipeFittingCategoryId);
        await CatalogSeedHelper.SeedFactAsync(_fixture, itemId, "part_type", "5", "Elbow");
        await CatalogSeedHelper.SeedFactAsync(_fixture, itemId, "connector_shape", "1", "Round");

        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task CountPending_RuleCategoryWithSentinelFact_NotPending()
    {
        var (itemId, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "FamA", revitCategoryId: PipeFittingCategoryId);
        await CatalogSeedHelper.SeedFactAsync(_fixture, itemId, "part_type", "", "");
        await CatalogSeedHelper.SeedFactAsync(_fixture, itemId, "connector_shape", "1", "Round");

        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task CountPending_RuleCategoryWithPartTypeOnly_PendingOnMissingConnectorShape()
    {
        // Баг 8 (owner stress test 2026-09-01): connector_shape joined the
        // rule set — a pre-fact catalog row is pending until BOTH exist.
        var (itemId, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "FamA", revitCategoryId: PipeFittingCategoryId);
        await CatalogSeedHelper.SeedFactAsync(_fixture, itemId, "part_type", "5", "Elbow");

        Assert.Equal(1, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task CountPending_NonRuleCategoryWithId_NotPending()
    {
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA", revitCategoryId: PipeCurvesCategoryId);

        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task CountPending_SystemFamily_NullCategoryId_Pending()
    {
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamSys", familySource: "system");

        Assert.Equal(1, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task CountNewerOnly_2026Row_NewerOnlyNotProcessable()
    {
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamNew", revitVersion: 2026);

        var newer = await _sut.GetNewerOnlyPendingAsync(2025);
        Assert.Equal(1, newer.Count);
        Assert.Equal(2026, newer.RequiredRevitVersion);
        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task LoadPendingKeys_OnlyPendingRows()
    {
        var (nullItem, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamNull");
        var (filledItem, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "FamFilled", revitCategoryId: PipeCurvesCategoryId);

        var keys = await _sut.LoadPendingGroupKeysAsync(2025);

        Assert.Contains(nullItem + "|v1", keys);
        Assert.DoesNotContain(filledItem + "|v1", keys);
    }

    [Fact]
    public async Task Apply_WritesCategoryIdAndFacts_AndClearsDetection()
    {
        var (itemId, versionId, fileId, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA");
        var variant = VariantFor(versionId, fileId);
        var snapshot = SnapshotWith(PipeFittingCategoryId,
            new FamilyFact("part_type", "5", "Elbow"),
            new FamilyFact("connector_shape", "1", "Round"));

        await _sut.ApplyAsync(ContextFor(itemId, "v1", snapshot, variant));

        var (categoryId, facts) = await CatalogSeedHelper.ReadFactsAsync(_fixture, itemId);
        Assert.Equal(PipeFittingCategoryId, categoryId);
        Assert.Equal(2, facts.Count);
        Assert.Contains(("part_type", "5", "Elbow"), facts);
        Assert.Contains(("connector_shape", "1", "Round"), facts);
        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task Apply_NullSnapshotCategory_WritesUnknownSentinel_AndClearsDetection()
    {
        var (itemId, versionId, fileId, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA");
        var variant = VariantFor(versionId, fileId);

        await _sut.ApplyAsync(ContextFor(itemId, "v1", SnapshotWith(null), variant));

        var (categoryId, _) = await CatalogSeedHelper.ReadFactsAsync(_fixture, itemId);
        Assert.Equal(UnknownCategoryId, categoryId);
        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task Apply_SentinelFact_WrittenAndClearsDetection()
    {
        var (itemId, versionId, fileId, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "FamA", revitCategoryId: PipeFittingCategoryId);
        var variant = VariantFor(versionId, fileId);
        var snapshot = SnapshotWith(PipeFittingCategoryId,
            new FamilyFact("part_type", "", ""),
            new FamilyFact("connector_shape", "1", "Round"));

        await _sut.ApplyAsync(ContextFor(itemId, "v1", snapshot, variant));

        var (_, facts) = await CatalogSeedHelper.ReadFactsAsync(_fixture, itemId);
        Assert.Equal(2, facts.Count);
        Assert.Contains(("part_type", "", ""), facts);
        Assert.Contains(("connector_shape", "1", "Round"), facts);
        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task Apply_SentinelDoesNotOverwriteRealCategoryId()
    {
        var (itemId, versionId, fileId, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "FamA", revitCategoryId: PipeFittingCategoryId);
        var variant = VariantFor(versionId, fileId);
        // Detection fired via the missing fact; this run's extraction could
        // not read the category (null → -1 sentinel). The real id must stay.
        var snapshot = SnapshotWith(null,
            new FamilyFact("part_type", "6", "Tee"),
            new FamilyFact("connector_shape", "1", "Round"));

        await _sut.ApplyAsync(ContextFor(itemId, "v1", snapshot, variant));

        var (categoryId, facts) = await CatalogSeedHelper.ReadFactsAsync(_fixture, itemId);
        Assert.Equal(PipeFittingCategoryId, categoryId);
        Assert.Equal(2, facts.Count);
        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task Apply_SystemItem_WritesCategoryIdOnly_AndClearsDetection()
    {
        var (itemId, versionId, fileId, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "Pipes", familySource: "system");
        var variant = VariantFor(versionId, fileId);
        var snapshot = new FamilySnapshot(
            FamilyName: "Pipes",
            Category: "Трубы",
            Parameters: [],
            Types: [],
            Geometry: new GeometryMetrics(0, []),
            SharedNestedFamilyNames: [],
            CategoryId: PipeCurvesCategoryId);

        await _sut.ApplyAsync(ContextFor(itemId, "v1", snapshot, variant));

        var (categoryId, facts) = await CatalogSeedHelper.ReadFactsAsync(_fixture, itemId);
        Assert.Equal(PipeCurvesCategoryId, categoryId);
        Assert.Empty(facts);
        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }
}
