using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.Actualization;
using SmartCon.FamilyManager.Services.LocalCatalog;
using SmartCon.Tests.FamilyManager.Repository;
using SmartCon.Tests.TestDoubles;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// Tests for the actualization engine <see cref="CatalogActualizationService"/>
/// (ADR-054): union of task detections, ONE file open per family group,
/// per-task apply/failure routing, newer-Revit classification, cancel,
/// file-free passes, and the missing-file purge.
/// </summary>
public sealed class CatalogActualizationServiceTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly FakeExtractor _extractor = new();
    private readonly FakeActualizationTask _taskA = new("task-a", order: 10, isCritical: true);
    private readonly FakeActualizationTask _taskB = new("task-b", order: 20, isCritical: false);
    private readonly CatalogActualizationService _sut;

    public CatalogActualizationServiceTests()
    {
        _fixture = new TempCatalogFixture();
        _sut = new CatalogActualizationService(
            _fixture.GetDatabase(),
            _fixture.GetPathResolver(),
            _extractor,
            _fixture.GetProvider(),
            _fixture.GetProvider(),
            new LocalFamilyDependencyRepository(_fixture.GetDatabase()),
            new IDatabaseActualizationTask[] { _taskA, _taskB });
    }

    public void Dispose() => _fixture.Dispose();

    private sealed class FakeExtractor : IFamilyMigrationExtractor
    {
        public Queue<FamilyMigrationExtractResult> Results { get; } = new();
        public List<string> OpenedPaths { get; } = new();
        public List<string> SystemCategoryPaths { get; } = new();
        public FamilySnapshot? DefaultSnapshot { get; set; }

        public Task<FamilyMigrationExtractResult> ExtractLoadableAsync(
            string absolutePath, CancellationToken ct = default)
            => ExtractLoadableWithGeometryAsync(absolutePath, ct);

        public Task<FamilyMigrationExtractResult> ExtractLoadableWithGeometryAsync(
            string absolutePath, CancellationToken ct = default)
        {
            OpenedPaths.Add(absolutePath);
            var result = Results.Count > 0
                ? Results.Dequeue()
                : FamilyMigrationExtractResult.Ok(DefaultSnapshot!);
            return Task.FromResult(result);
        }

        public Task<FamilyMigrationExtractResult> ExtractSystemCategoryAsync(
            string absolutePath, CancellationToken ct = default)
        {
            SystemCategoryPaths.Add(absolutePath);
            var result = Results.Count > 0
                ? Results.Dequeue()
                : FamilyMigrationExtractResult.Ok(DefaultSnapshot!);
            return Task.FromResult(result);
        }
    }

    [Fact]
    public async Task CountBreakdown_SumsByTier_ResilientToBrokenTask()
    {
        _taskA.PendingCount = 3;
        _taskA.NewerPending = new NewerOnlyPendingInfo(2, 2026);
        _taskB.PendingCount = 5;
        _taskB.NewerPending = new NewerOnlyPendingInfo(1, 2025);
        var broken = new FakeActualizationTask("broken", 30) { CountException = new InvalidOperationException("io") };
        var sut = new CatalogActualizationService(
            _fixture.GetDatabase(), _fixture.GetPathResolver(), _extractor,
            _fixture.GetProvider(), _fixture.GetProvider(),
            new LocalFamilyDependencyRepository(_fixture.GetDatabase()),
            new IDatabaseActualizationTask[] { _taskA, _taskB, broken });

        var breakdown = await sut.CountPendingBreakdownAsync(2025);

        Assert.Equal(3, breakdown.Critical);
        Assert.Equal(5, breakdown.Optional);
        Assert.Equal(2, breakdown.NewerOnlyCritical);
        Assert.Equal(1, breakdown.NewerOnlyOptional);
        Assert.Equal(2026, breakdown.NewerOnlyCriticalRequiredRevitVersion);
        Assert.Equal(8, breakdown.TotalProcessable);
        Assert.Equal(5, breakdown.TotalCritical);
    }

    [Fact]
    public async Task Run_UnionsDetections_OneOpenPerGroup_AppliesOnlyPendingTasks()
    {
        var (itemA, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA");
        var (itemB, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamB");
        _extractor.DefaultSnapshot = CatalogSeedHelper.CreateSnapshot();
        _taskA.PendingKeys.Add(itemA + "|v1");                 // only A
        _taskB.PendingKeys.Add(itemA + "|v1");
        _taskB.PendingKeys.Add(itemB + "|v1");                 // A and B

        var result = await _sut.RunAllPendingAsync(2025, null, CancellationToken.None);

        Assert.Equal(2, result.UpdatedCount);
        Assert.Equal(2, _extractor.OpenedPaths.Count);         // one open per group
        Assert.Equal(new[] { itemA + "|v1" }, _taskA.AppliedGroupKeys);
        Assert.Equal(2, _taskB.ApplyCalls);
        Assert.Empty(result.MissingFiles);
        Assert.Empty(result.FailedFiles);
    }

    [Fact]
    public async Task Run_GroupNotInAnyDetection_NotOpened()
    {
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA");
        _extractor.DefaultSnapshot = CatalogSeedHelper.CreateSnapshot();

        var result = await _sut.RunAllPendingAsync(2025, null, CancellationToken.None);

        Assert.Equal(0, result.UpdatedCount);
        Assert.Empty(_extractor.OpenedPaths);
    }

    [Fact]
    public async Task Run_SystemGroup_UsesSystemCategoryExtraction()
    {
        var (itemS, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "Pipes", familySource: "system");
        _extractor.DefaultSnapshot = new FamilySnapshot(
            "Pipes", "Трубы", [], [], new GeometryMetrics(0, []), []);
        _taskA.PendingKeys.Add(itemS + "|v1");

        var result = await _sut.RunAllPendingAsync(2025, null, CancellationToken.None);

        Assert.Equal(1, result.UpdatedCount);
        Assert.Single(_extractor.SystemCategoryPaths);
        Assert.Empty(_extractor.OpenedPaths);
        Assert.Equal(1, _taskA.ApplyCalls);
    }

    [Fact]
    public async Task Run_MissingFile_ReportedAndTasksNotified()
    {
        var (itemA, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA", createFileOnDisk: false);
        _taskA.PendingKeys.Add(itemA + "|v1");
        _taskB.PendingKeys.Add(itemA + "|v1");

        var result = await _sut.RunAllPendingAsync(2025, null, CancellationToken.None);

        Assert.Single(result.MissingFiles);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Empty(_extractor.OpenedPaths);
        Assert.Equal(0, _taskA.ApplyCalls);
        Assert.Equal((itemA + "|v1", ActualizationFailureKind.MissingFile), Assert.Single(_taskA.Failures));
        Assert.Equal((itemA + "|v1", ActualizationFailureKind.MissingFile), Assert.Single(_taskB.Failures));
    }

    [Fact]
    public async Task Run_ExtractionFailed_ReportedAndTasksNotified()
    {
        var (itemA, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA");
        _extractor.Results.Enqueue(FamilyMigrationExtractResult.Fail("corrupt file"));
        _taskA.PendingKeys.Add(itemA + "|v1");

        var result = await _sut.RunAllPendingAsync(2025, null, CancellationToken.None);

        Assert.Single(result.FailedFiles);
        Assert.Equal((itemA + "|v1", ActualizationFailureKind.ExtractionFailed), Assert.Single(_taskA.Failures));
        Assert.Equal(0, _taskA.ApplyCalls);
    }

    [Fact]
    public async Task Run_TaskApplyThrows_OtherTasksStillApply_GroupNotCounted()
    {
        var (itemA, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA");
        _extractor.DefaultSnapshot = CatalogSeedHelper.CreateSnapshot();
        _taskA.PendingKeys.Add(itemA + "|v1");
        _taskA.ApplyException = new InvalidOperationException("db locked");
        _taskB.PendingKeys.Add(itemA + "|v1");

        var result = await _sut.RunAllPendingAsync(2025, null, CancellationToken.None);

        Assert.Equal(0, result.UpdatedCount);   // partial failure — group not counted
        Assert.Equal(1, _taskB.ApplyCalls);     // the healthy task still applied
    }

    [Fact]
    public async Task Run_NewerRevitOnly_LeftPendingAndCounted()
    {
        var (itemA, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA", revitVersion: 2026);
        _taskA.PendingKeys.Add(itemA + "|v1");

        var result = await _sut.RunAllPendingAsync(2025, null, CancellationToken.None);

        Assert.Equal(1, result.NewerRevitCount);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Empty(_extractor.OpenedPaths);
        Assert.Equal(0, _taskA.ApplyCalls);
    }

    [Fact]
    public async Task Run_CancelledBeforeStart_NothingProcessed()
    {
        var (itemA, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA");
        _taskA.PendingKeys.Add(itemA + "|v1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await _sut.RunAllPendingAsync(2025, null, cts.Token);

        Assert.True(result.WasCancelled);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Empty(_extractor.OpenedPaths);
    }

    [Fact]
    public async Task Run_FileFreePass_CountsIntoUpdated_AndRunsBeforeFiles()
    {
        var (itemA, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA");
        _extractor.DefaultSnapshot = CatalogSeedHelper.CreateSnapshot();
        _taskA.FileFreeResult = 7;
        _taskB.PendingKeys.Add(itemA + "|v1");

        var result = await _sut.RunAllPendingAsync(2025, null, CancellationToken.None);

        Assert.Equal(1, _taskA.FileFreeCalls);
        Assert.Equal(7 + 1, result.UpdatedCount);
    }

    // ---- RequiresExtraction (audit B3: file-level tasks like
    // mini-project-marker-v1 own open/save — the engine must not open the
    // file for them, and extraction failures must not couple into their
    // terminal markers) ----

    [Fact]
    public async Task Run_FileLevelOnlyTask_ExtractionSkipped_TaskApplied()
    {
        var (itemS, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "Pipes", familySource: "system");
        var fileLevel = new FakeActualizationTask("file-level", order: 60, isCritical: false)
        {
            RequiresExtraction = false,
        };
        fileLevel.PendingKeys.Add(itemS + "|v1");
        var sut = new CatalogActualizationService(
            _fixture.GetDatabase(), _fixture.GetPathResolver(), _extractor,
            _fixture.GetProvider(), _fixture.GetProvider(),
            new LocalFamilyDependencyRepository(_fixture.GetDatabase()),
            new IDatabaseActualizationTask[] { fileLevel });

        var result = await sut.RunAllPendingAsync(2025, null, CancellationToken.None);

        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(1, fileLevel.ApplyCalls);
        Assert.Empty(_extractor.OpenedPaths);
        Assert.Empty(_extractor.SystemCategoryPaths);
    }

    [Fact]
    public async Task Run_ExtractionFailed_FileLevelTaskStillApplied_WithoutFailureNotice()
    {
        var (itemA, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA");
        _extractor.Results.Enqueue(FamilyMigrationExtractResult.Fail("corrupt file"));
        _taskA.PendingKeys.Add(itemA + "|v1");   // extraction-based task
        var fileLevel = new FakeActualizationTask("file-level", order: 60, isCritical: false)
        {
            RequiresExtraction = false,
        };
        fileLevel.PendingKeys.Add(itemA + "|v1");
        var sut = new CatalogActualizationService(
            _fixture.GetDatabase(), _fixture.GetPathResolver(), _extractor,
            _fixture.GetProvider(), _fixture.GetProvider(),
            new LocalFamilyDependencyRepository(_fixture.GetDatabase()),
            new IDatabaseActualizationTask[] { _taskA, fileLevel });

        var result = await sut.RunAllPendingAsync(2025, null, CancellationToken.None);

        Assert.Single(result.FailedFiles);
        Assert.Equal((itemA + "|v1", ActualizationFailureKind.ExtractionFailed), Assert.Single(_taskA.Failures));
        Assert.Equal(0, _taskA.ApplyCalls);
        // The file-level task is decoupled: applied with the sentinel
        // context, never notified about the extraction failure.
        Assert.Equal(1, fileLevel.ApplyCalls);
        Assert.Empty(fileLevel.Failures);
        Assert.Equal(1, result.UpdatedCount);
    }

    // ---- Purge (moved from the hash-recalculation service, ADR-054) ----

    [Fact]
    public async Task PurgeMissing_AllVersionsMissing_DeletesWholeItem()
    {
        var (itemId, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA", createFileOnDisk: false);
        var missing = new[] { new HashRecalculationMissingFile(itemId, "FamA", "v1", "FamA.rfa") };

        var result = await _sut.PurgeMissingAsync(missing, CancellationToken.None);

        Assert.Equal(1, result.DeletedItems);
        Assert.Equal(1, result.DeletedVersions);
        Assert.Equal(0, result.FailedDirectories);
        Assert.Empty(result.ResetRoutingLinks);
        Assert.Null(await _fixture.GetProvider().GetItemAsync(itemId));
    }

    [Fact]
    public async Task PurgeMissing_ActiveMissing_SwitchesActiveAndDeletesVersion()
    {
        // v1 is ACTIVE and its file is missing; v2 is intact (hash v2).
        var (itemId, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "FamA", versionLabel: "v1", currentLabel: "v1", createFileOnDisk: false);
        await SeedV2VersionAsync(itemId);
        var missing = new[] { new HashRecalculationMissingFile(itemId, "FamA", "v1", "FamA.rfa") };

        var result = await _sut.PurgeMissingAsync(missing, CancellationToken.None);

        Assert.Equal(0, result.DeletedItems);
        Assert.Equal(1, result.DeletedVersions);
        Assert.Equal(0, result.FailedDirectories);
        // #133: the user must be told the active version was switched.
        var switched = Assert.Single(result.SwitchedActiveVersions);
        Assert.Equal("FamA", switched.ItemName);
        Assert.Equal("v2", switched.NewActiveVersionLabel);

        var item = await _fixture.GetProvider().GetItemAsync(itemId);
        Assert.NotNull(item);
        Assert.Equal("v2", item!.CurrentVersionLabel);
        Assert.Equal("SEEDEDHASH", item.ContentHash);
        Assert.Equal(2, item.HashFormatVersion);
    }

    [Fact]
    public async Task PurgeMissing_DirectoryLocked_DeletesRowsDbOnlyAndReports()
    {
        var (itemId, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA", createFileOnDisk: false);

        var itemDir = Path.Combine(_fixture.GetDatabaseRoot(), "files", itemId);
        Directory.CreateDirectory(itemDir);
        var lockFile = Path.Combine(itemDir, "stale.tmp");
        await File.WriteAllTextAsync(lockFile, "locked");
        await using var lockHandle = new FileStream(lockFile, FileMode.Open, FileAccess.Read, FileShare.None);

        var missing = new[] { new HashRecalculationMissingFile(itemId, "FamA", "v1", "FamA.rfa") };
        var result = await _sut.PurgeMissingAsync(missing, CancellationToken.None);

        Assert.Equal(1, result.DeletedItems);
        Assert.Equal(1, result.DeletedVersions);
        Assert.Equal(1, result.FailedDirectories);
        Assert.Null(await _fixture.GetProvider().GetItemAsync(itemId));
        Assert.True(Directory.Exists(itemDir));
    }

    [Fact]
    public async Task PurgeMissing_ReferencedByDependency_DeletesItemAndResetsLinks()
    {
        // #133 (owner decision 2026-09-09): a missing fitting can never be
        // loaded into routing — the dependency link is dead weight. The purge
        // deletes the item; family_dependencies FK CASCADE resets the parent
        // links and the result reports the count (the UI warns about stale
        // project routing).
        var (parentId, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "ParentPipe");
        var (childId, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "FamA", createFileOnDisk: false);
        var dependencyRepository = new LocalFamilyDependencyRepository(_fixture.GetDatabase());
        await dependencyRepository.ReplaceForCurrentVersionAsync(parentId, new[]
        {
            new FamilyDependencyInfo(childId, FamilyDependencyKind.Routing, "FamA:Стандарт", 0),
        });
        var missing = new[] { new HashRecalculationMissingFile(childId, "FamA", "v1", "FamA.rfa") };

        var result = await _sut.PurgeMissingAsync(missing, CancellationToken.None);

        Assert.Equal(1, result.DeletedItems);
        Assert.Equal(1, result.DeletedVersions);
        Assert.Equal(0, result.FailedDirectories);
        var resetLink = Assert.Single(result.ResetRoutingLinks);
        Assert.Equal("FamA", resetLink.PurgedItemName);
        Assert.Equal("ParentPipe", resetLink.ParentItemName);
        Assert.Null(await _fixture.GetProvider().GetItemAsync(childId));
        // The parent survives; its dependency list no longer references the purged child.
        Assert.NotNull(await _fixture.GetProvider().GetItemAsync(parentId));
        var references = await dependencyRepository.GetReferencingParentsAsync(childId);
        Assert.Empty(references);
    }

    [Fact]
    public async Task LoadMissingRecordCandidates_MapsFields_OneRowPerItemAndLabel()
    {
        var (itemId, _, _, relPath) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA",
            hashFormatVersion: FamilyContentHashFormat.RecalculationMissing, createFileOnDisk: false);
        // A second Revit variant of the SAME label must not duplicate the row.
        await CatalogSeedHelper.SeedAdditionalVariantAsync(_fixture, itemId, "FamA", "v1", 2023);

        var candidates = await _sut.LoadMissingRecordCandidatesAsync();

        var candidate = Assert.Single(candidates);
        Assert.Equal(itemId, candidate.CatalogItemId);
        Assert.Equal("FamA", candidate.ItemName);
        Assert.Equal("v1", candidate.VersionLabel);
        Assert.Equal("FamA.rfa", candidate.FileName);
        Assert.Equal(relPath, candidate.RelativePath);
        Assert.Equal(2025, candidate.RevitVersion);
        Assert.Equal(MissingRecordReason.MarkedMissing, candidate.Reason);
    }

    [Fact]
    public async Task ScanForMissingFiles_FindsMarkedAndUnmarked_ReportsIncrementally()
    {
        // -2 marked → candidate WITHOUT touching the disk (marked branch)
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamMarked",
            hashFormatVersion: FamilyContentHashFormat.RecalculationMissing, createFileOnDisk: false);
        // healthy record with its file on disk
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamHealthy");
        // no marker + file deleted manually (Revit stayed open) → File.Exists branch
        var (orphanId, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "FamOrphan", createFileOnDisk: false);
        var reports = new List<MissingRecordScanProgress>();
        var progress = new Progress<MissingRecordScanProgress>(reports.Add);

        var found = await _sut.ScanForMissingFilesAsync(progress, CancellationToken.None);

        Assert.Equal(2, found);
        var withCandidates = reports.Where(r => r.Found is not null).ToList();
        Assert.Equal(2, withCandidates.Count);
        var marked = withCandidates.Single(r => r.Found!.CatalogItemId != orphanId);
        Assert.Equal(MissingRecordReason.MarkedMissing, marked.Found!.Reason);
        Assert.Equal(3, marked.Total);
        var orphan = withCandidates.Single(r => r.Found!.CatalogItemId == orphanId);
        Assert.Equal(MissingRecordReason.FileMissing, orphan.Found!.Reason);
        // every group is reported with its file name (dialog shows "X of Y — file")
        Assert.Equal(3, reports.Count);
        Assert.All(reports, r => Assert.False(string.IsNullOrEmpty(r.CurrentFileName)));
    }

    [Fact]
    public async Task ScanForMissingFiles_LabelWithSurvivingVariant_NotReported()
    {
        // The label's 2025 file exists; only an extra 2023 variant file is
        // absent — the label still works in its Revit version and must NOT
        // be purged (DeleteVersionAsync removes all variants of a label).
        var (itemId, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamHalf");
        await InsertVariantWithoutFileAsync(itemId, "FamHalf", 2023);

        var found = await _sut.ScanForMissingFilesAsync(null, CancellationToken.None);

        Assert.Equal(0, found);
    }

    [Fact]
    public async Task ScanForMissingFiles_CancelledBeforeStart_Throws()
    {
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _sut.ScanForMissingFilesAsync(null, cts.Token));
    }

    private async Task InsertVariantWithoutFileAsync(string itemId, string name, int revitVersion)
    {
        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        var fileId = Guid.NewGuid().ToString();
        var relativePath = $"files/{itemId}/v1/r{revitVersion}/{name}.rfa";
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO family_files (id, relative_path, file_name, revit_major_version, imported_at_utc)
                VALUES (@id, @path, @name, @revit, @t)
                """;
            cmd.Parameters.Add(new SqliteParameter("@id", fileId));
            cmd.Parameters.Add(new SqliteParameter("@path", relativePath));
            cmd.Parameters.Add(new SqliteParameter("@name", name + ".rfa"));
            cmd.Parameters.Add(new SqliteParameter("@revit", revitVersion));
            cmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
            await cmd.ExecuteNonQueryAsync();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO catalog_versions (id, catalog_item_id, file_id, version_label,
                                              revit_major_version, published_at_utc)
                VALUES (@id, @itemId, @fileId, 'v1', @revit, @t)
                """;
            cmd.Parameters.Add(new SqliteParameter("@id", Guid.NewGuid().ToString()));
            cmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
            cmd.Parameters.Add(new SqliteParameter("@fileId", fileId));
            cmd.Parameters.Add(new SqliteParameter("@revit", revitVersion));
            cmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private async Task SeedV2VersionAsync(string itemId)
    {
        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction();
        var fileId = Guid.NewGuid().ToString();
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
                                              revit_major_version, content_hash, hash_format_version, published_at_utc)
                VALUES (@id, @itemId, @fileId, 'v2', 2025, 'SEEDEDHASH', 2, @t)
                """;
            cmd.Parameters.Add(new SqliteParameter("@id", Guid.NewGuid().ToString()));
            cmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
            cmd.Parameters.Add(new SqliteParameter("@fileId", fileId));
            cmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
            await cmd.ExecuteNonQueryAsync();
        }
        tx.Commit();

        // The v2 file must exist on disk for DeleteVersionAsync of v1 to succeed.
        var absolute = Path.Combine(_fixture.GetDatabaseRoot(), $"files/{itemId}/v2/FamA.rfa");
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, "FAKE");
    }
}
