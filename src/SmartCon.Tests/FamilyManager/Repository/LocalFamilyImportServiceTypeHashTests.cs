using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

/// <summary>
/// Issue #249 (Phase 2): the import transactions must persist the
/// Prepare-time per-type hashes into <c>family_type_hashes</c> — on the
/// initial import, on IncrementVersion, and on OverwriteCurrent (where
/// the previous rows are invalidated by definition).
/// </summary>
public sealed class LocalFamilyImportServiceTypeHashTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly LocalFamilyImportService _importService;

    public LocalFamilyImportServiceTypeHashTests()
    {
        _fixture = new TempCatalogFixture();
        _importService = new LocalFamilyImportService(
            _fixture.GetDatabase(),
            _fixture.GetMigrator(),
            _fixture.GetProvider(),
            _fixture.GetPathResolver(),
            new FileMetadataExtractionService(),
            _fixture.GetTypeRepository(),
            _fixture.GetValueRepository(),
            _fixture.GetRunRepository(),
            _fixture.GetTypeCatalogBaker());
    }

    public void Dispose() => _fixture.Dispose();

    private static IReadOnlyList<FamilyTypeHashEntry> Hashes(params (string Name, string Hash)[] types)
        => types.Select(t => FamilyTypeHashEntry.ForLoadableType(t.Name, t.Hash)).ToList();

    private async Task<IReadOnlyList<(string Key, string Name, string Hash)>> ReadRowsAsync(string versionId)
    {
        var rows = new List<(string, string, string)>();
        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT type_identity_key, type_name, type_hash FROM family_type_hashes WHERE catalog_version_id = @id ORDER BY type_identity_key";
        cmd.Parameters.Add(new SqliteParameter("@id", versionId));
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return rows;
    }

    [Fact]
    public async Task ImportFile_WithPerTypeHashes_WritesRows()
    {
        var path = _fixture.CreateFakeRfaFile("FamHashes.rfa");
        var request = new FamilyImportRequest(
            path, 2025, null, null, null,
            ContentHash: "H1",
            HashFormatVersion: 11,
            PerTypeHashes: Hashes(("DN50", "AAA"), ("DN80", "BBB")));

        var result = await _importService.ImportFileAsync(request);

        Assert.True(result.Success);
        var rows = await ReadRowsAsync(result.VersionId!);
        Assert.Equal(2, rows.Count);
        Assert.Equal("DN50", rows[0].Key);
        Assert.Equal("DN50", rows[0].Name);
        Assert.Equal("AAA", rows[0].Hash);
        Assert.Equal("DN80", rows[1].Key);
        Assert.Equal("BBB", rows[1].Hash);
    }

    [Fact]
    public async Task ImportFile_WithoutPerTypeHashes_LeavesTableEmpty()
    {
        // Legacy/folder import: no Prepare-time hashes — rows stay absent,
        // the type-hashes-v1 actualization task backfills them later.
        var path = _fixture.CreateFakeRfaFile("FamLegacy.rfa");
        var result = await _importService.ImportFileAsync(
            new FamilyImportRequest(path, 2025, null, null, null));

        Assert.True(result.Success);
        Assert.Empty(await ReadRowsAsync(result.VersionId!));
    }

    [Fact]
    public async Task UpdateFamily_WithPerTypeHashes_WritesRowsForNewVersion()
    {
        var seedPath = _fixture.CreateFakeRfaFile("FamUpd.rfa");
        var seed = await _importService.ImportFileAsync(
            new FamilyImportRequest(seedPath, 2025, null, null, null));
        Assert.True(seed.Success);

        var updatePath = _fixture.CreateFakeRfaFileWithContent("FamUpd2.rfa", "NEW_CONTENT"u8.ToArray());
        var update = await _importService.UpdateFamilyAsync(new FamilyUpdateRequest(
            CatalogItemId: seed.CatalogItemId!,
            FilePath: updatePath,
            RevitMajorVersion: 2025,
            PerTypeHashes: Hashes(("DN50", "CCC"))));

        Assert.True(update.Success);
        Assert.Equal("v2", update.VersionLabel);
        var rows = await ReadRowsAsync(update.VersionId!);
        var row = Assert.Single(rows);
        Assert.Equal("DN50", row.Key);
        Assert.Equal("CCC", row.Hash);
        // v1 (imported without hashes) stays untouched.
        Assert.Empty(await ReadRowsAsync(seed.VersionId!));
    }

    [Fact]
    public async Task OverwriteCurrent_WithPerTypeHashes_ReplacesRows()
    {
        var (item, absolutePath) = await SeedOverwriteScenarioAsync(Hashes(("OLD", "000")));
        item = item with { PerTypeHashes = Hashes(("DN50", "NEW1"), ("DN80", "NEW2")) };

        var result = await _importService.ImportBatchAsync(new[] { item }, null, null);

        Assert.True(result.Results[0].Success, result.Results[0].ErrorMessage);
        var rows = await ReadRowsAsync(result.Results[0].VersionId!);
        Assert.Equal(2, rows.Count);
        Assert.Equal("DN50", rows[0].Key);
        Assert.Equal("NEW1", rows[0].Hash);
        Assert.Equal("DN80", rows[1].Key);
        Assert.Equal("NEW2", rows[1].Hash);
    }

    [Fact]
    public async Task OverwriteCurrent_NullPerTypeHashes_ClearsStaleRows()
    {
        // The overwrite REPLACED the content: without a fresh hash set the
        // previous rows are invalid by definition and must be cleared so
        // the version is re-detected as pending for backfill.
        var (item, _) = await SeedOverwriteScenarioAsync(Hashes(("OLD", "000")));

        var result = await _importService.ImportBatchAsync(new[] { item }, null, null);

        Assert.True(result.Results[0].Success, result.Results[0].ErrorMessage);
        Assert.Empty(await ReadRowsAsync(result.Results[0].VersionId!));
    }

    [Fact]
    public async Task ImportFile_WithSections_WritesJsonColumns()
    {
        var path = _fixture.CreateFakeRfaFile("FamSections.rfa");
        var sections = new[]
        {
            new ContentSectionHash("META", "FHV12|LOADABLE|-1|", "M1"),
            new ContentSectionHash("TYPES", "TYPES|", "T1"),
        };
        var request = new FamilyImportRequest(
            path, 2025, null, null, null,
            ContentHash: "H1",
            HashFormatVersion: 12,
            Sections: sections);

        var result = await _importService.ImportFileAsync(request);

        Assert.True(result.Success);
        var (hashesJson, stringsJson) = await ReadSectionColumnsAsync(result.VersionId!);
        var hashes = SmartCon.Core.Services.Implementation.ContentSectionJsonSerializer.Deserialize(hashesJson);
        var strings = SmartCon.Core.Services.Implementation.ContentSectionJsonSerializer.Deserialize(stringsJson);
        Assert.NotNull(hashes);
        Assert.NotNull(strings);
        Assert.Equal("M1", hashes!["META"]);
        Assert.Equal("FHV12|LOADABLE|-1|", strings!["META"]);
    }

    [Fact]
    public async Task OverwriteCurrent_NullSections_ClearsStaleColumns()
    {
        var (item, _) = await SeedOverwriteScenarioAsync(
            Hashes(("OLD", "000")),
            sections: new[] { new ContentSectionHash("META", "x", "OLDHASH") });

        var result = await _importService.ImportBatchAsync(new[] { item }, null, null);

        Assert.True(result.Results[0].Success, result.Results[0].ErrorMessage);
        var (hashesJson, stringsJson) = await ReadSectionColumnsAsync(result.Results[0].VersionId!);
        Assert.Null(hashesJson);
        Assert.Null(stringsJson);
    }

    private async Task<(string? Hashes, string? Strings)> ReadSectionColumnsAsync(string versionId)
    {
        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT section_hashes, section_strings FROM catalog_versions WHERE id = @id";
        cmd.Parameters.Add(new SqliteParameter("@id", versionId));
        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException("version row must exist");
        return (
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private async Task<(FamilyBatchImportItem Item, string AbsolutePath)> SeedOverwriteScenarioAsync(
        IReadOnlyList<FamilyTypeHashEntry>? seedHashes,
        IReadOnlyList<ContentSectionHash>? sections = null)
    {
        var seedPath = _fixture.CreateFakeRfaFile("FamOw.rfa");
        var seed = await _importService.ImportFileAsync(
            new FamilyImportRequest(seedPath, 2025, null, null, null,
                PerTypeHashes: seedHashes, Sections: sections));
        Assert.True(seed.Success);

        var versions = await _fixture.GetProvider().GetVersionsAsync(seed.CatalogItemId!);
        var existingFile = await _fixture.GetProvider().GetFileAsync(versions[0].FileId);
        var absolutePath = Path.Combine(_fixture.GetDatabaseRoot(), existingFile!.RelativePath);
        File.SetAttributes(absolutePath, File.GetAttributes(absolutePath) & ~FileAttributes.ReadOnly);
        File.WriteAllText(absolutePath, $"OVERWRITTEN_{Guid.NewGuid()}");

        var item = new FamilyBatchImportItem(
            FilePath: absolutePath,
            FileName: "FamOw",
            RevitMajorVersion: 2025,
            Status: FamilyBatchImportStatus.Existing,
            ExistingCatalogItemId: seed.CatalogItemId,
            ExistingVersionLabel: seed.VersionLabel,
            ContentHash: "NEWHASH_" + Guid.NewGuid().ToString("N"),
            HashFormatVersion: 11)
        {
            Action = FamilyBatchImportAction.OverwriteCurrent,
        };
        return (item, absolutePath);
    }
}
