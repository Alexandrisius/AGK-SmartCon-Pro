using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.Actualization;
using SmartCon.Tests.FamilyManager.Repository;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// Tests for <see cref="RevitCategoryActualizationTask"/> (ADR-054):
/// detection of loadable items with NULL <c>revit_category</c> on the
/// ACTIVE label, and the idempotent apply that writes the snapshot's
/// category and clears its own detection.
/// </summary>
public sealed class RevitCategoryActualizationTaskTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly RevitCategoryActualizationTask _sut;

    public RevitCategoryActualizationTaskTests()
    {
        _fixture = new TempCatalogFixture();
        _sut = new RevitCategoryActualizationTask(_fixture.GetDatabase());
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

    private async Task<string?> ReadRevitCategoryAsync(string itemId)
    {
        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT revit_category FROM catalog_items WHERE id = @id";
        cmd.Parameters.Add(new SqliteParameter("@id", itemId));
        var result = await cmd.ExecuteScalarAsync();
        return result is string s ? s : null;
    }

    [Fact]
    public async Task CountPending_NullCategory_Pending()
    {
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA");

        Assert.Equal(1, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task CountPending_FilledCategory_NotPending()
    {
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA", revitCategory: "Pipe Fittings");

        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task CountPending_EmptyMarkerCategory_NotPending()
    {
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA", revitCategory: "");

        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task CountPending_SystemFamily_Pending()
    {
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamSys", familySource: "system");

        Assert.Equal(1, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task CountPending_SystemFamily_Filled_NotPending()
    {
        await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "FamSys", familySource: "system", revitCategory: "Трубы");

        Assert.Equal(0, await _sut.CountPendingAsync(2025));
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
    public async Task LoadPendingKeys_OnlyNullActiveLabelRows()
    {
        var (nullItem, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamNull");
        var (filledItem, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "FamFilled", revitCategory: "Pipe Fittings");

        var keys = await _sut.LoadPendingGroupKeysAsync(2025);

        Assert.Contains(nullItem + "|v1", keys);
        Assert.DoesNotContain(filledItem + "|v1", keys);
    }

    [Fact]
    public async Task Apply_WritesCategory_AndClearsDetection()
    {
        var (itemId, versionId, fileId, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA");
        var variant = VariantFor(versionId, fileId);

        await _sut.ApplyAsync(ContextFor(itemId, "v1", CatalogSeedHelper.CreateSnapshot(), variant));

        Assert.Equal("Pipe Fittings", await ReadRevitCategoryAsync(itemId));
        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task Apply_EmptySnapshotCategory_WritesEmptyMarker_AndClearsDetection()
    {
        var (itemId, versionId, fileId, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA");
        var variant = VariantFor(versionId, fileId);
        var snapshot = new FamilySnapshot(
            FamilyName: "FamA",
            Category: "",
            Parameters: [],
            Types: [],
            Geometry: new GeometryMetrics(0, []),
            SharedNestedFamilyNames: []);

        await _sut.ApplyAsync(ContextFor(itemId, "v1", snapshot, variant));

        Assert.Equal(string.Empty, await ReadRevitCategoryAsync(itemId));
        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task Apply_DoesNotOverwriteExistingCategory()
    {
        var (itemId, versionId, fileId, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "FamA", revitCategory: "Existing Category");
        var variant = VariantFor(versionId, fileId);

        await _sut.ApplyAsync(ContextFor(itemId, "v1", CatalogSeedHelper.CreateSnapshot(), variant));

        Assert.Equal("Existing Category", await ReadRevitCategoryAsync(itemId));
    }

    [Fact]
    public async Task Apply_SystemItem_WritesCategory_AndClearsDetection()
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
            SharedNestedFamilyNames: []);

        await _sut.ApplyAsync(ContextFor(itemId, "v1", snapshot, variant));

        Assert.Equal("Трубы", await ReadRevitCategoryAsync(itemId));
        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }
}
