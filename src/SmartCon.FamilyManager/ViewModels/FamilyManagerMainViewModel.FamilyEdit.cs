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
    [RelayCommand(CanExecute = nameof(CanEditOps))]
    private async Task OpenCategoryEditorAsync()
    {
        using var _scope = SmartConLogger.BeginScope("FMEdit",
            ("Method", "OpenCategoryEditorAsync"));
        if (!await EnsureDatabaseUpToDateAsync().ConfigureAwait(true)) return;
        var editorVm = _viewModelFactory.CreateCategoryTreeEditorViewModel();
        await editorVm.InitializeAsync();
        _dialogService.ShowCategoryTreeEditor(editorVm);
        // Full-immediate editor: all mutations were committed live inside
        // the dialog — refresh the main tree once it closes.
        await LoadTreeAsync();
    }

    [RelayCommand]
    private async Task OpenProperties()
    {
        using var _scope = SmartConLogger.BeginScope("FMEdit",
            ("Method", "OpenProperties"));
        if (SelectedItem is null)
        {
            SmartConLogger.Warn("OpenProperties: SelectedItem is null — abort. [Action: select a family first]");
            return;
        }

        var itemId = SelectedItem.Id;
        var updatedAt = SelectedItem.UpdatedAtUtc != default
            ? SelectedItem.UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : null;

        SmartConLogger.Info($"OpenProperties: creating VM for itemId={itemId} name='{SelectedItem.Name}'...");
        FamilyPropertiesViewModel vm;
        try
        {
            vm = _viewModelFactory.CreatePropertiesViewModel(
                SelectedItem.Id,
                SelectedItem.Name,
                SelectedItem.Description,
                SelectedItem.CategoryId,
                SelectedItem.CategoryName,
                SelectedItem.Tags,
                SelectedItem.ContentStatus,
                SelectedItem.VersionLabel,
                null,
                updatedAt,
                SelectedItem.RevitCategory,
                isReadOnly: !CanEdit);
            SmartConLogger.Info("OpenProperties: VM created, calling InitializeCommand...");

            // ADR-047 rev 2 / #131: refresh the tree node's tooltip the moment the
            // avatar is re-cropped/removed inside the dialog — don't wait for the
            // post-OK LoadTreeAsync (which also doesn't run on Cancel).
            if (SelectedTreeNode is FamilyLeafNodeViewModel leaf)
                vm.AvatarChanged += () => leaf.TooltipViewModel.Invalidate();
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"OpenProperties: VM construction failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return;
        }

        try
        {
            vm.InitializeCommand.Execute(null);
            SmartConLogger.Info("OpenProperties: InitializeCommand dispatched, calling ShowProperties...");
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"OpenProperties: InitializeCommand threw: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return;
        }

        try
        {
            var result = _dialogService.ShowProperties(vm);
            SmartConLogger.Info($"OpenProperties: ShowProperties returned result={result}");

            // MakeActive on the Versions tab commits to the DB immediately —
            // even a Cancelled dialog may have changed the active version's
            // Revit major version, which drives the tree's availability badge.
            // E5 (#213): a deleted parent version frees dependency links —
            // the tree must rebuild to clear the freed child's paperclip.
            if (result != true && !vm.ActiveVersionChanged && !vm.VersionsChanged) return;

            await LoadTreeAsync();
            ExpandAndSelectItem(itemId);
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"OpenProperties: ShowProperties failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditOps))]
    private async Task EditFamilyAsync()
    {
        using var _scope = SmartConLogger.BeginScope("FMEdit",
            ("Method", "EditFamilyAsync"));
        if (SelectedTreeNode is not FamilyLeafNodeViewModel leaf) return;

        var resolved = await _fileResolver.ResolveForLoadAsync(
            leaf.CatalogItemId, CurrentRevitVersion, CancellationToken.None);
        if (string.IsNullOrEmpty(resolved.AbsolutePath))
        {
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_FamilyFileNotFound) ?? "Error",
                LanguageManager.GetString(StringLocalization.Keys.FM_FamilyFileNotFound) ?? "Family file not found in managed storage.");
            return;
        }

        await _awaitableEvent.RaiseAsync(obj =>
        {
            try
            {
                var app = (Autodesk.Revit.UI.UIApplication)obj;
                app.OpenAndActivateDocument(resolved.AbsolutePath);
            }
            catch (Exception ex)
            {
                SmartConLogger.Error($"EditFamily OpenAndActivateDocument failed: {ex.Message}");
            }
        });
    }

    [RelayCommand(CanExecute = nameof(CanEditOps))]
    private async Task ImportActiveFileAsync()
    {
        using var _scope = SmartConLogger.BeginScope("FMImport",
            ("Method", "ImportActiveFileAsync"));
        if (!await EnsureDatabaseUpToDateAsync().ConfigureAwait(true)) return;
        IsLoading = true;
        var sessionStart = DateTime.Now;
        try
        {
            SmartConLogger.LogSessionStart("ImportActiveFile");

            var kind = await _activeDocumentClassifier.ClassifyAsync();
            SmartConLogger.Info($"Active document kind: {kind}");

            switch (kind)
            {
                case ActiveDocumentKind.None:
                    _dialogService.ShowError(
                        LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Error",
                        LanguageManager.GetString(StringLocalization.Keys.FM_ActiveDocNotProject)
                            ?? "Активный документ не является проектом. Откройте проект Revit.");
                    return;

                case ActiveDocumentKind.Family:
                    await ProcessFamilyImportAsync();
                    break;

                case ActiveDocumentKind.Project:
                    string? capturedActivePath = null;
                    var isMiniProjectDoc = false;
                    var (systemAnalysesRaw, loadableFamilies) = await _awaitableEvent.RaiseAsync<(IReadOnlyList<CategoryAnalysis>, IReadOnlyList<LoadableFamilyInfo>)>(obj =>
                    {
                        try
                        {
                            var uiApp = (Autodesk.Revit.UI.UIApplication)obj;
                            var activeDoc = uiApp.ActiveUIDocument?.Document;
                            if (activeDoc is null) return (Array.Empty<CategoryAnalysis>(), Array.Empty<LoadableFamilyInfo>());

                            capturedActivePath = activeDoc.PathName;
                            // Stress test 2026-08-05: in a mini-project the
                            // pre-import confirmation is pure noise — one
                            // category, a couple of types, nothing "long" to
                            // warn about. Detected here (Revit thread) because
                            // the ES marker read needs the API context.
                            isMiniProjectDoc = _miniProjectMarker.IsMiniProject(activeDoc)
                                || MiniProjectPathPattern.IsMiniProjectPath(activeDoc.PathName);
                            var sys = _systemFamilyRevitOps.AnalyzeActiveProject(activeDoc);
                            var load = _loadableFamilyScanner.GetUniqueFamilies(activeDoc);
                            return (sys, load);
                        }
                        catch (Exception ex)
                        {
                            SmartConLogger.Error(
                                $"Analyze failed: {ex.Message}");
                            return (Array.Empty<CategoryAnalysis>(), Array.Empty<LoadableFamilyInfo>());
                        }
                    });

                    // ADR-027 Phase 2: categories whose placement API is missing
                    // on this Revit version (ceilings <2022, railings <2025) are
                    // excluded with a styled info dialog — a staged mini-project
                    // without placed instances is not a valid reference.
                    var systemAnalyses = ApplyPlacementVersionGate(systemAnalysesRaw);

                    var systemTypeCount = systemAnalyses.Sum(a => a.TypeCount);
                    var systemCategoryCount = systemAnalyses.Count;
                    var loadableCount = loadableFamilies.Count;

                    SmartConLogger.Info(
                        $"Phase 1 (fast): system={systemCategoryCount}cat/{systemTypeCount}types, loadable={loadableCount} families");

                    if (systemCategoryCount == 0 && loadableCount == 0)
                    {
                        _dialogService.ShowError(
                            LanguageManager.GetString(StringLocalization.Keys.FM_NoSystemFamiliesFound) ?? "Error",
                            LanguageManager.GetString(StringLocalization.Keys.FM_NoSystemFamiliesFound)
                                ?? "В проекте не найдено размещённых семейств для импорта");
                        return;
                    }

                    if (isMiniProjectDoc)
                    {
                        // Mini-project reimport (curator workflow): skip the
                        // confirmation — its purpose is to warn about a long
                        // import of a big REAL project.
                        SmartConLogger.Info("Phase 2: mini-project — confirmation skipped");
                    }
                    else
                    {
                        var confirmMessage = string.Format(
                            LanguageManager.GetString(StringLocalization.Keys.FM_ImportActiveConfirmMessage)
                                ?? "Импортировать в каталог: {0} системных категорий ({1} типов) и {2} загружаемых семейств?",
                            systemCategoryCount, systemTypeCount, loadableCount);
                        var confirmed = _dialogService.ShowConfirmation(
                            LanguageManager.GetString(StringLocalization.Keys.FM_ImportActiveConfirmTitle)
                                ?? "Импорт активного файла",
                            confirmMessage);
                        SmartConLogger.Info($"Phase 2: user confirmed={confirmed}");
                        if (!confirmed)
                        {
                            StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_Cancel) ?? "Отменено";
                            return;
                        }
                    }

                    var batchItems = await BuildActiveProjectBatchItemsAsync(systemAnalyses, loadableFamilies);
                    if (batchItems.Count == 0)
                    {
                        _dialogService.ShowError(
                            LanguageManager.GetString(StringLocalization.Keys.FM_ImportPrepareError) ?? "Error",
                            LanguageManager.GetString(StringLocalization.Keys.FM_NoSystemFamiliesFound) ?? "Не удалось подготовить семейства для импорта");
                        return;
                    }
                    var outcome = await ProcessProjectImportAsync(batchItems);
                    // #186: after a successful reimport close the reference
                    // mini-project and return focus to the work project —
                    // consistent with the .rfa flow (CloseFamilyDocumentAsync).
                    // A document without the mini-project ES marker (#188) is
                    // NEVER closed — that is the user's real work project.
                    if (outcome.ImportStarted && outcome.SuccessCount > 0
                        && !string.IsNullOrEmpty(capturedActivePath))
                    {
                        await CloseMiniProjectAfterImportAsync(capturedActivePath!);
                    }

                    // #185: automatic stale check right after the import —
                    // the work project (now active) holds markers of the
                    // previous catalog version; the user must SEE that
                    // "Обновить" is needed, without hunting for "Проверить".
                    if (outcome.ImportStarted && outcome.SuccessCount > 0)
                    {
                        await RunPostImportStaleCheckAsync(outcome.ImportedItems);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"FAILED: {ex}");
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Error",
                ex.Message);
        }
        finally
        {
            // v2.0.0: no temp staging, no document close. The active family
            // document remains open across the import — SaveAs into managed
            // storage does NOT close it. User sees the family file with its
            // PathName now pointing to the managed copy, but the document is
            // still editable in-place until they choose to close it.
            IsLoading = false;
            SmartConLogger.LogSessionEnd("ImportActiveFile", sessionStart);
        }
    }

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
            HealthReport: prepared.HealthReport)
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
            validationService: _validationService);
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
        var childImports = toImport.Where(i =>
            !string.Equals(i.FilePath, placeholderFilePath, StringComparison.Ordinal)).ToList();

        if (parentImport is null && childImports.Count == 0)
        {
            await _preparationService.CloseAllPreparedDocumentsAsync(CancellationToken.None);
            return;
        }

        if (parentImport is null)
        {
            // Parent row skipped (e.g. Duplicate) — import only the children.
            // No new parent version → no dependency links are written against
            // it (same semantics as the UC-1 planner).
            await ImportActiveFamilyChildrenAsync(childImports, externalParentItemIds: null)
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
            StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Import error";
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
                StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Import error";
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
                SystemSnapshot: null)
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
                SystemSnapshot: null)
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
                Facts: importItem.LoadableSnapshot?.Facts);

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

    /// <summary>
    /// E2 (#209): imports the shared-nested children of the active family
    /// through the regular file-batch executor — staging resolves the
    /// held-open <c>nested://</c> documents exactly like UC-1. The parent
    /// itself was imported through the bespoke active-document path, so its
    /// catalog id arrives via <paramref name="externalParentItemIds"/> and
    /// the executor's link write lands on the parent's current version.
    /// </summary>
    private async Task<FamilyBatchImportExecutionResult> ImportActiveFamilyChildrenAsync(
        IReadOnlyList<FamilyBatchImportItem> childImports,
        IReadOnlyDictionary<string, string>? externalParentItemIds)
    {
        var staging = new FileFamilyStagingService(
            _awaitableEvent, _preparationService, _importService);
        var executor = new FileFamilyBatchImportExecutor(
            staging,
            _importService,
            _familyDependencyRepository,
            _dataImportService,
            _sharedNestedRepository,
            CurrentRevitVersion);
        var result = await executor.ExecuteAsync(
                childImports, categoryId: null, progress: null, pauseGate: null,
                CancellationToken.None, externalParentItemIds)
            .ConfigureAwait(false);
        SmartConLogger.Info(
            $"Active-family nested import: success={result.SuccessCount}, " +
            $"skipped={result.SkippedCount}, errors={result.ErrorCount}");
        return result;
    }

    /// <summary>
    /// Issue #186: after a successful "Импорт активного файла" run closes the
    /// reference mini-project WITHOUT saving and returns focus to the user's
    /// work project — consistent with the .rfa flow
    /// (<see cref="CloseFamilyDocumentAsync"/>).
    /// </summary>
    /// <remarks>
    /// Safety contract (#188):
    /// <list type="bullet">
    /// <item>Only a document carrying the mini-project ES marker is eligible —
    ///       an unmarked document (the user's real work project) is NEVER
    ///       closed;</item>
    /// <item>closing is always <c>Close(false)</c> — saving would overwrite the
    ///       read-only reference version on disk (I-16);</item>
    /// <item><c>Document.Close</c> is forbidden on the ACTIVE document, so
    ///       focus first moves to a work project
    ///       (<see cref="WorkProjectSelector"/>); when none is open, a blank
    ///       project is created — <c>PostableCommand.Close</c> is deliberately
    ///       NOT used because its "Save changes?" prompt risks overwriting the
    ///       reference;</item>
    /// <item>"Импорт выделенных элементов" does not call this method — the
    ///       mini-project stays open.</item>
    /// </list>
    /// </remarks>
    private async Task CloseMiniProjectAfterImportAsync(string capturedMiniProjectPath)
    {
        using var _scope = SmartConLogger.BeginScope("FMImport",
            ("Method", nameof(CloseMiniProjectAfterImportAsync)),
            ("File", System.IO.Path.GetFileName(capturedMiniProjectPath)));
        try
        {
            await _awaitableEvent.RaiseAsync(obj =>
            {
                var uiApp = (Autodesk.Revit.UI.UIApplication)obj;
                var app = uiApp.Application;

                Document? capturedDoc = null;
                var openDocs = new List<OpenDocumentInfo>();
                foreach (Document d in app.Documents)
                {
                    // L1: the path fallback keeps legacy unmarked mini-projects
                    // out of the focus candidates as well (ES marker is the
                    // primary check, path pattern covers pre-#188 files).
                    var isMini = _miniProjectMarker.IsMiniProject(d)
                        || MiniProjectPathPattern.IsMiniProjectPath(d.PathName);
                    openDocs.Add(new OpenDocumentInfo(
                        d.PathName ?? string.Empty, d.IsFamilyDocument, d.IsLinked, isMini));
                    if (string.Equals(d.PathName, capturedMiniProjectPath, StringComparison.OrdinalIgnoreCase))
                    {
                        capturedDoc = d;
                    }
                }

                if (capturedDoc is null)
                {
                    SmartConLogger.Debug("Mini-project document not found among open documents (already closed?)");
                    return;
                }

                // #188: NEVER close a document that is not a marked SmartCon
                // mini-project — that would destroy unsaved work in the user's
                // real project. Two factors are required (adversarial review
                // M1): the ES marker AND the managed-storage path. A fork
                // (mini-project SaveAs'd to a user location as the seed of a
                // new work project) keeps the ES marker but leaves managed
                // storage — closing it would lose unsaved user work.
                if (!_miniProjectMarker.IsMiniProject(capturedDoc)
                    || !MiniProjectPathPattern.IsMiniProjectPath(capturedDoc.PathName))
                {
                    SmartConLogger.Info(
                        "Active document is not a marked managed-storage mini-project " +
                        "(no ES marker or outside managed storage) — leaving it open (work-project protection)");
                    return;
                }

                var activePath = uiApp.ActiveUIDocument?.Document?.PathName;
                if (string.Equals(activePath, capturedMiniProjectPath, StringComparison.OrdinalIgnoreCase))
                {
                    var workPath = WorkProjectSelector.SelectWorkProjectPath(openDocs, capturedMiniProjectPath);
                    if (workPath is not null)
                    {
                        try
                        {
                            uiApp.OpenAndActivateDocument(workPath);
                        }
                        catch (Exception activateEx)
                        {
                            SmartConLogger.Warn(
                                $"Activate work project failed: {activateEx.Message} " +
                                "[Action: переключитесь на рабочий проект в Revit вручную и закройте мини-проект без сохранения]");
                            return;
                        }
                    }
                    else
                    {
                        // No work project open: Document.Close is forbidden on
                        // the active document and PostableCommand.Close would
                        // ask "Save changes?" — a blank project takes focus so
                        // the reference can be closed without saving.
                        try
                        {
                            app.NewProjectDocument(UnitSystem.Metric);
                        }
                        catch (Exception newEx)
                        {
                            SmartConLogger.Warn(
                                $"NewProjectDocument failed: {newEx.Message} " +
                                "[Action: закройте мини-проект вручную БЕЗ сохранения]");
                            return;
                        }
                    }
                }

                try
                {
                    capturedDoc.Close(false);
                    SmartConLogger.Info("Reference mini-project closed without saving");
                }
                catch (Exception closeEx)
                {
                    SmartConLogger.Warn(
                        $"Mini-project close failed: {closeEx.Message} " +
                        "[Action: закройте мини-проект вручную БЕЗ сохранения]");
                }
            });
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"CloseMiniProjectAfterImportAsync failed: {ex.Message} " +
                "[Action: закройте мини-проект вручную БЕЗ сохранения — каталог уже содержит импортированную версию]");
        }
    }

    /// <summary>
    /// Switches focus back to the project (if one was open) and closes the
    /// family document that was just imported. Runs on the Revit UI thread
    /// via the awaitable external event. Tolerates missing documents
    /// gracefully — Revit may have already closed them.
    /// </summary>
    /// <remarks>
    /// Behaviour:
    /// <list type="bullet">
    /// <item>Project was open before Edit Family → switch focus to project, then close the family</item>
    /// <item>Only a family was open → post the Close command (Revit closes the active doc)</item>
    /// <item>Family already closed by user → no-op</item>
    /// </list>
    /// We pass the post-SaveAs managed path so we close exactly the document
    /// that was just written, regardless of whether SaveAs switched focus.
    /// </remarks>
    private async Task CloseFamilyDocumentAsync(string capturedFamilyPath)
    {
        using var _ = SmartConLogger.BeginScope("FMImport",
            ("Method", "CloseFamilyDocumentAsync"));
        try
        {
            await _awaitableEvent.RaiseAsync(obj =>
            {
                using var _uiScope = SmartConLogger.BeginScope("FMImport",
                    ("Method", "CloseFamilyDocumentAsync"),
                    ("Thread", "RevitUI"));
                var uiApp = (Autodesk.Revit.UI.UIApplication)obj;
                var app = uiApp.Application;
                var activeBeforeSwitch = uiApp.ActiveUIDocument?.Document?.PathName;

                var projectDoc = app.Documents.Cast<Document>()
                    .FirstOrDefault(d => !d.IsFamilyDocument && !d.IsLinked
                        && !string.IsNullOrEmpty(d.PathName)
                        && d.PathName != activeBeforeSwitch);

                if (projectDoc != null)
                {
                    try
                    {
                        uiApp.OpenAndActivateDocument(projectDoc.PathName);
                    }
                    catch (Exception activateEx)
                    {
                        SmartConLogger.Warn(
                            $"Activate project failed: {activateEx.Message} [Action: переключитесь на проект в Revit вручную]");
                        try
                        {
                            var closeCmd = RevitCommandId.LookupPostableCommandId(PostableCommand.Close);
                            uiApp.PostCommand(closeCmd);
                        }
                        catch { }
                    }
                }
                else
                {
                    try
                    {
                        var closeCmd = RevitCommandId.LookupPostableCommandId(PostableCommand.Close);
                        uiApp.PostCommand(closeCmd);
                        SmartConLogger.Info(
                            "No project to switch to — posted Close command");
                    }
                    catch (Exception postEx)
                    {
                        SmartConLogger.Warn(
                            $"PostCommand Close failed: {postEx.Message} [Action: закройте активный документ в Revit вручную]");
                    }
                }

                var activeAfterSwitch = uiApp.ActiveUIDocument?.Document?.PathName;
                if (!string.IsNullOrEmpty(capturedFamilyPath)
                    && !string.Equals(activeAfterSwitch, capturedFamilyPath, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var docToClose = app.Documents.Cast<Document>()
                            .FirstOrDefault(d => string.Equals(
                                d.PathName, capturedFamilyPath, StringComparison.OrdinalIgnoreCase));
                        if (docToClose != null && !docToClose.IsLinked)
                        {
                            docToClose.Close(false);
                            SmartConLogger.Debug(
                                $"Closed family file: {capturedFamilyPath}");
                        }
                        else
                        {
                            SmartConLogger.Debug(
                                "Family document not found in app.Documents (already closed?)");
                        }
                    }
                    catch (Exception closeEx)
                    {
                        SmartConLogger.Info(
                            $"Family close skipped: {closeEx.Message}");
                    }
                }
                else
                {
                    SmartConLogger.Debug(
                        $"Active document switched to '{activeAfterSwitch}' — no need to close family");
                }
            });
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"CloseFamilyDocumentAsync failed: {ex.Message} [Action: переключитесь на нужный документ в Revit вручную, каталог уже содержит импортированную запись]");
        }
    }

    /// <summary>Snapshot of an active family document captured on the Revit UI thread.</summary>
    /// <remarks>
    /// v2.0.0: <c>HasTypeCatalog</c> removed. ADR-033 bakes the Type Catalog
    /// into the managed .rfa at import time, so the active document no
    /// longer needs to advertise whether a sidecar exists.
    /// </remarks>
    private sealed record ActiveFamilySnapshot(
        Document? Document,
        string BaseName,
        int RevitVersion,
        string? OriginalPathName);

    private async Task<ProjectImportOutcome> ProcessProjectImportAsync(List<FamilyBatchImportItem> batchItems)
    {
        using var _ = SmartConLogger.BeginScope("FMImport",
            ("Method", "ProcessProjectImportAsync"));
        if (batchItems.Count == 0)
        {
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportPrepareError) ?? "Error",
                LanguageManager.GetString(StringLocalization.Keys.FM_NoSystemFamiliesFound) ?? "No families found");
            return ProjectImportOutcome.NotStarted;
        }

        // ProcessProjectImportAsync is invoked from:
        //   1) "Импорт активного файла" (case Project)
        //   2) "Импорт выделенных элементов"
        // Neither path carries a user-selected category intent — the user did
        // not click "Импорт в категорию". The default behaviour in this flow
        // matches the legacy ImportFiles command: every row opens with
        // "Без категории" and the user can pick a target per row, or leave
        // it empty to keep the family un-categorised. The "Импорт в категорию"
        // command is a SEPARATE entry point (ImportFileToCategoryAsync) that
        // resolves defaultCategoryId from SelectedTreeNode — see
        // FamilyManagerMainViewModel.Import.cs:ImportFileToCategoryAsync.
        if (_batchDialogOpen)
        {
            SmartConLogger.Warn(
                "Batch import dialog is already open — ignoring re-entry " +
                "[Action: дождитесь завершения текущего импорта или закройте его диалог]");
            return ProjectImportOutcome.NotStarted;
        }
        _batchDialogOpen = true;

        try
        {
        var staging = new ProjectFamilyStagingService(
            _awaitableEvent,
            _preparationService,
            _revitContext,
            _systemFamilyIsolationProject,
            _importService,
            _databaseManager);
        var executor = new ProjectFamilyBatchImportExecutor(
            staging,
            _systemFamilyImportOrchestrator,
            _systemFamilyAttributeExtractor,
            _loadableFamilyImportOrchestrator,
            _dataImportService,
            _sharedNestedRepository,
            _versionWriter,
            _staleDetector,
            _catalogProvider,
            _familyDependencyRepository,
            CurrentRevitVersion);

        using var vm = new FamilyBatchImportViewModel(
            batchItems,
            _dialogService,
            _viewModelFactory,
            defaultCategoryId: null,
            defaultCategoryName: null,
            _catalogProvider,
            importPrecomputer: _importPrecomputer,
            dedupService: _dedupService,
            executor: executor,
            publishedByUser: _revitContext.GetUsername(),
            dispatcher: _dispatcher,
            validationService: _validationService);

        _dialogService.ShowModelessBatchImportDialog(vm);
        await vm.DialogCompletion;

        if (!vm.ImportStarted)
        {
            await _preparationService.CloseAllPreparedDocumentsAsync(CancellationToken.None);
            return ProjectImportOutcome.NotStarted;
        }

        if (vm.ImportSuccessCount > 0)
        {
            await LoadTreeAsync();
        }

        StatusMessage = string.Format(
            LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_SummaryFormat)
                ?? "Импортировано: {0}, пропущено: {1}, ошибок: {2}",
            vm.ImportSuccessCount, vm.ImportSkippedCount, vm.ImportErrorCount);

        // #185/#186: collect the catalog items this run touched so callers can
        // run post-import actions (safe mini-project close, stale check).
        var importedItems = batchItems
            .Where(i => i.Action != FamilyBatchImportAction.Skip)
            .Select(i => new { Id = i.PrecomputedCatalogItemId ?? i.ExistingCatalogItemId, Item = i })
            .Where(x => !string.IsNullOrEmpty(x.Id))
            .Select(x => new ImportedCatalogItem(x.Id!, x.Item.FileName, x.Item.FamilySource))
            .ToList();
        return new ProjectImportOutcome(true, vm.ImportSuccessCount, importedItems);
        }
        finally
        {
            _batchDialogOpen = false;
        }
    }

    private async Task ExtractAttributesForLoadableTasks(IReadOnlyList<LoadableFamilyAttributeTask> tasks)
    {
        using var _scope = SmartConLogger.BeginScope("FMLoadable",
            ("Method", "ExtractAttributesForLoadableTasks"),
            ("Count", tasks.Count));
        var helper = new LoadableAttributeExtractionHelper(
            _dataImportService, _sharedNestedRepository, CurrentRevitVersion);
        foreach (var task in tasks)
        {
            await helper.ExtractAsync(task, CancellationToken.None);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditOps))]
    private async Task EditSystemFamilyAsync()
    {
        using var _scope = SmartConLogger.BeginScope("FMEdit",
            ("Method", "EditSystemFamilyAsync"));
        if (SelectedTreeNode is not FamilyLeafNodeViewModel leaf) return;
        if (leaf.FamilySource != "system") return;

        var resolved = await _fileResolver.ResolveForLoadAsync(
            leaf.CatalogItemId, CurrentRevitVersion, CancellationToken.None);
        if (string.IsNullOrEmpty(resolved.AbsolutePath))
        {
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_FamilyFileNotFound) ?? "Error",
                LanguageManager.GetString(StringLocalization.Keys.FM_FamilyFileNotFound) ?? "System family file not found in managed storage.");
            return;
        }

        await _awaitableEvent.RaiseAsync(obj =>
        {
            try
            {
                var app = (Autodesk.Revit.UI.UIApplication)obj;
                app.OpenAndActivateDocument(resolved.AbsolutePath);
            }
            catch (Exception ex)
            {
                SmartConLogger.Error($"OpenAndActivateDocument failed: {ex.Message}");
            }
        });
    }

    [RelayCommand(CanExecute = nameof(CanEditOps))]
    private async Task DeleteFamilyAsync()
    {
        using var _scope = SmartConLogger.BeginScope("FMEdit",
            ("Method", "DeleteFamilyAsync"));
        if (SelectedItem is null) return;
        if (!await EnsureDatabaseUpToDateAsync().ConfigureAwait(true)) return;

        // E5 (#213, ADR-067): an item referenced by ANY version of ANY
        // parent cannot be deleted — every stored parent version must stay
        // self-sufficient. Release path: delete the referencing parent
        // versions (properties dialog) or the parents themselves.
        IReadOnlyList<FamilyDependencyReference> dependencyReferences;
        try
        {
            dependencyReferences = await _familyDependencyRepository
                .GetReferencingParentsAsync(SelectedItem.Id)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Fail-safe: a guard we cannot evaluate blocks the deletion.
            SmartConLogger.Error(
                $"Dependency guard read failed for '{SelectedItem.Name}': {ex.GetType().Name}: {ex.Message} " +
                "[Action: deletion blocked; check the catalog database, then retry]");
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_DependencyGuard_Title) ?? "Deletion blocked",
                ex.Message);
            return;
        }
        if (dependencyReferences.Count > 0)
        {
            var lines = DependencyGuardText.FormatReferenceLines(dependencyReferences);
            SmartConLogger.Info(
                $"Delete blocked by dependency guard: {dependencyReferences.Count} reference(s) — " +
                string.Join("; ", lines));
            _dialogService.ShowInfo(
                LanguageManager.GetString(StringLocalization.Keys.FM_DependencyGuard_Title) ?? "Deletion blocked",
                string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_DependencyGuard_Body) ??
                        "Cannot delete \"{0}\": the item is referenced as a dependency:\n{1}",
                    SelectedItem.Name,
                    string.Join(Environment.NewLine, lines.Select(l => "• " + l))));
            return;
        }

        var confirmed = _dialogService.ShowConfirmation(
            LanguageManager.GetString(StringLocalization.Keys.FM_FamilyDeleteTitle) ?? "Delete Family",
            string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_FamilyDeletePrompt) ?? "Delete \"{0}\"?",
                SelectedItem.Name));

        if (!confirmed) return;

        IsLoading = true;
        try
        {
            var success = await _writableProvider.DeleteItemAsync(SelectedItem.Id);
            if (success)
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_FamilyDeleted) ?? "Deleted: {0}",
                    SelectedItem.Name);
                await LoadTreeAsync();
            }
        }
        catch (IOException ex)
        {
            try
            {
                await _preparationService.LogOpenRevitDocumentsStateAsync(
                    "DeleteFamilyAsync.IOException",
                    SelectedItem?.Id);
            }
            catch (Exception logEx)
            {
                SmartConLogger.Debug(
                    $"LogOpenRevitDocumentsStateAsync failed during DeleteFamilyAsync catch: " +
                    $"{logEx.GetType().Name}: {logEx.Message}");
            }

            SmartConLogger.Error(
                $"DeleteFamilyAsync IOException for id='{SelectedItem?.Id}', name='{SelectedItem?.Name}': {ex.Message} " +
                "[Action: check LogOpenRevitDocumentsStateAsync output above to identify which open Document holds the lock]");

            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_FamilyDeleteError) ?? "Error",
                LanguageManager.GetString(StringLocalization.Keys.FM_FamilyDeleteInUse) ?? "Failed to delete family files. The file may be open in Revit or another application. Close the file and try again.");
            StatusMessage = $"{LanguageManager.GetString(StringLocalization.Keys.FM_FamilyDeleteError) ?? "Error"}: {ex.Message}";
            await LoadTreeAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"{LanguageManager.GetString(StringLocalization.Keys.FM_FamilyDeleteError) ?? "Error"}: {ex.Message}";
            await LoadTreeAsync();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task MoveFamilyToCategoryAsync(string familyId, string? targetCategoryId)
    {
        if (!CanEdit)
        {
            SmartConLogger.Warn($"MoveFamilyToCategoryAsync blocked: user lacks edit permissions [Action: обратитесь к владельцу БД каталога через окно Users для получения прав на редактирование]");
            return;
        }

        IsLoading = true;
        try
        {
            await _writableProvider.UpdateItemAsync(familyId, null, null, targetCategoryId, null, null);
            await LoadTreeAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Error: {0}",
                ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartDrag))]
    private void StartDrag(object? item)
    {
        // Drag permission gate. Behavior handles the actual DoDragDrop.
    }

    private bool CanStartDrag(object? item) => item is FamilyLeafNodeViewModel && CanEdit;

    [RelayCommand(CanExecute = nameof(CanDropFamily))]
    private async Task DropFamilyAsync(TreeViewDropInfo? info)
    {
        using var _scope = SmartConLogger.BeginScope("FMTree",
            ("Method", "DropFamilyAsync"));
        if (info is not { Payload: FamilyLeafNodeViewModel leaf, Target: CategoryNodeViewModel target })
            return;
        if (!await EnsureDatabaseUpToDateAsync().ConfigureAwait(true)) return;

        var categoryId = target.CategoryId == "__no_category__"
            ? null
            : target.CategoryId;

        // Drop on the family's own current category is a no-op — running
        // the gate there would needlessly re-check rules (and could even
        // block a family that already lives in the category).
        var sameCategory = string.IsNullOrEmpty(leaf.CategoryId)
            ? categoryId is null
            : string.Equals(leaf.CategoryId, categoryId, StringComparison.Ordinal);
        if (sameCategory)
        {
            SmartConLogger.Debug($"DropFamilyAsync: '{leaf.DisplayName}' dropped on its own category — no-op");
            return;
        }

        // Import Validation Gate: a rule-protected category accepts the
        // family only when it passes the rules (checked from persisted
        // extraction — no .rfa re-open). Blocked = dialog shown, move aborted.
        if (!await _categoryChangeGate.EnsureFamilyPassesAsync(
                leaf.CatalogItemId, leaf.DisplayName, categoryId, target.FullPath))
        {
            return;
        }

        await MoveFamilyToCategoryAsync(leaf.CatalogItemId, categoryId);
    }

    private bool CanDropFamily(TreeViewDropInfo? info)
    {
        if (info is null) return false;
        return info.Payload is FamilyLeafNodeViewModel
            && info.Target is CategoryNodeViewModel
            && CanEdit;
    }

    private void ExpandAndSelectItem(string catalogItemId)
    {
        foreach (var root in TreeNodes)
        {
            if (ExpandToItem(root, catalogItemId))
                return;
        }
    }

    private bool ExpandToItem(CatalogTreeNodeViewModel node, string catalogItemId)
    {
        foreach (var child in node.Children)
        {
            if (child is FamilyLeafNodeViewModel leaf && leaf.CatalogItemId == catalogItemId)
            {
                node.IsExpanded = true;
                leaf.IsSelected = true;
                SelectedTreeNode = leaf;
                return true;
            }
            if (ExpandToItem(child, catalogItemId))
            {
                node.IsExpanded = true;
                return true;
            }
        }
        return false;
    }

    private bool CanEditOps() => CanEdit;
}

