using System.IO;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

public sealed class LocalFamilyImportServiceTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly LocalFamilyImportService _importService;

    public LocalFamilyImportServiceTests()
    {
        _fixture = new TempCatalogFixture();


        
        var metadataService = new FileMetadataExtractionService();
        _importService = new LocalFamilyImportService(
            _fixture.GetDatabase(),
            _fixture.GetMigrator(),
            _fixture.GetProvider(),
            _fixture.GetPathResolver(),
            metadataService,
            _fixture.GetTypeRepository(),
            _fixture.GetValueRepository(),
            _fixture.GetRunRepository(),
            _fixture.GetTypeCatalogBaker());
    }

    [Fact]
    public async Task ImportFile_NonExistentFile_ReturnsError()
    {
        var request = new FamilyImportRequest(
            FilePath: Path.Combine(_fixture.TempDir, "nonexistent.rfa"),
            RevitMajorVersion: 2025,
            Category: null, Tags: null, Description: null);

        var result = await _importService.ImportFileAsync(request);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("not found", result.ErrorMessage);
    }

    [Fact]
    public async Task ImportFile_ValidFile_ReturnsSuccess()
    {
        var path = _fixture.CreateFakeRfaFile("TestFamily.rfa");
        var request = new FamilyImportRequest(path, 2025, "Pipes", null, "Test desc");

        var result = await _importService.ImportFileAsync(request);

        Assert.True(result.Success);
        Assert.NotNull(result.CatalogItemId);
        Assert.NotNull(result.VersionId);
        Assert.NotNull(result.FileId);
        Assert.Equal("TestFamily.rfa", result.FileName);
        Assert.Equal("v1", result.VersionLabel);
        Assert.False(result.WasSkippedAsDuplicate);
    }

    [Fact]
    public async Task ImportFile_SameName_CreatesNewVersion()
    {
        // v2.0.0: SHA-256 dedup is gone. Re-importing the same path with the
        // same content now produces a new version (v2), not a Skip.
        var content = "SAME_CONTENT_NEW_VERSION"u8.ToArray();
        var path1 = _fixture.CreateFakeRfaFileWithContent("FamilyV2.rfa", content);

        var result1 = await _importService.ImportFileAsync(new FamilyImportRequest(path1, 2025, null, null, null));
        Assert.True(result1.Success);
        Assert.False(result1.WasSkippedAsDuplicate);
        Assert.Equal("v1", result1.VersionLabel);

        var result2 = await _importService.ImportFileAsync(new FamilyImportRequest(path1, 2025, null, null, null));
        Assert.True(result2.Success);
        Assert.False(result2.WasSkippedAsDuplicate);
        Assert.Equal("v2", result2.VersionLabel);
        Assert.Equal(result1.CatalogItemId, result2.CatalogItemId);
        Assert.Equal(result1.CatalogItemId, result2.CatalogItemId);
    }

    [Fact]
    public async Task ImportFile_CopiesToManagedStorage()
    {
        var path = _fixture.CreateFakeRfaFile("ManagedFamily.rfa");
        var request = new FamilyImportRequest(path, 2025, null, null, null);

        var result = await _importService.ImportFileAsync(request);

        Assert.True(result.Success);
        var file = await _fixture.GetProvider().GetFileAsync(result.FileId!);
        Assert.NotNull(file);
        Assert.NotEmpty(file.RelativePath);
        Assert.Equal(2025, file.RevitMajorVersion);

        var absolutePath = Path.Combine(_fixture.GetDatabaseRoot(), file.RelativePath);
        Assert.True(File.Exists(absolutePath));
    }

    [Fact]
    public async Task ImportFile_SameNameDifferentRevit_CreatesNewVersion()
    {
        var content1 = "CONTENT_R2024"u8.ToArray();
        var content2 = "CONTENT_R2025"u8.ToArray();
        _fixture.CreateFakeRfaFileWithContent("SameName.rfa", content1);

        var subDir = Path.Combine(_fixture.TempDir, "sub");
        Directory.CreateDirectory(subDir);
        var path2 = Path.Combine(subDir, "SameName.rfa");
        File.WriteAllBytes(path2, content2);

        var result1 = await _importService.ImportFileAsync(
            new FamilyImportRequest(Path.Combine(_fixture.TempDir, "SameName.rfa"), 2024, null, null, null));

        Assert.True(result1.Success);
        Assert.Equal("v1", result1.VersionLabel);

        var result2 = await _importService.ImportFileAsync(
            new FamilyImportRequest(path2, 2025, null, null, null));

        Assert.True(result2.Success);
        Assert.Equal("v2", result2.VersionLabel);
    }

    [Fact]
    public async Task ImportFolder_MultipleFiles_ReturnsBatchResult()
    {
        _fixture.CreateFakeRfaFile("A.rfa");
        _fixture.CreateFakeRfaFile("B.rfa");
        File.WriteAllText(Path.Combine(_fixture.TempDir, "C.txt"), "not an rfa");

        var request = new FamilyFolderImportRequest(_fixture.TempDir, 2025, false, "TestCategory", null, null);
        var result = await _importService.ImportFolderAsync(request, null);

        Assert.Equal(2, result.TotalFiles);
        Assert.Equal(2, result.SuccessCount);
    }

    [Fact]
    public async Task ImportFile_WithTags_TagsStored()
    {
        var path = _fixture.CreateFakeRfaFile("TaggedFamily.rfa");
        var tags = new List<string> { "HVAC", "Duct" };
        var request = new FamilyImportRequest(path, 2025, null, tags, null);

        var result = await _importService.ImportFileAsync(request);
        Assert.True(result.Success);

        var item = await _fixture.GetProvider().GetItemAsync(result.CatalogItemId!);
        Assert.NotNull(item);
        Assert.Equal(2, item.Tags.Count);
        Assert.Contains("HVAC", item.Tags);
        Assert.Contains("Duct", item.Tags);
    }

    [Fact]
    public async Task ImportFile_DefaultStatusIsActive()
    {
        var path = _fixture.CreateFakeRfaFile("StatusFamily.rfa");
        var request = new FamilyImportRequest(path, 2025, null, null, null);

        var result = await _importService.ImportFileAsync(request);
        Assert.True(result.Success);

        var item = await _fixture.GetProvider().GetItemAsync(result.CatalogItemId!);
        Assert.NotNull(item);
        Assert.Equal(ContentStatus.Active, item.ContentStatus);
    }

    /// <summary>
    /// v2.0.0 regression: when the VM hands <see cref="ImportFileAsync"/>
    /// a staged file path AND the precomputed (catalogItemId, versionLabel,
    /// managedPath) triple (UC-2 SaveAs / UC-3 / UC-4 staging flow), the
    /// service must register the staged file at the precomputed canonical
    /// path with no extra copy / re-allocation. Before this invariant was
    /// introduced the service generated a fresh GUID and the
    /// managed-path check returned false, producing
    /// <c>Success = false</c> for every staged item (UC-3 / UC-4 batch
    /// imports consistently imported 0 families).
    /// </summary>
    [Fact]
    public async Task ImportFile_StagedManagedFile_NewItem_RegistersAtStagedPath()
    {
        var stagedCatalogItemId = Guid.NewGuid().ToString("N");
        var stagedPath = _fixture.CreateFakeStagedManagedFile(
            stagedCatalogItemId, "v1", "StagedFamily.rfa");

        var request = new FamilyImportRequest(
            FilePath: stagedPath,
            RevitMajorVersion: 2025,
            Category: null,
            Tags: null,
            Description: null,
            PrecomputedCatalogItemId: stagedCatalogItemId,
            PrecomputedVersionLabel: "v1",
            PrecomputedManagedPath: stagedPath);

        var result = await _importService.ImportFileAsync(request);

        Assert.True(result.Success, $"Import returned Success=false: ErrorMessage={result.ErrorMessage}, FileName={result.FileName}");
        Assert.NotNull(result.CatalogItemId);
        Assert.Equal(stagedCatalogItemId, result.CatalogItemId);
        Assert.Equal("v1", result.VersionLabel);
        Assert.Equal(stagedPath, result.ManagedFilePath);
        Assert.Equal("StagedFamily.rfa", result.FileName);
        Assert.False(result.WasSkippedAsDuplicate);

        var file = await _fixture.GetProvider().GetFileAsync(result.FileId!);
        Assert.NotNull(file);
        var absolutePath = Path.Combine(_fixture.GetDatabaseRoot(), file.RelativePath);
        Assert.True(File.Exists(absolutePath));
        Assert.Equal(stagedPath, absolutePath);
    }

    /// <summary>
    /// v2.0.0 regression: when the VM resolves a re-import of an existing
    /// item, the VM passes the existing item's id as
    /// <c>PrecomputedCatalogItemId</c> and the next version as
    /// <c>PrecomputedVersionLabel</c>. <see cref="ImportFileAsync"/> must
    /// honour that contract — never overwrite the existing item's id with
    /// the staged GUID. (Earlier revisions tried to detect "staged" via
    /// path inspection, which broke the managed-path invariant because the
    /// staged path was committed with one catalog_item_id and the file's
    /// location implied another.)
    /// </summary>
    [Fact]
    public async Task ImportFile_StagedManagedFile_ExistingItem_UsesExistingCatalogItem()
    {
        // Seed: import the same family once normally so it exists in catalog.
        var firstPath = _fixture.CreateFakeRfaFile("ReimportFamily.rfa");
        var first = await _importService.ImportFileAsync(
            new FamilyImportRequest(firstPath, 2025, null, null, null));
        Assert.True(first.Success);
        Assert.NotNull(first.CatalogItemId);

        // VM-side resolution: precomputed id = existing, version = next.
        var stagedPath = _fixture.CreateFakeStagedManagedFile(
            first.CatalogItemId!, "v2", "ReimportFamily.rfa");

        var request = new FamilyImportRequest(
            FilePath: stagedPath,
            RevitMajorVersion: 2025,
            Category: null,
            Tags: null,
            Description: null,
            PrecomputedCatalogItemId: first.CatalogItemId,
            PrecomputedVersionLabel: "v2",
            PrecomputedManagedPath: stagedPath);

        var result = await _importService.ImportFileAsync(request);

        Assert.True(result.Success);
        Assert.Equal(first.CatalogItemId, result.CatalogItemId);
        Assert.Equal("v2", result.VersionLabel);
        Assert.Equal(stagedPath, result.ManagedFilePath);
        Assert.True(result.WasNewVersion);

        var versions = await _fixture.GetProvider().GetVersionsAsync(first.CatalogItemId!);
        Assert.Equal(2, versions.Count);
    }

    /// <summary>
    /// v2.0.0 regression: <see cref="UpdateFamilyAsync"/> must also honour
    /// the precomputed (versionLabel, managedPath) supplied by the VM for
    /// staged re-imports of existing items.
    /// </summary>
    [Fact]
    public async Task UpdateFamily_StagedManagedFile_ReusesStagedPath()
    {
        var seedPath = _fixture.CreateFakeRfaFile("UpdatableFamily.rfa");
        var seed = await _importService.ImportFileAsync(
            new FamilyImportRequest(seedPath, 2025, null, null, null));
        Assert.True(seed.Success);
        Assert.NotNull(seed.CatalogItemId);

        // VM allocates the next version + canonical managed path.
        var stagedPath = _fixture.CreateFakeStagedManagedFile(
            seed.CatalogItemId!, "v2", "UpdatableFamily.rfa");

        var updateRequest = new FamilyUpdateRequest(
            CatalogItemId: seed.CatalogItemId!,
            FilePath: stagedPath,
            RevitMajorVersion: 2025,
            CategoryId: null,
            CategoryName: null,
            FileName: "UpdatableFamily",
            PrecomputedVersionLabel: "v2",
            PrecomputedManagedPath: stagedPath);

        var result = await _importService.UpdateFamilyAsync(updateRequest);

        Assert.True(result.Success);
        Assert.Equal(seed.CatalogItemId, result.CatalogItemId);
        Assert.Equal("v2", result.VersionLabel);
        Assert.Equal(stagedPath, result.ManagedFilePath);

        var versions = await _fixture.GetProvider().GetVersionsAsync(seed.CatalogItemId!);
        Assert.Equal(2, versions.Count);
    }

    /// <summary>
    /// v2.0.0 regression: <see cref="ImportBatchAsync"/> with N items
    /// pointing to distinct staged managed paths must register every one
    /// of them. Before this fix all rows returned <c>Success = false</c>
    /// and the user saw "Импортировано: 0".
    /// </summary>
    [Fact]
    public async Task ImportBatchAsync_AllItemsStaged_RegistersAllAtStagedPaths()
    {
        var stagedPaths = new List<(string CatalogItemId, string Path, string FileName)>();
        for (var i = 0; i < 3; i++)
        {
            var stagedCatalogItemId = Guid.NewGuid().ToString("N");
            var fileName = $"BatchStaged{i}.rfa";
            var stagedPath = _fixture.CreateFakeStagedManagedFile(
                stagedCatalogItemId, "v1", fileName);
            stagedPaths.Add((stagedCatalogItemId, stagedPath, fileName));
        }

        var items = stagedPaths
            .Select(sp => new FamilyBatchImportItem(
                FilePath: sp.Path,
                FileName: Path.GetFileNameWithoutExtension(sp.Path),
                RevitMajorVersion: 2025,
                Status: FamilyBatchImportStatus.New,
                FamilySource: "loadable",
                TypeCount: null,
                PrecomputedCatalogItemId: sp.CatalogItemId,
                PrecomputedVersionLabel: "v1",
                PrecomputedManagedPath: sp.Path))
            .ToList();

        var result = await _importService.ImportBatchAsync(items, null, null);

        Assert.Equal(3, result.TotalFiles);
        Assert.Equal(3, result.SuccessCount);
        Assert.Equal(0, result.ErrorCount);

        for (var i = 0; i < items.Count; i++)
        {
            Assert.True(result.Results[i].Success);
            Assert.Equal(stagedPaths[i].CatalogItemId, result.Results[i].CatalogItemId);
            Assert.Equal(stagedPaths[i].Path, result.Results[i].ManagedFilePath);
        }
    }

    /// <summary>
    /// v2.0.0 regression: ImportBatchAsync with two rows for the same
    /// (catalogItemId, versionLabel, revitMajorVersion) triple must drop
    /// the duplicate, otherwise the SQLite UNIQUE constraint on
    /// <c>catalog_versions(catalog_item_id, version_label, revit_major_version)</c>
    /// trips and the entire batch rolls back.
    /// </summary>
    [Fact]
    public async Task ImportBatchAsync_DuplicateVersion_DropsSecond()
    {
        var catalogItemId = Guid.NewGuid().ToString("N");
        var firstStagedPath = _fixture.CreateFakeStagedManagedFile(
            catalogItemId, "v1", "DupFamily.rfa");
        var secondStagedPath = _fixture.CreateFakeStagedManagedFile(
            catalogItemId, "v1", "DupFamilyOther.rfa");

        var items = new List<FamilyBatchImportItem>
        {
            new(
                FilePath: firstStagedPath,
                FileName: "DupFamily",
                RevitMajorVersion: 2025,
                Status: FamilyBatchImportStatus.New,
                FamilySource: "loadable",
                TypeCount: null,
                PrecomputedCatalogItemId: catalogItemId,
                PrecomputedVersionLabel: "v1",
                PrecomputedManagedPath: firstStagedPath),
            new(
                FilePath: secondStagedPath,
                FileName: "DupFamilyOther",
                RevitMajorVersion: 2025,
                Status: FamilyBatchImportStatus.New,
                FamilySource: "loadable",
                TypeCount: null,
                PrecomputedCatalogItemId: catalogItemId,
                PrecomputedVersionLabel: "v1",
                PrecomputedManagedPath: secondStagedPath),
        };

        var result = await _importService.ImportBatchAsync(items, null, null);

        // Only the first row actually imports; the second is deduped.
        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Equal(0, result.ErrorCount);

        var versions = await _fixture.GetProvider().GetVersionsAsync(catalogItemId);
        Assert.Single(versions);
    }

    /// <summary>
    /// v2.0.0: <see cref="IFamilyImportService.GetNextVersionLabelAsync"/>
    /// must read the last version from the catalog and increment by one.
    /// Called by the VM in BuildSystemFamilyBatchRowVirtualAsync /
    /// BuildLoadableFamilyBatchRowVirtualAsync.
    /// </summary>
    [Fact]
    public async Task GetNextVersionLabelAsync_ReturnsV1ForNewAndIncrementsForExisting()
    {
        var newLabel = await _importService.GetNextVersionLabelAsync(Guid.NewGuid().ToString("N"), default);
        Assert.Equal("v2", newLabel);

        var seedPath = _fixture.CreateFakeRfaFile("VersionedFamily.rfa");
        var seed = await _importService.ImportFileAsync(
            new FamilyImportRequest(seedPath, 2025, null, null, null));
        Assert.True(seed.Success);

        var next = await _importService.GetNextVersionLabelAsync(seed.CatalogItemId!, default);
        Assert.Equal("v2", next);
    }

    /// <summary>
    /// v2.0.0: <see cref="IFamilyImportService.ComputeManagedFilePath"/>
    /// must compose the canonical managed path
    /// (<c>{dbRoot}/files/&lt;id&gt;/&lt;ver&gt;/&lt;name&gt;.{ext}</c>) and
    /// return <c>null</c> when the args are incomplete. Single source of
    /// truth for path allocation; both the staging helper and the import
    /// service go through here so the on-disk layout and the DB row can
    /// never drift out of sync.
    /// </summary>
    [Fact]
    public void ComputeManagedFilePath_HonoursCatalogIdVersionAndExtension()
    {
        var path = _importService.ComputeManagedFilePath(
            "abc-123", "v7", "MyFamily", ".rvt");
        Assert.NotNull(path);
        var dbRoot = _fixture.GetDatabaseRoot();
        Assert.StartsWith(dbRoot, path!);
        Assert.Contains("files", path!);
        Assert.Contains("abc-123", path!);
        Assert.Contains("v7", path!);
        Assert.EndsWith("MyFamily.rvt", path!);
    }

    [Fact]
    public void ComputeManagedFilePath_DefaultsToRfaWhenExtensionMissing()
    {
        var path = _importService.ComputeManagedFilePath(
            "abc-123", "v1", "MyFamily", "");
        Assert.EndsWith("MyFamily.rfa", path!);
    }

    [Fact]
    public void ComputeManagedFilePath_NullOnEmptyArgs()
    {
        Assert.Null(_importService.ComputeManagedFilePath("", "v1", "x", ".rfa"));
        Assert.Null(_importService.ComputeManagedFilePath("abc", "", "x", ".rfa"));
        Assert.Null(_importService.ComputeManagedFilePath("abc", "v1", " ", ".rfa"));
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }
}
