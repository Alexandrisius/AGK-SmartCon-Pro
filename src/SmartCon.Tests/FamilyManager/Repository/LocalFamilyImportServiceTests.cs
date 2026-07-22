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
        Assert.False(result.WasSkipped);
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
        Assert.False(result1.WasSkipped);
        Assert.Equal("v1", result1.VersionLabel);

        var result2 = await _importService.ImportFileAsync(new FamilyImportRequest(path1, 2025, null, null, null));
        Assert.True(result2.Success);
        Assert.False(result2.WasSkipped);
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
        Assert.False(result.WasSkipped);

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
    /// Called by the VM in MapPreparedItemsToBatchItemsAsync.
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

    /// <summary>
    /// v2.0.1: when the user renames a row in the batch dialog and picks
    /// OverwriteCurrent, the catalog row's `name` and `normalized_name`
    /// must reflect the new name. Previously the on-disk file was written
    /// under the new name but the DB row kept the old name, so the catalog
    /// tree showed the family under the wrong label.
    ///
    /// OverwriteCurrent keeps the existing file path (vN) — only the
    /// `file_name` column and the catalog row's `name` change. We
    /// simulate the post-SaveAs state by writing the staged bytes to the
    /// existing managed file path so OverwriteCurrentAsync's
    /// <c>PrepareManagedRfaAsync</c> sees the source already at the
    /// canonical location and skips the copy.
    /// </summary>
    [Fact]
    public async Task ImportBatchAsync_OverwriteCurrent_UpdatesCatalogItemName()
    {
        // Seed an existing family in v1.
        var seedPath = _fixture.CreateFakeRfaFile("OriginalName.rfa");
        var seed = await _importService.ImportFileAsync(
            new FamilyImportRequest(seedPath, 2025, null, null, null));
        Assert.True(seed.Success);
        Assert.NotNull(seed.CatalogItemId);

        // Simulate the post-SaveAs state: ProcessFamilyImportAsync wrote
        // the staged bytes to the EXISTING managed path (overwrite), so
        // the on-disk file at the canonical v1 path is what the dialog
        // considers the "renamed" content. OverwriteCurrentAsync then
        // overwrites family_files.file_name and catalog_items.name with
        // the dialog's FileName ("RenamedName").
        var versionsBefore = await _fixture.GetProvider().GetVersionsAsync(seed.CatalogItemId!);
        Assert.Single(versionsBefore);
        var seedVersionId = versionsBefore[0].Id;
        var seedPublishedAt = versionsBefore[0].PublishedAtUtc;
        var existingFile = await _fixture.GetProvider().GetFileAsync(versionsBefore[0].FileId);
        Assert.NotNull(existingFile);
        var existingAbsolutePath = Path.Combine(_fixture.GetDatabaseRoot(), existingFile!.RelativePath);
        File.SetAttributes(existingAbsolutePath, File.GetAttributes(existingAbsolutePath) & ~FileAttributes.ReadOnly);
        var newContent = $"RENAMED_CONTENT_{Guid.NewGuid()}";
        File.WriteAllText(existingAbsolutePath, newContent);
        Assert.True(File.Exists(existingAbsolutePath));

        var item = new FamilyBatchImportItem(
            FilePath: existingAbsolutePath,
            FileName: "RenamedName",
            RevitMajorVersion: 2025,
            Status: FamilyBatchImportStatus.Existing,
            ExistingCatalogItemId: seed.CatalogItemId,
            ExistingVersionLabel: seed.VersionLabel,
            TargetCategoryId: null,
            TargetCategoryName: null,
            FamilySource: "loadable",
            TypeCount: null,
            RevitCategory: null,
            OriginalSourcePath: null,
            SourceTypes: null,
            Source: null,
            PrecomputedCatalogItemId: null,
            PrecomputedVersionLabel: null,
            PrecomputedManagedPath: null,
            ContentHash: "NEW_HASH_ADR040_" + Guid.NewGuid().ToString("N"),
            HashFormatVersion: 1,
            MatchedVersionLabel: null,
            LoadableSnapshot: null,
            SystemSnapshot: null)
        {
            // v2.0.1: the default Action is IncrementVersion, which would
            // route through UpdateFamilyAsync and try to create v2 — not
            // what we want to test here. Set it to OverwriteCurrent so the
            // service exercises the rename-while-overwriting path.
            Action = FamilyBatchImportAction.OverwriteCurrent
        };

        var result = await _importService.ImportBatchAsync(new[] { item }, null, null);

        Assert.True(
            result.Results.Count > 0 && result.Results[0].Success,
            $"Overwrite failed: ErrorMessage={result.Results[0].ErrorMessage}, FileName={result.Results[0].FileName}, SuccessCount={result.SuccessCount}, ErrorCount={result.ErrorCount}");
        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(0, result.ErrorCount);

        var itemAfter = await _fixture.GetProvider().GetItemAsync(seed.CatalogItemId!);
        Assert.NotNull(itemAfter);
        Assert.Equal("RenamedName", itemAfter!.Name);
        Assert.Equal("renamedname", itemAfter.NormalizedName);

        // The family_files row must also reflect the new file_name.
        var versions = await _fixture.GetProvider().GetVersionsAsync(seed.CatalogItemId!);
        Assert.Single(versions);
        var fileAfter = await _fixture.GetProvider().GetFileAsync(versions[0].FileId);
        Assert.NotNull(fileAfter);
        Assert.Equal("RenamedName.rfa", fileAfter!.FileName);

        // ADR-040: catalog_versions must be UPDATEd in place, not INSERTed.
        // The version id must stay the same (no new row), but content_hash
        // and published_at_utc must reflect the new content.
        Assert.Single(versions);
        Assert.Equal(seedVersionId, versions[0].Id);
        Assert.Equal(seed.VersionLabel, versions[0].VersionLabel);
        Assert.Equal(item.ContentHash, versions[0].ContentHash);
        Assert.Equal(1, versions[0].HashFormatVersion);
        Assert.True(versions[0].PublishedAtUtc > seedPublishedAt,
            $"published_at_utc should advance: was {seedPublishedAt:O}, now {versions[0].PublishedAtUtc:O}");

        // ADR-040: the on-disk .rfa file must contain the new content
        // (OverwriteCurrentAsync must not leave the old bytes).
        var onDiskContent = File.ReadAllText(existingAbsolutePath);
        Assert.Equal(newContent, onDiskContent);

        // catalog_items.content_hash must also be updated (stale detection
        // relies on it).
        Assert.Equal(versions[0].ContentHash, itemAfter.ContentHash);
    }

    /// <summary>
    /// ADR-041 rev #2 regression: MakeActive on a Duplicate status row must
    /// NOT insert a new catalog_versions row. The incoming file's content
    /// hash matched an existing version, so the file is ALREADY on disk at
    /// <c>{itemId}/{MatchedVersionLabel}/{name}.rfa</c> — MakeActive only
    /// switches <c>catalog_items.current_version_label</c>. Any new version
    /// row would duplicate the file on disk, waste storage, and break the
    /// version-history display in the UI.
    /// </summary>
    [Fact]
    public async Task ImportBatchAsync_MakeActive_Duplicate_DoesNotCreateNewVersion()
    {
        // Seed two versions (v1 = 3 types, v2 = 2 types) for a single
        // catalog item. Active = v1. Both versions persist on disk because
        // V18 migration keeps per-version family_types UNIQUE.
        var seedPath = _fixture.CreateFakeRfaFile("MakeActiveSource.rfa");
        var seed = await _importService.ImportFileAsync(
            new FamilyImportRequest(seedPath, 2025, null, null, null));
        Assert.True(seed.Success);
        Assert.NotNull(seed.CatalogItemId);

        var versions = await _fixture.GetProvider().GetVersionsAsync(seed.CatalogItemId!);
        Assert.Single(versions); // v1 only after the first import

        // Simulate an import of the SAME content (duplicate of v1) — the
        // dedup service would have resolved Status = Duplicate and
        // MatchedVersionLabel = "v1". The user picks MakeActive from the
        // batch dialog. ImportBatchAsync must route through
        // SetActiveVersionAsync and return WasSkipped=true (no file write,
        // no new version row).
        var item = new FamilyBatchImportItem(
            FilePath: seedPath,
            FileName: "MakeActiveSource",
            RevitMajorVersion: 2025,
            Status: FamilyBatchImportStatus.Duplicate,
            ExistingCatalogItemId: seed.CatalogItemId,
            ExistingVersionLabel: "v1",
            TargetCategoryId: null,
            TargetCategoryName: null,
            FamilySource: "loadable",
            TypeCount: null,
            RevitCategory: null,
            OriginalSourcePath: null,
            SourceTypes: null,
            Source: null,
            PrecomputedCatalogItemId: seed.CatalogItemId,
            PrecomputedVersionLabel: "v1",
            PrecomputedManagedPath: null,
            ContentHash: "ANY_HASH_ADR041_" + Guid.NewGuid().ToString("N"),
            HashFormatVersion: 1,
            MatchedVersionLabel: "v1",
            LoadableSnapshot: null,
            SystemSnapshot: null)
        {
            Action = FamilyBatchImportAction.MakeActive
        };

        var result = await _importService.ImportBatchAsync(new[] { item }, null, null);

        // Success, WasSkipped, and VersionLabel = MatchedVersionLabel ("v1")
        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(0, result.ErrorCount);
        var r = result.Results[0];
        Assert.True(r.Success, $"MakeActive failed: {r.ErrorMessage}");
        Assert.True(r.WasSkipped, "MakeActive must set WasSkipped=true — no file is written");
        Assert.Equal("v1", r.VersionLabel);
        Assert.Null(r.VersionId);
        Assert.Null(r.FileId);

        // No new catalog_versions row was inserted.
        var versionsAfter = await _fixture.GetProvider().GetVersionsAsync(seed.CatalogItemId!);
        Assert.Single(versionsAfter);
        // The id/version_label of v1 must be unchanged — ImportBatchAsync
        // did not UPDATE catalog_versions, only catalog_items.current_version_label.
        Assert.Equal(versions[0].Id, versionsAfter[0].Id);
        Assert.Equal("v1", versionsAfter[0].VersionLabel);

        // And current_version_label is now "v1" (it already was "v1" after
        // the seed import, so SetActiveVersionAsync is a no-op of the label,
        // but it still syncs content_hash and returns Success=true).
        var itemAfter = await _fixture.GetProvider().GetItemAsync(seed.CatalogItemId!);
        Assert.NotNull(itemAfter);
        Assert.Equal("v1", itemAfter!.CurrentVersionLabel);
    }

    /// <summary>
    /// ADR-040: OverwriteCurrent with a non-existent current version
    /// (catalog_items.current_version_label points to a missing
    /// catalog_versions row) must return a descriptive error instead of
    /// throwing or silently succeeding.
    /// </summary>
    [Fact]
    public async Task OverwriteCurrent_CurrentVersionNotFound_ReturnsError()
    {
        var item = new FamilyBatchImportItem(
            FilePath: _fixture.CreateFakeRfaFile("Orphan.rfa"),
            FileName: "Orphan",
            RevitMajorVersion: 2025,
            Status: FamilyBatchImportStatus.Existing,
            ExistingCatalogItemId: "nonexistent-catalog-item-id",
            ExistingVersionLabel: "v1",
            TargetCategoryId: null,
            TargetCategoryName: null,
            FamilySource: "loadable",
            TypeCount: null)
        {
            Action = FamilyBatchImportAction.OverwriteCurrent
        };

        var result = await _importService.ImportBatchAsync(new[] { item }, null, null);

        Assert.Equal(0, result.SuccessCount);
        Assert.Equal(1, result.ErrorCount);
        Assert.Single(result.Results);
        Assert.False(result.Results[0].Success);
        Assert.Contains("Current version not found", result.Results[0].ErrorMessage);
    }

    /// <summary>
    /// ADR-040: OverwriteCurrent for a system family (FamilySource="system")
    /// must write the ".rvt" extension to family_files.file_name, not ".rfa".
    /// Previously the extension was hardcoded to ".rfa", producing
    /// "Трубы.rfa" for a system Pipe family.
    /// </summary>
    [Fact]
    public async Task OverwriteCurrent_SystemFamily_UsesRvtExtensionInFileName()
    {
        // Seed an existing system family in v1.
        var seedPath = _fixture.CreateFakeRfaFile("Truby.rfa");
        var seed = await _importService.ImportFileAsync(
            new FamilyImportRequest(seedPath, 2025, null, null, null)
            {
                FamilySource = "system"
            });
        Assert.True(seed.Success);
        Assert.NotNull(seed.CatalogItemId);

        // Simulate post-staging: overwrite the v1 managed file.
        var versionsBefore = await _fixture.GetProvider().GetVersionsAsync(seed.CatalogItemId!);
        Assert.Single(versionsBefore);
        var existingFile = await _fixture.GetProvider().GetFileAsync(versionsBefore[0].FileId);
        Assert.NotNull(existingFile);
        var existingAbsolutePath = Path.Combine(_fixture.GetDatabaseRoot(), existingFile!.RelativePath);
        File.SetAttributes(existingAbsolutePath, File.GetAttributes(existingAbsolutePath) & ~FileAttributes.ReadOnly);
        File.WriteAllText(existingAbsolutePath, $"SYSTEM_OVERWRITE_{Guid.NewGuid()}");

        var item = new FamilyBatchImportItem(
            FilePath: existingAbsolutePath,
            FileName: "Truby",
            RevitMajorVersion: 2025,
            Status: FamilyBatchImportStatus.Existing,
            ExistingCatalogItemId: seed.CatalogItemId,
            ExistingVersionLabel: seed.VersionLabel,
            TargetCategoryId: null,
            TargetCategoryName: null,
            FamilySource: "system",
            TypeCount: null)
        {
            Action = FamilyBatchImportAction.OverwriteCurrent
        };

        var result = await _importService.ImportBatchAsync(new[] { item }, null, null);

        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(0, result.ErrorCount);

        var versions = await _fixture.GetProvider().GetVersionsAsync(seed.CatalogItemId!);
        Assert.Single(versions);
        var fileAfter = await _fixture.GetProvider().GetFileAsync(versions[0].FileId);
        Assert.NotNull(fileAfter);
        Assert.Equal("Truby.rvt", fileAfter!.FileName);
    }
}
