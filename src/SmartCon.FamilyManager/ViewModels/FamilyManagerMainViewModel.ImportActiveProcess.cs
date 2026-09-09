using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using SmartCon.FamilyManager.Services.Import;
using SmartCon.UI;
using SmartCon.UI.Behaviors;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyManagerMainViewModel
{
    /// <summary>
    /// v2.0.0: Handle the active family document directly without any temp
    /// staging. The active document stays open across the import — SaveAs
    /// into managed storage does NOT close it. If the user cancels the
    /// batch dialog, the document is left untouched.
    /// </summary>
    private async Task ProcessFamilyImportAsync()
    {
        using var _ = SmartConLogger.BeginScope("FMImport",
            ("Method", "ProcessFamilyImportAsync"));

        var preparedItems = await _preparationService.PrepareActiveFamilyAsync(CancellationToken.None);
        // E2 (#209): [0] is the active family itself, the rest are its
        // shared-nested children (regular batch rows).
        var prepared = preparedItems[0];

        if (prepared.ErrorMessage is not null)
        {
            StatusMessage = $"Import error: {prepared.ErrorMessage}";
            return;
        }

        string? existingCategoryId = null;
        string? existingCategoryName = null;
        if (prepared.ExistingCatalogItemId is not null)
        {
            try
            {
                var existingItem = await _catalogProvider.GetItemAsync(prepared.ExistingCatalogItemId, CancellationToken.None);
                existingCategoryId = existingItem?.CategoryId;
                if (existingCategoryId is not null)
                {
                    var allCategories = await _categoryRepository.GetAllAsync(CancellationToken.None);
                    var categoriesById = allCategories.ToDictionary(c => c.Id);
                    if (categoriesById.TryGetValue(existingCategoryId, out var cat) && cat is not null)
                        existingCategoryName = cat.FullPath ?? cat.Name;
                }
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn($"Failed to resolve category: {ex.Message} [Action: проверьте, что БД каталога доступна]");
            }
        }

        var precomputed = await _importPrecomputer
            .BuildPrecomputedTripleAsync(prepared.DisplayName, ".rfa", prepared.FamilySource, prepared.ExistingCatalogItemId, CancellationToken.None)
            .ConfigureAwait(false);
        var precomputedCatalogItemId = precomputed?.CatalogItemId ?? Guid.NewGuid().ToString("N");
        var precomputedVersionLabel = precomputed?.VersionLabel ?? "v1";
        var precomputedManagedPath = precomputed?.ManagedPath;

        var placeholderFilePath = prepared.SourcePath;
        var status = prepared.ErrorMessage is not null
            ? FamilyBatchImportStatus.Error
            : prepared.Status;

        var item = new FamilyBatchImportItem(
            placeholderFilePath,
            prepared.DisplayName,
            prepared.RevitMajorVersion,
            status,
            prepared.ExistingCatalogItemId,
            prepared.ExistingVersionLabel,
            existingCategoryId,
            existingCategoryName,
            FamilySource: prepared.FamilySource,
            TypeCount: SnapshotExtractionMapper.ResolveTypeCount(
                prepared.LoadableSnapshot, prepared.SystemSnapshot, prepared.SourceTypes),
            RevitCategory: prepared.LoadableSnapshot?.Category,
            OriginalSourcePath: prepared.SourcePath,
            SourceTypes: null,
            Source: null,
            PrecomputedCatalogItemId: precomputedCatalogItemId,
            PrecomputedVersionLabel: precomputedVersionLabel,
            PrecomputedManagedPath: precomputedManagedPath,
            ContentHash: prepared.ContentHash?.HexString,
            HashFormatVersion: prepared.ContentHash?.FormatVersion,
            MatchedVersionLabel: prepared.MatchedVersionLabel,
            LoadableSnapshot: prepared.LoadableSnapshot,
            SystemSnapshot: prepared.SystemSnapshot,
            IsCrossNameDuplicate: prepared.IsCrossNameDuplicate,
            MatchedItemName: prepared.MatchedItemName,
            ExistingCategoryId: existingCategoryId,
            ExistingCategoryPath: existingCategoryName,
            HealthReport: prepared.HealthReport,
            PerTypeHashes: prepared.PerTypeHashes,
            Sections: prepared.Sections,
            UnsubstitutedMiniRouting: prepared.UnsubstitutedMiniRouting)
        {
            Action = status == FamilyBatchImportStatus.Duplicate
                ? FamilyBatchImportAction.Skip
                : FamilyBatchImportAction.IncrementVersion
        };

        // E2 (#209): shared-nested children of the active family as regular
        // batch rows via the shared mapping helper (same kind-aware action
        // defaults as UC-1: SharedNested + Existing → IncrementVersion).
        var dialogItems = new List<FamilyBatchImportItem> { item };
        if (preparedItems.Count > 1)
        {
            var childItems = await MapPreparedItemsToBatchItemsAsync(
                    preparedItems.Skip(1).ToList(), CancellationToken.None)
                .ConfigureAwait(false);
            dialogItems.AddRange(childItems);
        }

        using var vm = new FamilyBatchImportViewModel(
            dialogItems,
            _dialogService,
            _viewModelFactory,
            catalogProvider: _catalogProvider,
            importPrecomputer: _importPrecomputer,
            dedupService: _dedupService,
            dispatcher: _dispatcher,
            validationService: _validationService,
            autoAssignService: _autoAssignService,
            analyticsRepository: _contentHashAnalytics);
        if (_dialogService.ShowBatchImportDialog(vm) != true)
        {
            await _preparationService.CloseAllPreparedDocumentsAsync(CancellationToken.None);
            return;
        }

        var selectedItems = vm.GetResultItems();
        var toImport = selectedItems.Where(i => i.Action != FamilyBatchImportAction.Skip).ToList();

        // The parent row is identified by its file path (the active
        // document's path); every other row is a shared-nested child.
        var parentImport = toImport.FirstOrDefault(i =>
            string.Equals(i.FilePath, placeholderFilePath, StringComparison.Ordinal));
        // ADR-066 dedup-link bug (stress test 2026-08-11): the executor must
        // receive ALL child rows — including skipped-as-Duplicate ones —
        // because DependencyLinkPlanner records dedup-links for skipped
        // children that already exist in the catalog. Filtering them out
        // here meant a new parent version imported while all its nested
        // children were duplicates got NO dependency links → the dependency
        // drift badge never appeared for that parent.
        var childImports = selectedItems.Where(i =>
            !string.Equals(i.FilePath, placeholderFilePath, StringComparison.Ordinal)).ToList();

        if (parentImport is null && childImports.Count == 0)
        {
            await _preparationService.CloseAllPreparedDocumentsAsync(CancellationToken.None);
            return;
        }

        if (parentImport is null)
        {
            // Parent row skipped — import only the children. Self-heal
            // (stress test 2026-08-12): when the parent is a Duplicate OF
            // ITS CURRENT version (content identical), still rewrite the
            // current version's dependency links from the actual embedded
            // children — repairs parent versions imported before the
            // dedup-link fix, which have zero links and can never drift
            // (pair v3 / crane v2 in the owner's test catalog).
            var healParents = new Dictionary<string, string>(StringComparer.Ordinal);
            var skippedParentRow = selectedItems.FirstOrDefault(i =>
                string.Equals(i.FilePath, placeholderFilePath, StringComparison.Ordinal));
            if (skippedParentRow is { Status: FamilyBatchImportStatus.Duplicate, ExistingCatalogItemId: not null }
                && string.Equals(
                    skippedParentRow.MatchedVersionLabel,
                    skippedParentRow.ExistingVersionLabel,
                    StringComparison.OrdinalIgnoreCase))
            {
                healParents[placeholderFilePath] = skippedParentRow.ExistingCatalogItemId;
            }
            await ImportActiveFamilyChildrenAsync(
                    childImports, healParents.Count > 0 ? healParents : null)
                .ConfigureAwait(false);
            await _preparationService.CloseAllPreparedDocumentsAsync(CancellationToken.None);
            return;
        }

        var importItem = parentImport;

        var displayName = importItem.FileName;

        var postDialogPrecomputed = await _importPrecomputer
            .BuildPrecomputedTripleAsync(displayName, ".rfa", importItem.FamilySource, importItem.ExistingCatalogItemId, CancellationToken.None)
            .ConfigureAwait(false);
        var resolvedCatalogItemId = postDialogPrecomputed?.CatalogItemId
            ?? importItem.PrecomputedCatalogItemId
            ?? precomputedCatalogItemId
            ?? Guid.NewGuid().ToString("N");
        var resolvedVersionLabel = postDialogPrecomputed?.VersionLabel
            ?? importItem.PrecomputedVersionLabel
            ?? precomputedVersionLabel
            ?? "v1";
        var resolvedManagedPath = postDialogPrecomputed?.ManagedPath
            ?? importItem.PrecomputedManagedPath
            ?? precomputedManagedPath;

        var isOverwrite = importItem.Action == FamilyBatchImportAction.OverwriteCurrent
            && importItem.Status == FamilyBatchImportStatus.Existing
            && importItem.ExistingCatalogItemId is not null
            && importItem.ExistingVersionLabel is not null;

        // ADR-041 rev #2: MakeActive is a no-file-write operation. The
        // incoming file's content hash matched an existing version
        // (Duplicate status), so the .rfa is ALREADY on disk at
        // <ExistingCatalogItemId>/<MatchedVersionLabel>/<name>.rfa. We skip
        // EnsureFamilyDirectories, SaveAs, ExtractAttributesForLoadableTasks
        // and the ImportFileAsync path (which would INSERT a new
        // catalog_versions row — exactly the v3-on-duplicate bug rev #2 fixes).
        // ImportBatchAsync dispatches to SetActiveVersionAsync via the
        // MakeActive branch in LocalFamilyImportService.ImportBatchAsync.
        var isMakeActive = importItem.Action == FamilyBatchImportAction.MakeActive
            && importItem.Status == FamilyBatchImportStatus.Duplicate
            && !string.IsNullOrEmpty(importItem.ExistingCatalogItemId)
            && !string.IsNullOrEmpty(importItem.MatchedVersionLabel);

        if (isOverwrite)
        {
            resolvedVersionLabel = importItem.ExistingVersionLabel!;
            var overwritePath = _importService.ComputeManagedFilePath(
                importItem.ExistingCatalogItemId!,
                resolvedVersionLabel,
                SafeFileName.SanitizeFileName(displayName),
                ".rfa");
            if (!string.IsNullOrEmpty(overwritePath))
            {
                resolvedManagedPath = overwritePath;
            }
            resolvedCatalogItemId = importItem.ExistingCatalogItemId!;
        }

        if (!isMakeActive && string.IsNullOrEmpty(resolvedManagedPath))
        {
            StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_ImportErrorTitle) ?? "Import error";
            return;
        }

        string? saveAsPath = null;

        if (!isMakeActive)
        {
            _pathResolver.EnsureFamilyDirectories(resolvedCatalogItemId, resolvedVersionLabel);

            var activeDoc = await _awaitableEvent.RaiseAsync(app => _revitContext.GetDocument(), CancellationToken.None);

            saveAsPath = await _awaitableEvent.RaiseAsync<string?>(obj =>
            {
                try
                {
                    if (File.Exists(resolvedManagedPath!))
                    {
                        File.SetAttributes(resolvedManagedPath!, File.GetAttributes(resolvedManagedPath!) & ~FileAttributes.ReadOnly);
                        File.Delete(resolvedManagedPath!);
                    }

                    activeDoc.SaveAs(resolvedManagedPath!, new SaveAsOptions { OverwriteExistingFile = true });
                    File.SetAttributes(resolvedManagedPath!, File.GetAttributes(resolvedManagedPath!) | FileAttributes.ReadOnly);

                    return resolvedManagedPath;
                }
                catch (Exception ex)
                {
                    SmartConLogger.Error($"SaveAs to managed storage failed: {ex.Message}");
                    return null;
                }
            });

            if (string.IsNullOrEmpty(saveAsPath))
            {
                StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_ImportErrorTitle) ?? "Import error";
                return;
            }
        }

        var resolvedCategoryId = importItem.TargetCategoryId ?? existingCategoryId;
        var resolvedCategoryName = importItem.TargetCategoryName ?? existingCategoryName;

        var progress = new Progress<FamilyImportProgress>(p =>
        {
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportProgress) ?? "Importing {0} of {1}...",
                p.CurrentFileIndex + 1, p.TotalFiles);
        });

        // ADR-040: for OverwriteCurrent, route through ImportBatchAsync (which
        // dispatches to OverwriteCurrentAsync → UPDATE catalog_versions in place)
        // instead of ImportFileAsync (which always INSERTs a new catalog_versions
        // row and fails with UNIQUE constraint when versionLabel = ExistingVersionLabel).
        // For New/IncrementVersion, keep the existing ImportFileAsync path — it
        // creates a fresh catalog_versions row, which is correct for those actions.
        // ADR-041 rev #2: for MakeActive, route through ImportBatchAsync too —
        // it dispatches to SetActiveVersionAsync without touching disk or DB rows.
        FamilyImportResult importResult;
        if (isMakeActive)
        {
            // MatchedVersionLabel is the version the user wants to activate.
            // No precomputed managed path — the file is already at
            // <ExistingCatalogItemId>/<MatchedVersionLabel>/<name>.rfa from a
            // prior import. ImportBatchAsync's MakeActive branch ignores the
            // FilePath/ManagedPath fields and only reads ExistingCatalogItemId
            // + MatchedVersionLabel.
            var batchItem = new FamilyBatchImportItem(
                FilePath: placeholderFilePath,
                FileName: displayName,
                RevitMajorVersion: prepared.RevitMajorVersion,
                Status: FamilyBatchImportStatus.Duplicate,
                ExistingCatalogItemId: importItem.ExistingCatalogItemId,
                ExistingVersionLabel: importItem.ExistingVersionLabel,
                TargetCategoryId: resolvedCategoryId,
                TargetCategoryName: resolvedCategoryName,
                FamilySource: "loadable",
                TypeCount: importItem.TypeCount,
                RevitCategory: null,
                OriginalSourcePath: prepared.SourcePath,
                SourceTypes: null,
                Source: null,
                PrecomputedCatalogItemId: importItem.ExistingCatalogItemId,
                PrecomputedVersionLabel: importItem.MatchedVersionLabel,
                PrecomputedManagedPath: null,
                ContentHash: importItem.ContentHash,
                HashFormatVersion: importItem.HashFormatVersion,
                MatchedVersionLabel: importItem.MatchedVersionLabel,
                LoadableSnapshot: importItem.LoadableSnapshot,
                SystemSnapshot: null,
                PerTypeHashes: importItem.PerTypeHashes,
                Sections: importItem.Sections)
            {
                Action = FamilyBatchImportAction.MakeActive,
                PublishedByUser = _revitContext.GetUsername()
            };

            var batchResult = await _importService.ImportBatchAsync(
                new[] { batchItem }, resolvedCategoryId, progress, CancellationToken.None);
            importResult = batchResult.Results.Count > 0
                ? batchResult.Results[0]
                : new FamilyImportResult(
                    Success: false,
                    CatalogItemId: importItem.ExistingCatalogItemId,
                    VersionId: null,
                    FileId: null,
                    FileName: displayName,
                    VersionLabel: importItem.MatchedVersionLabel,
                    ErrorMessage: "ImportBatchAsync returned no results for MakeActive");
        }
        else if (isOverwrite)
        {
            var batchItem = new FamilyBatchImportItem(
                FilePath: saveAsPath!,
                FileName: displayName,
                RevitMajorVersion: prepared.RevitMajorVersion,
                Status: FamilyBatchImportStatus.Existing,
                ExistingCatalogItemId: resolvedCatalogItemId,
                ExistingVersionLabel: resolvedVersionLabel,
                TargetCategoryId: resolvedCategoryId,
                TargetCategoryName: resolvedCategoryName,
                FamilySource: "loadable",
                TypeCount: importItem.TypeCount,
                RevitCategory: importItem.RevitCategory,
                OriginalSourcePath: prepared.SourcePath,
                SourceTypes: null,
                Source: null,
                PrecomputedCatalogItemId: resolvedCatalogItemId,
                PrecomputedVersionLabel: resolvedVersionLabel,
                PrecomputedManagedPath: saveAsPath,
                ContentHash: importItem.ContentHash,
                HashFormatVersion: importItem.HashFormatVersion,
                MatchedVersionLabel: importItem.MatchedVersionLabel,
                LoadableSnapshot: importItem.LoadableSnapshot,
                SystemSnapshot: null,
                PerTypeHashes: importItem.PerTypeHashes,
                Sections: importItem.Sections)
            {
                Action = FamilyBatchImportAction.OverwriteCurrent,
                PublishedByUser = _revitContext.GetUsername()
            };

            var batchResult = await _importService.ImportBatchAsync(
                new[] { batchItem }, resolvedCategoryId, progress, CancellationToken.None);
            importResult = batchResult.Results.Count > 0
                ? batchResult.Results[0]
                : new FamilyImportResult(
                    Success: false,
                    CatalogItemId: resolvedCatalogItemId,
                    VersionId: null,
                    FileId: null,
                    FileName: displayName,
                    VersionLabel: resolvedVersionLabel,
                    ErrorMessage: "ImportBatchAsync returned no results for OverwriteCurrent");
        }
        else
        {
            var request = new FamilyImportRequest(
                FilePath: saveAsPath!,
                RevitMajorVersion: prepared.RevitMajorVersion,
                Category: resolvedCategoryName,
                Tags: null,
                Description: null,
                CategoryId: resolvedCategoryId,
                FamilySource: "loadable",
                RevitCategory: importItem.RevitCategory,
                FileName: displayName,
                OriginalSourcePath: prepared.SourcePath,
                PrecomputedCatalogItemId: resolvedCatalogItemId,
                PrecomputedVersionLabel: resolvedVersionLabel,
                PrecomputedManagedPath: saveAsPath,
                ContentHash: importItem.ContentHash,
                HashFormatVersion: importItem.HashFormatVersion,
                PublishedBy: _revitContext.GetUsername(),
                PreextractedGeometry: importItem.GeometryPerType,
                RevitCategoryId: importItem.LoadableSnapshot?.CategoryId ?? importItem.SystemSnapshot?.CategoryId,
                Facts: importItem.LoadableSnapshot?.Facts,
                PerTypeHashes: importItem.PerTypeHashes,
                Sections: importItem.Sections);

            importResult = await _importService.ImportFileAsync(request, CancellationToken.None);
        }

        // Phase 27B / ADR-036 Bug #2: LoadTreeAsync MUST run AFTER
        // ExtractAttributesForLoadableTasks, not before. Otherwise the tree
        // reloads with 0 types (extraction has not written them to the DB yet)
        // and never refreshes again — the user sees the family node but no
        // type children. This was the ADR-036 regression: in Phase A the old
        // FireAndForget ExtractAttributesForImportedFamilies was replaced by
        // the synchronous ExtractAttributesForLoadableTasks, but the
        // LoadTreeAsync call was left at its old pre-extraction position.
        // ADR-041 rev #2: skip extraction for MakeActive — the activated
        // version's types/attribute values are already in the DB from the
        // original import that created that version; re-extracting would
        // overwrite them with the active document's snapshot (which is
        // identical anyway), but it would also write to family_types with
        // a version_id that does not match the activated version's
        // catalog_versions.id (the snapshot is from the active doc, but
        // MakeActive didn't change the version handle). Avoid the
        // confusion: MakeActive = pointer switch only, no DB writes.
        if (importResult.Success && importItem.LoadableSnapshot is not null && !isMakeActive)
        {
            var attrTask = new LoadableFamilyAttributeTask(
                importResult.CatalogItemId!,
                saveAsPath!,
                importResult.VersionId,
                importResult.FileId,
                Snapshot: importItem.LoadableSnapshot);
            await ExtractAttributesForLoadableTasks(new[] { attrTask });
        }

        if (importResult.Success)
        {
            await LoadTreeAsync();
        }

        var total = 1;
        var success = importResult.Success ? 1 : 0;
        // ADR-041 rev #2: MakeActive returns WasSkipped=true from
        // ImportBatchAsync (status message should reflect "switched" not
        // "imported"). The BuildImportStatusMessage helper already accepts
        // a skipped count, so surface it here.
        var skipped = importResult.WasSkipped ? 1 : 0;
        var errors = importResult.Success ? 0 : 1;
        StatusMessage = BuildImportStatusMessage(success, skipped, errors, total);

        if (importResult.Success)
        {
            // CloseFamilyDocumentAsync expects the path of the document we
            // saved via SaveAs. For MakeActive there was no SaveAs (the file
            // is already on disk from a prior import), so use the active
            // document's source path (placeholderFilePath = prepared.SourcePath)
            // — the user was editing this .rfa and expects it to close after
            // the batch dialog confirms the action, exactly like
            // IncrementVersion/OverwriteCurrent close the editor.
            var pathToClose = saveAsPath ?? placeholderFilePath;
            await CloseFamilyDocumentAsync(pathToClose);

            // #185: automatic stale check after a single loadable import —
            // the work project (now active again) may hold markers of the
            // previous catalog version; the user must SEE "Обновить" is
            // needed without hunting for "Проверить". MakeActive is NOT
            // skipped (review M2): a rollback to an older version makes the
            // project's markers of the newer one stale — the check is honest.
            if (!string.IsNullOrEmpty(importResult.CatalogItemId))
            {
                await RunPostImportStaleCheckAsync(new[]
                {
                    new ImportedCatalogItem(
                        importResult.CatalogItemId!,
                        importItem.FileName,
                        "loadable")
                });
            }
        }

        // E2 (#209): import the shared-nested children AFTER the parent so
        // the dependency links land on the parent's NEW current version.
        // The nested documents are independent EditFamily copies (probe P3)
        // and survive the parent editor's close above.
        if (childImports.Count > 0)
        {
            var externalParents = new Dictionary<string, string>(StringComparer.Ordinal);
            if (importResult.Success && !string.IsNullOrEmpty(importResult.CatalogItemId))
            {
                externalParents[placeholderFilePath] = importResult.CatalogItemId!;
            }

            var childResult = await ImportActiveFamilyChildrenAsync(childImports, externalParents)
                .ConfigureAwait(false);
            if (childResult.SuccessCount > 0 || childResult.ErrorCount > 0)
            {
                StatusMessage = BuildImportStatusMessage(
                    success + childResult.SuccessCount,
                    skipped + childResult.SkippedCount,
                    errors + childResult.ErrorCount,
                    total + childImports.Count);
            }
        }

        await _preparationService.CloseAllPreparedDocumentsAsync(CancellationToken.None);
    }
}
