using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.Actualization;
using SmartCon.Tests.FamilyManager.Repository;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// Tests for <see cref="AttributesActualizationTask"/> (ADR-054): detection
/// of missing (#152) and broken (#151 READERROR/raw units) extraction data
/// on ACTIVE labels, and the idempotent per-variant apply with counters.
/// </summary>
public sealed class AttributesActualizationTaskTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly FakeDataImportService _dataImport = new();
    private readonly FakeSharedNestedRepository _sharedNested = new();
    private readonly AttributesActualizationTask _sut;

    public AttributesActualizationTaskTests()
    {
        _fixture = new TempCatalogFixture();
        _sut = new AttributesActualizationTask(
            _fixture.GetDatabase(), _dataImport, _sharedNested);
    }

    public void Dispose() => _fixture.Dispose();

    private sealed record SaveCall(string ItemId, string? VersionId, string? FileId);

    private sealed class FakeDataImportService : IFamilyDataImportService
    {
        public List<SaveCall> SaveCalls { get; } = new();

        public Task<FamilyDataImportResult> SaveExtractionResultAsync(
            string catalogItemId, FamilyExtractionResult extractionResult,
            string? versionId, string? fileId, CancellationToken ct = default)
        {
            SaveCalls.Add(new SaveCall(catalogItemId, versionId, fileId));
            return Task.FromResult(new FamilyDataImportResult(true, "run", 1, 1, 0, null));
        }

        public Task<FamilyDataImportResult> ImportDataAsync(string catalogItemId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<FamilyExtractionPrepareResult> PrepareExtractionAsync(
            string catalogItemId, int targetRevitVersion, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private sealed class FakeSharedNestedRepository : ISharedNestedFamilyRepository
    {
        public List<(string ItemId, string VersionId, IReadOnlyList<string> Names)> ReplaceCalls { get; } = new();

        public Task ReplaceForVersionAsync(
            string catalogItemId, string versionId,
            IReadOnlyList<string> nestedSharedNames, CancellationToken ct = default)
        {
            ReplaceCalls.Add((catalogItemId, versionId, nestedSharedNames));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetNamesForCurrentVersionAsync(
            string catalogItemId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    private static FamilyActualizationContext ContextFor(
        string itemId, string label, params ActualizationVariant[] variants)
    {
        return new FamilyActualizationContext(
            new ActualizationGroup(itemId, "FamA", label, true, variants),
            variants[0],
            "C:\\fake\\path.rfa",
            CatalogSeedHelper.CreateSnapshot(),
            Geometry: null);
    }

    [Fact]
    public async Task CountPending_BareRow_Pending_FullyPopulated_NotPending()
    {
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamBare");
        var (itemId, versionId, fileId, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "FamOk", hashFormatVersion: 2, contentHash: "H");
        var runId = await CatalogSeedHelper.SeedDataExtractedAsync(_fixture, itemId, versionId, fileId);
        await CatalogSeedHelper.SeedAttributeValueAsync(
            _fixture, itemId, versionId, fileId, "50 мм", "autodesk.unit.unit:millimeters-1.0.1", runId);

        Assert.Equal(1, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task CountPending_BrokenValues_Pending()
    {
        var (itemId, versionId, fileId, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA");
        var runId = await CatalogSeedHelper.SeedDataExtractedAsync(_fixture, itemId, versionId, fileId);
        await CatalogSeedHelper.SeedAttributeValueAsync(_fixture, itemId, versionId, fileId, "50", null, runId);

        Assert.Equal(1, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task CountPending_ReadErrorValue_Pending()
    {
        var (itemId, versionId, fileId, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA");
        var runId = await CatalogSeedHelper.SeedDataExtractedAsync(_fixture, itemId, versionId, fileId);
        await CatalogSeedHelper.SeedAttributeValueAsync(
            _fixture, itemId, versionId, fileId, "READERROR", "autodesk.unit.unit:millimeters-1.0.1", runId);

        Assert.Equal(1, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task CountNewerOnly_Bare2026Row_NewerOnlyNotProcessable()
    {
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamNew", revitVersion: 2026);

        var newer = await _sut.GetNewerOnlyPendingAsync(2025);
        Assert.Equal(1, newer.Count);
        Assert.Equal(2026, newer.RequiredRevitVersion);
        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task LoadPendingKeys_ActiveLabelOnly_NonActiveIgnored()
    {
        // Item whose ACTIVE label (v2) is fully extracted, but the old v1
        // has no data — v1 is history and must NOT be pending.
        var (itemId, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "FamA", versionLabel: "v1", currentLabel: "v2");
        var (v2Id, fileId2) = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        await SeedV2ExtractedAsync(itemId, v2Id, fileId2);

        var keys = await _sut.LoadPendingGroupKeysAsync(2025);

        Assert.Empty(keys);
        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task Apply_WritesDataToEveryVariant_AndUpdatesCounters()
    {
        var (itemId, v2025, f2025, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA", revitVersion: 2025);
        var v2021 = await CatalogSeedHelper.SeedAdditionalVariantAsync(_fixture, itemId, "FamA", "v1", 2021);

        var ctx = ContextFor(itemId, "v1",
            new ActualizationVariant(v2025, f2025, 2025, "p", "FamA.rfa"),
            new ActualizationVariant(v2021, "f2021", 2021, "p", "FamA.rfa"));
        await _sut.ApplyAsync(ctx, CancellationToken.None);

        Assert.Equal(2, _dataImport.SaveCalls.Count);
        Assert.Contains(_dataImport.SaveCalls, c => c.VersionId == v2025);
        Assert.Contains(_dataImport.SaveCalls, c => c.VersionId == v2021);
        Assert.Equal(2, _sharedNested.ReplaceCalls.Count);
        Assert.Equal(new[] { "SharedNestedA" }, _sharedNested.ReplaceCalls[0].Names);

        foreach (var vid in new[] { v2025, v2021 })
        {
            var (_, _, types, pars) = await CatalogSeedHelper.ReadVersionAsync(_fixture, vid);
            Assert.Equal(1, types);
            Assert.Equal(1, pars);
        }
    }

    private async Task SeedV2ExtractedAsync(string itemId, string versionId, string fileId)
    {
        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO family_files (id, relative_path, file_name, revit_major_version, imported_at_utc)
                VALUES (@id, @path, 'FamA.rfa', 2025, @t)
                """;
            cmd.Parameters.Add(new SqliteParameter("@id", fileId));
            cmd.Parameters.Add(new SqliteParameter("@path", $"files/{itemId}/v2/FamA.rfa"));
            cmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
            await cmd.ExecuteNonQueryAsync();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO catalog_versions (id, catalog_item_id, file_id, version_label,
                                              revit_major_version, published_at_utc)
                VALUES (@id, @itemId, @fileId, 'v2', 2025, @t)
                """;
            cmd.Parameters.Add(new SqliteParameter("@id", versionId));
            cmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
            cmd.Parameters.Add(new SqliteParameter("@fileId", fileId));
            cmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
            await cmd.ExecuteNonQueryAsync();
        }
        tx.Commit();
        await CatalogSeedHelper.SeedDataExtractedAsync(_fixture, itemId, versionId, fileId);
    }
}
