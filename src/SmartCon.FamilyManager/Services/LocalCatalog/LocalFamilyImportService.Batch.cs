using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed partial class LocalFamilyImportService
{
    public async Task<FamilyBatchImportResult> ImportFolderAsync(FamilyFolderImportRequest request, IProgress<FamilyImportProgress>? progress, CancellationToken ct = default)
    {
        var searchOption = request.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = Directory.GetFiles(request.FolderPath, "*.rfa", searchOption);

        var results = new List<FamilyImportResult>();
        var successCount = 0;
        var skippedCount = 0;
        var errorCount = 0;

        for (var i = 0; i < files.Length; i++)
        {
            ct.ThrowIfCancellationRequested();

            var file = files[i];
            var fileName = Path.GetFileName(file);

            progress?.Report(new FamilyImportProgress(
                CurrentFileIndex: i,
                TotalFiles: files.Length,
                CurrentFileName: fileName,
                SuccessCount: successCount,
                SkippedCount: skippedCount,
                ErrorCount: errorCount));

            try
            {
                var detectedVersion = _fileInfoReader?.ReadRevitVersion(file) ?? request.RevitMajorVersion;
                var importRequest = new FamilyImportRequest(
                    FilePath: file,
                    RevitMajorVersion: detectedVersion,
                    Category: request.Category,
                    Tags: request.Tags,
                    Description: request.Description,
                    CategoryId: request.CategoryId,
                    PublishedBy: request.PublishedBy);

                var result = await ImportFileAsync(importRequest, ct);
                results.Add(result);

                if (result.WasSkipped)
                    skippedCount++;
                else if (result.Success)
                    successCount++;
                else
                    errorCount++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errorCount++;
                results.Add(new FamilyImportResult(
                    Success: false,
                    CatalogItemId: null,
                    VersionId: null,
                    FileId: null,
                    FileName: fileName,
                    VersionLabel: null,
                    ErrorMessage: ex.Message));
            }
        }

        progress?.Report(new FamilyImportProgress(
            CurrentFileIndex: files.Length - 1,
            TotalFiles: files.Length,
            CurrentFileName: "",
            SuccessCount: successCount,
            SkippedCount: skippedCount,
            ErrorCount: errorCount));

        return new FamilyBatchImportResult(
            Results: results,
            TotalFiles: files.Length,
            SuccessCount: successCount,
            SkippedCount: skippedCount,
            ErrorCount: errorCount);
    }

    public async Task<FamilyBatchImportResult> ImportBatchAsync(
        IReadOnlyList<FamilyBatchImportItem> items,
        string? categoryId,
        IProgress<FamilyImportProgress>? progress,
        CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope("LocalImport",
            ("Method", "ImportBatchAsync"),
            ("Count", items.Count));

        await _migrator.MigrateAsync(ct);

        var results = new List<FamilyImportResult>();
        var successCount = 0;
        var skippedCount = 0;
        var errorCount = 0;

        // v2.0.0: dedupe by (catalogItemId, versionLabel) so multiple
        // staged rows that resolve to the same canonical managed entry
        // (e.g. two system-family analyses that name the same category,
        // or a user re-running the import twice in the same dialog) only
        // produce ONE catalog_versions row. Otherwise we'd hit a SQLite
        // UNIQUE constraint failure on
        // (catalog_item_id, version_label, revit_major_version).
        var processedKeys = new HashSet<string>();
        var dedupedItems = new List<FamilyBatchImportItem>(items.Count);

        foreach (var item in items)
        {
            if (item.Action == FamilyBatchImportAction.Skip)
            {
                dedupedItems.Add(item);
                continue;
            }

            var key = BuildBatchDedupKey(item);
            if (key is null || processedKeys.Add(key))
            {
                dedupedItems.Add(item);
            }
            else
            {
                SmartConLogger.Warn(
                    $"ImportBatchAsync: dropping duplicate item '{item.FileName}' " +
                    $"for key '{key}' [Action: same family/version already queued in this batch]");
                // We still need to emit a synthetic "skipped" result so
                // the caller's per-item progress stays in sync.
                dedupedItems.Add(item with { Action = FamilyBatchImportAction.Skip });
            }
        }

        for (var i = 0; i < dedupedItems.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = dedupedItems[i];

            SmartConLogger.Info($"File: {item.FileName}, Status: {item.Status}, Action: {item.Action}");

            progress?.Report(new FamilyImportProgress(
                CurrentFileIndex: i,
                TotalFiles: dedupedItems.Count,
                CurrentFileName: item.FileName,
                SuccessCount: successCount,
                SkippedCount: skippedCount,
                ErrorCount: errorCount));

            if (item.Action == FamilyBatchImportAction.Skip)
            {
                skippedCount++;
                results.Add(new FamilyImportResult(
                    Success: true,
                    CatalogItemId: item.ExistingCatalogItemId,
                    VersionId: null,
                    FileId: null,
                    FileName: item.FileName,
                    VersionLabel: item.ExistingVersionLabel,
                    ErrorMessage: null,
                    WasSkipped: true));
                continue;
            }

            try
            {
                FamilyImportResult result;
                if (item.Status == FamilyBatchImportStatus.New)
                {
                    // Source-of-truth rule: when no category is assigned, write
                    // a NULL `Category` (and a NULL `CategoryId`) so the
                    // denormalised catalog_items.category_name column never
                    // accumulates a literal "Без категории" placeholder.
                    // The picker in FamilyBatchImportViewModel writes the
                    // placeholder string into TargetCategoryName when the user
                    // explicitly picks "no category" — the writer must
                    // translate that back to a real NULL, otherwise future
                    // batch dialogs would read back the placeholder and
                    // display it as if it were a real category name.
                    var effectiveCategoryId = item.TargetCategoryId ?? categoryId;
                    var effectiveCategoryName = effectiveCategoryId is null
                        ? null
                        : item.TargetCategoryName;
                    var request = new FamilyImportRequest(
                        FilePath: item.FilePath,
                        RevitMajorVersion: item.RevitMajorVersion,
                        Category: effectiveCategoryName,
                        Tags: null,
                        Description: null,
                        CategoryId: effectiveCategoryId,
                        FamilySource: item.FamilySource,
                        RevitCategory: item.RevitCategory,
                        FileName: item.FileName,
                        OriginalSourcePath: item.OriginalSourcePath,
                        PrecomputedCatalogItemId: item.PrecomputedCatalogItemId,
                        PrecomputedVersionLabel: item.PrecomputedVersionLabel,
                        PrecomputedManagedPath: item.PrecomputedManagedPath,
                        ContentHash: item.ContentHash,
                        HashFormatVersion: item.HashFormatVersion,
                        PublishedBy: item.PublishedByUser,
                        PreextractedGeometry: item.GeometryPerType,
                        RevitCategoryId: item.LoadableSnapshot?.CategoryId ?? item.SystemSnapshot?.CategoryId,
                        Facts: item.LoadableSnapshot?.Facts,
                        PerTypeHashes: item.PerTypeHashes,
                        Sections: item.Sections);
                    result = await ImportFileAsync(request, ct);
                }
                else
                {
                    if (item.Action == FamilyBatchImportAction.MakeActive)
                    {
                        // ADR-041: MakeActive only applies to Duplicate status.
                        // The content hash matched an existing version, so we
                        // just switch the active pointer — no file is saved to
                        // storage, no new version row is inserted.
                        if (string.IsNullOrEmpty(item.ExistingCatalogItemId) ||
                            string.IsNullOrEmpty(item.MatchedVersionLabel))
                        {
                            result = new FamilyImportResult(
                                Success: false,
                                CatalogItemId: item.ExistingCatalogItemId,
                                VersionId: null,
                                FileId: null,
                                FileName: item.FileName,
                                VersionLabel: item.MatchedVersionLabel,
                                ErrorMessage: "MakeActive requires ExistingCatalogItemId and MatchedVersionLabel. " +
                                              "The row's content hash did not match any catalog version.");
                        }
                        else
                        {
                            using var _maScope = SmartConLogger.BeginScope("LocalImport",
                                ("Method", "MakeActive"),
                                ("CatalogItemId", item.ExistingCatalogItemId),
                                ("VersionLabel", item.MatchedVersionLabel));
                            var setResult = await _catalogProvider.SetActiveVersionAsync(
                                item.ExistingCatalogItemId!, item.MatchedVersionLabel!, ct).ConfigureAwait(false);
                            result = new FamilyImportResult(
                                Success: setResult.Success,
                                CatalogItemId: item.ExistingCatalogItemId,
                                VersionId: null,
                                FileId: null,
                                FileName: item.FileName,
                                VersionLabel: item.MatchedVersionLabel,
                                ErrorMessage: setResult.ErrorMessage,
                                WasSkipped: true);
                            SmartConLogger.Info(
                                $"MakeActive: prev={(setResult.PreviousVersionLabel ?? "<null>")} " +
                                $"new={item.MatchedVersionLabel} success={setResult.Success} " +
                                $"hashSynced={setResult.ContentHashSynced}");
                        }
                    }
                    else if (item.Action == FamilyBatchImportAction.IncrementVersion)
                    {
                        // Source-of-truth rule: when no category is assigned
                        // (TargetCategoryId is null), do not write the picker
                        // placeholder literal into catalog_items.category_name.
                        // The DB column should stay NULL until the user picks
                        // a real category. We pass the real name only when
                        // a real CategoryId is present, mirroring the
                        // FamilyImportRequest branch above. UpdateFamilyAsync
                        // calls UpdateCatalogItemCategoryAsync only when
                        // CategoryId is not empty — EXCEPT when the user
                        // explicitly picked «Без категории» (#261: ClearCategory
                        // writes a real NULL, moving the item out of its
                        // current category).
                        var effectiveCategoryName = item.TargetCategoryId is null
                            ? null
                            : item.TargetCategoryName;
                        var request = new FamilyUpdateRequest(
                            CatalogItemId: item.ExistingCatalogItemId!,
                            FilePath: item.FilePath,
                            RevitMajorVersion: item.RevitMajorVersion,
                            CategoryId: item.TargetCategoryId,
                            CategoryName: effectiveCategoryName,
                            FileName: item.FileName,
                            OriginalSourcePath: item.OriginalSourcePath,
                            PrecomputedVersionLabel: item.PrecomputedVersionLabel,
                            PrecomputedManagedPath: item.PrecomputedManagedPath,
                            ContentHash: item.ContentHash,
                            HashFormatVersion: item.HashFormatVersion,
                        PublishedBy: item.PublishedByUser,
                        PreextractedGeometry: item.GeometryPerType,
                        RevitCategory: item.RevitCategory,
                        RevitCategoryId: item.LoadableSnapshot?.CategoryId ?? item.SystemSnapshot?.CategoryId,
                        Facts: item.LoadableSnapshot?.Facts,
                        PerTypeHashes: item.PerTypeHashes,
                        Sections: item.Sections,
                        ClearCategory: item.ClearCategoryOnImport);
                        result = await UpdateFamilyAsync(request, ct);
                    }
                    else
                    {
                        result = await OverwriteCurrentAsync(item, ct);
                    }
                }

                results.Add(result);
                if (result.Success) successCount++;
                else errorCount++;
            }
            catch (Exception ex)
            {
                errorCount++;
                SmartConLogger.Error(
                    $"ImportBatchAsync: file='{item.FileName}' threw: {ex.GetType().Name}: {ex.Message} " +
                    $"[Action: see prior log lines from ImportFileAsync for the underlying cause]");
                SmartConLogger.Debug($"ImportBatchAsync stack trace: {ex.StackTrace}");
                results.Add(new FamilyImportResult(
                    Success: false,
                    CatalogItemId: null,
                    VersionId: null,
                    FileId: null,
                    FileName: item.FileName,
                    VersionLabel: null,
                    ErrorMessage: ex.Message));
            }
        }

        progress?.Report(new FamilyImportProgress(
            CurrentFileIndex: dedupedItems.Count - 1,
            TotalFiles: dedupedItems.Count,
            CurrentFileName: string.Empty,
            SuccessCount: successCount,
            SkippedCount: skippedCount,
            ErrorCount: errorCount));

        return new FamilyBatchImportResult(
            Results: results,
            TotalFiles: dedupedItems.Count,
            SuccessCount: successCount,
            SkippedCount: skippedCount,
            ErrorCount: errorCount);
    }

    private static string? BuildBatchDedupKey(FamilyBatchImportItem item)
    {
        // v2.0.0: a batch row collides with a previous one when both
        // resolve to the same canonical managed file, i.e. the same
        // (catalogItemId, versionLabel, revit_major_version). Without
        // this guard the second row would trip the SQLite UNIQUE
        // constraint on catalog_versions.
        var id = item.PrecomputedCatalogItemId ?? item.ExistingCatalogItemId;
        var ver = item.PrecomputedVersionLabel
            ?? item.ExistingVersionLabel
            ?? "v1";
        if (string.IsNullOrEmpty(id)) return null;
        return $"{id}|{ver}|{item.RevitMajorVersion}";
    }
}
