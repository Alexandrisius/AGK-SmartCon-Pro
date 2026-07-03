using System.IO;
using Moq;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// ADR-040 regression tests for <see cref="SystemFamilyImportOrchestrator"/>.
/// Verifies that the matching logic uses CatalogItemId (not filename) so
/// OverwriteCurrent results — where r.FileName has no extension but
/// item.FilePath does — are correctly matched and SyncTypesAsync is called.
/// </summary>
public sealed class SystemFamilyImportOrchestratorTests
{
    [Fact]
    public async Task ImportBatchItemsAsync_OverwriteCurrent_MatchesByCatalogItemId_CallsSyncTypesAsync()
    {
        // Arrange: a staged system family with OverwriteCurrent.
        var existingCatalogItemId = "existing-sys-id-adr040";
        var existingVersionLabel = "v1";
        var versionId = "version-id-adr040";
        var fileId = "file-id-adr040";
        var stagedPath = Path.Combine(Path.GetTempPath(), $"SmartConTest_{Guid.NewGuid():N}", "Truby.rvt");
        Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
        File.WriteAllText(stagedPath, "FAKE_STAGED_CONTENT");

        var sourceTypes = new List<FamilySourceTypeInfo>
        {
            new("uid-1", "TypeA", "Truby", -2008044),
            new("uid-2", "TypeB", "Truby", -2008044)
        };

        var item = new FamilyBatchImportItem(
            FilePath: stagedPath,
            FileName: "Truby",
            RevitMajorVersion: 2025,
            Status: FamilyBatchImportStatus.Existing,
            ExistingCatalogItemId: existingCatalogItemId,
            ExistingVersionLabel: existingVersionLabel,
            TargetCategoryId: null,
            TargetCategoryName: null,
            FamilySource: "system",
            TypeCount: 2,
            SourceTypes: sourceTypes)
        {
            Action = FamilyBatchImportAction.OverwriteCurrent
        };

        // Mock IFamilyImportService: ImportBatchAsync returns a result
        // whose CatalogItemId matches item.ExistingCatalogItemId. The
        // FileName is "Truby" (no extension) — this is what
        // OverwriteCurrentAsync returns. The OLD matching logic
        // (Path.GetFileName(item.FilePath) = "Truby.rvt" vs r.FileName =
        // "Truby") would FAIL to match; the ADR-040 logic matches by
        // CatalogItemId.
        var importServiceMock = new Mock<IFamilyImportService>();
        importServiceMock
            .Setup(s => s.ImportBatchAsync(It.IsAny<IReadOnlyList<FamilyBatchImportItem>>(), It.IsAny<string?>(), It.IsAny<IProgress<FamilyImportProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FamilyBatchImportResult(
                Results: new List<FamilyImportResult>
                {
                    new(true, existingCatalogItemId, versionId, fileId, "Truby", existingVersionLabel, null, stagedPath, false)
                },
                TotalFiles: 1,
                SuccessCount: 1,
                SkippedCount: 0,
                ErrorCount: 0));

        var typeRepoMock = new Mock<IFamilyTypeRepository>();

        var orchestrator = new SystemFamilyImportOrchestrator(
            importServiceMock.Object,
            typeRepoMock.Object);

        // Act
        var result = await orchestrator.ImportBatchItemsAsync(new[] { item });

        // Assert: SyncTypesAsync was called with the matched versionId/fileId.
        Assert.True(result.Success);
        Assert.Equal(1, result.TypesCount);
        Assert.Single(result.ExtractionTasks);

        typeRepoMock.Verify(
            t => t.SyncTypesAsync(
                existingCatalogItemId,
                versionId,
                fileId,
                "no-run",
                It.Is<IReadOnlyList<FamilyTypeDescriptor>>(list => list.Count == 2),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "SyncTypesAsync must be called once with the matched CatalogItemId/VersionId/FileId");

        // Cleanup
        if (File.Exists(stagedPath)) File.Delete(stagedPath);
        var dir = Path.GetDirectoryName(stagedPath);
        if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    [Fact]
    public async Task ImportBatchItemsAsync_NoMatchingResult_LogsWarningAndSkips()
    {
        // Arrange: ImportBatchAsync returns a result with a DIFFERENT
        // CatalogItemId than the item expects. Neither CatalogItemId nor
        // filename fallback should match.
        var stagedPath = Path.Combine(Path.GetTempPath(), $"SmartConTest_{Guid.NewGuid():N}", "Orphan.rvt");
        Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
        File.WriteAllText(stagedPath, "FAKE");

        var item = new FamilyBatchImportItem(
            FilePath: stagedPath,
            FileName: "Orphan",
            RevitMajorVersion: 2025,
            Status: FamilyBatchImportStatus.Existing,
            ExistingCatalogItemId: "expected-id",
            ExistingVersionLabel: "v1",
            TargetCategoryId: null,
            TargetCategoryName: null,
            FamilySource: "system",
            TypeCount: 1,
            SourceTypes: new List<FamilySourceTypeInfo> { new("uid", "TypeA", "Orphan", -2008044) })
        {
            Action = FamilyBatchImportAction.OverwriteCurrent
        };

        var importServiceMock = new Mock<IFamilyImportService>();
        importServiceMock
            .Setup(s => s.ImportBatchAsync(It.IsAny<IReadOnlyList<FamilyBatchImportItem>>(), It.IsAny<string?>(), It.IsAny<IProgress<FamilyImportProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FamilyBatchImportResult(
                Results: new List<FamilyImportResult>
                {
                    new(true, "different-id", "v-id", "f-id", "CompletelyDifferentName", "v1", null, null, false)
                },
                TotalFiles: 1,
                SuccessCount: 1,
                SkippedCount: 0,
                ErrorCount: 0));

        var typeRepoMock = new Mock<IFamilyTypeRepository>();
        var orchestrator = new SystemFamilyImportOrchestrator(
            importServiceMock.Object,
            typeRepoMock.Object);

        // Act
        var result = await orchestrator.ImportBatchItemsAsync(new[] { item });

        // Assert: no matching result → no extraction tasks, no SyncTypesAsync.
        // (result.TypesCount reflects importResult.SuccessCount from the mock,
        //  not the number of items matched by the orchestrator — so we assert
        //  on ExtractionTasks.Count and SyncTypesAsync call count instead.)
        Assert.Empty(result.ExtractionTasks);
        typeRepoMock.Verify(
            t => t.SyncTypesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<FamilyTypeDescriptor>>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "SyncTypesAsync must NOT be called when no matching result is found");

        if (File.Exists(stagedPath)) File.Delete(stagedPath);
        var dir = Path.GetDirectoryName(stagedPath);
        if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}

/// <summary>
/// ADR-040 regression tests for <see cref="LoadableFamilyImportOrchestrator"/>.
/// Verifies that the matching logic uses CatalogItemId (not filename) so
/// OverwriteCurrent results are correctly matched.
/// </summary>
public sealed class LoadableFamilyImportOrchestratorTests
{
    [Fact]
    public async Task ImportAndPersistTypesAsync_OverwriteCurrent_MatchesByCatalogItemId_CallsSyncTypesAsync()
    {
        // Arrange
        var existingCatalogItemId = "existing-load-id-adr040";
        var existingVersionLabel = "v1";
        var versionId = "version-id-load-adr040";
        var fileId = "file-id-load-adr040";
        var stagedPath = Path.Combine(Path.GetTempPath(), $"SmartConTest_{Guid.NewGuid():N}", "Fam.rfa");
        Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
        File.WriteAllText(stagedPath, "FAKE");

        var snapshot = new FamilySnapshot(
            FamilyName: "Fam",
            Category: "Pipe Fittings",
            Parameters: new List<FamilyParameterInfo>(),
            Types: new List<FamilyTypeSnapshot>
            {
                new("TypeA", new List<FamilyParameterValue>(), "uid-a"),
                new("TypeB", new List<FamilyParameterValue>(), "uid-b")
            },
            Geometry: new GeometryMetrics(0, new List<FormMetrics>(), 0, 0, 0, 0, 0, 0),
            SharedNestedFamilyNames: new List<string>());

        var item = new FamilyBatchImportItem(
            FilePath: stagedPath,
            FileName: "Fam",
            RevitMajorVersion: 2025,
            Status: FamilyBatchImportStatus.Existing,
            ExistingCatalogItemId: existingCatalogItemId,
            ExistingVersionLabel: existingVersionLabel,
            TargetCategoryId: null,
            TargetCategoryName: null,
            FamilySource: "loadable",
            TypeCount: 2,
            LoadableSnapshot: snapshot)
        {
            Action = FamilyBatchImportAction.OverwriteCurrent
        };

        var importServiceMock = new Mock<IFamilyImportService>();
        importServiceMock
            .Setup(s => s.ImportBatchAsync(It.IsAny<IReadOnlyList<FamilyBatchImportItem>>(), It.IsAny<string?>(), It.IsAny<IProgress<FamilyImportProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FamilyBatchImportResult(
                Results: new List<FamilyImportResult>
                {
                    new(true, existingCatalogItemId, versionId, fileId, "Fam", existingVersionLabel, null, stagedPath, false)
                },
                TotalFiles: 1,
                SuccessCount: 1,
                SkippedCount: 0,
                ErrorCount: 0));

        var typeRepoMock = new Mock<IFamilyTypeRepository>();
        var fileResolverMock = new Mock<IFamilyFileResolver>();
        fileResolverMock
            .Setup(r => r.ResolveForLoadAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FamilyResolvedFile(stagedPath, existingCatalogItemId, versionId, existingVersionLabel));

        var awaitableEventMock = new Mock<IFamilyManagerAwaitableEvent>();

        var orchestrator = new LoadableFamilyImportOrchestrator(
            importServiceMock.Object,
            new Mock<ILoadableFamilyTypeResolver>().Object,
            typeRepoMock.Object,
            fileResolverMock.Object,
            awaitableEventMock.Object);

        // Act
        var result = await orchestrator.ImportAndPersistTypesAsync(new[] { item }, 2025);

        // Assert
        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(0, result.SkippedCount);

        typeRepoMock.Verify(
            t => t.SyncTypesAsync(
                existingCatalogItemId,
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                "no-run",
                It.Is<IReadOnlyList<FamilyTypeDescriptor>>(list => list.Count == 2),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "SyncTypesAsync must be called once for OverwriteCurrent with matched CatalogItemId");

        if (File.Exists(stagedPath)) File.Delete(stagedPath);
        var dir = Path.GetDirectoryName(stagedPath);
        if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}
