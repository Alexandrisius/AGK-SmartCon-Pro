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
        var editorVm = _viewModelFactory.CreateCategoryTreeEditorViewModel();
        editorVm.Saved += () => _ = LoadTreeAsync();
        await editorVm.InitializeAsync();
        _dialogService.ShowCategoryTreeEditor(editorVm);
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
                SelectedItem.Manufacturer,
                SelectedItem.VersionLabel,
                null,
                null,
                updatedAt,
                isReadOnly: !CanEdit);
            SmartConLogger.Info("OpenProperties: VM created, calling InitializeCommand...");
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
            if (result != true) return;

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
                    var (systemAnalyses, loadableFamilies) = await _awaitableEvent.RaiseAsync<(IReadOnlyList<CategoryAnalysis>, IReadOnlyList<LoadableFamilyInfo>)>(obj =>
                    {
                        try
                        {
                            var uiApp = (Autodesk.Revit.UI.UIApplication)obj;
                            var activeDoc = uiApp.ActiveUIDocument?.Document;
                            if (activeDoc is null) return (Array.Empty<CategoryAnalysis>(), Array.Empty<LoadableFamilyInfo>());

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

                    var batchItems = await BuildActiveProjectBatchItemsAsync(systemAnalyses, loadableFamilies);
                    if (batchItems.Count == 0)
                    {
                        _dialogService.ShowError(
                            LanguageManager.GetString(StringLocalization.Keys.FM_ImportPrepareError) ?? "Error",
                            LanguageManager.GetString(StringLocalization.Keys.FM_NoSystemFamiliesFound) ?? "Не удалось подготовить семейства для импорта");
                        return;
                    }
                    await ProcessProjectImportAsync(batchItems);
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

        var prepared = await _preparationService.PrepareActiveFamilyAsync(CancellationToken.None);

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
            .BuildPrecomputedTripleAsync(prepared.DisplayName, ".rfa", CancellationToken.None)
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
            FamilySource: "loadable",
            TypeCount: SnapshotExtractionMapper.ResolveTypeCount(
                prepared.LoadableSnapshot, prepared.SystemSnapshot, prepared.SourceTypes),
            RevitCategory: null,
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
            SystemSnapshot: prepared.SystemSnapshot)
        {
            Action = status == FamilyBatchImportStatus.Duplicate
                ? FamilyBatchImportAction.Skip
                : FamilyBatchImportAction.IncrementVersion
        };

        using var vm = new FamilyBatchImportViewModel(
            new[] { item },
            _dialogService,
            _viewModelFactory,
            catalogProvider: _catalogProvider,
            importPrecomputer: _importPrecomputer,
            dedupService: _dedupService);
        if (_dialogService.ShowBatchImportDialog(vm) != true)
        {
            await _preparationService.CloseAllPreparedDocumentsAsync(CancellationToken.None);
            return;
        }

        var selectedItems = vm.GetResultItems();
        var toImport = selectedItems.Where(i => i.Action != FamilyBatchImportAction.Skip).ToList();
        if (toImport.Count == 0)
        {
            await _preparationService.CloseAllPreparedDocumentsAsync(CancellationToken.None);
            return;
        }

        var importItem = toImport[0];

        var displayName = importItem.FileName;

        var postDialogPrecomputed = await _importPrecomputer
            .BuildPrecomputedTripleAsync(displayName, ".rfa", CancellationToken.None)
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
                RevitCategory: null,
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
                RevitCategory: null,
                FileName: displayName,
                OriginalSourcePath: prepared.SourcePath,
                PrecomputedCatalogItemId: resolvedCatalogItemId,
                PrecomputedVersionLabel: resolvedVersionLabel,
                PrecomputedManagedPath: saveAsPath,
                ContentHash: importItem.ContentHash,
                HashFormatVersion: importItem.HashFormatVersion,
                PublishedBy: _revitContext.GetUsername());

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
        }

        await _preparationService.CloseAllPreparedDocumentsAsync(CancellationToken.None);
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

    private async Task ProcessProjectImportAsync(List<FamilyBatchImportItem> batchItems)
    {
        using var _ = SmartConLogger.BeginScope("FMImport",
            ("Method", "ProcessProjectImportAsync"));
        if (batchItems.Count == 0)
        {
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportPrepareError) ?? "Error",
                LanguageManager.GetString(StringLocalization.Keys.FM_NoSystemFamiliesFound) ?? "No families found");
            return;
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
        var defaultCategoryId = (string?)null;
        var defaultCategoryName = (string?)null;

        using var vm = new FamilyBatchImportViewModel(
            batchItems,
            _dialogService,
            _viewModelFactory,
            defaultCategoryId,
            defaultCategoryName,
            _catalogProvider,
            importPrecomputer: _importPrecomputer,
            dedupService: _dedupService);
        if (_dialogService.ShowBatchImportDialog(vm) != true)
        {
            await _preparationService.CloseAllPreparedDocumentsAsync(CancellationToken.None);
            return;
        }

        var selectedItems = vm.GetResultItems();
        var toImport = selectedItems.Where(i => i.Action != FamilyBatchImportAction.Skip).ToList();
        if (toImport.Count == 0)
        {
            await _preparationService.CloseAllPreparedDocumentsAsync(CancellationToken.None);
            return;
        }

        // ADR-041 rev #5: stamp every item with the publishing user's name
        // before dispatching. The orchestrator (system or loadable) propagates
        // this into catalog_versions.published_by via InsertVersionAsync.
        // _revitContext.GetUsername() returns a cached string field (NOT a
        // Revit API call — see RevitContext.cs:66-72), so this is safe from
        // the WPF async thread.
        var username = _revitContext.GetUsername();
        foreach (var item in toImport)
            item.PublishedByUser = username;

        var systemItems = toImport.Where(i => i.FamilySource == "system").ToList();
        var loadableItems = toImport.Where(i => i.FamilySource == "loadable").ToList();

        try
        {

        StatusMessage = string.Format(
            LanguageManager.GetString(StringLocalization.Keys.FM_SystemFamilyPreparing) ?? "Импорт {0} семейств...",
            toImport.Count);

        var systemTotalTypes = 0;
        if (systemItems.Count > 0)
        {
            // v2.0.0: stage the .rvt files for the user-confirmed system
            // items only (previously this was done BEFORE the dialog, leaving
            // orphan .rvt on cancel). After staging, the items now have a
            // real managed FilePath, so the orchestrator's ImportBatchAsync
            // can proceed normally.
            await StageSystemFamiliesFromMetadataAsync(systemItems);

            var sysResult = await _systemFamilyImportOrchestrator.ImportBatchItemsAsync(systemItems);
            systemTotalTypes = systemItems.Sum(i => i.TypeCount ?? 0);
            SmartConLogger.Info(
                $"System: imported={sysResult.Success}, types={systemTotalTypes}, tasks={sysResult.ExtractionTasks.Count}");

            if (sysResult.ExtractionTasks.Count > 0)
            {
                await _systemFamilyAttributeExtractor.ExtractAndSaveAsync(sysResult.ExtractionTasks);
            }
        }

        var loadableTotalTypes = 0;
        IReadOnlyList<LoadableFamilyAttributeTask> loadableAttributeTasks = [];
        if (loadableItems.Count > 0)
        {
            // v2.0.0: stage the .rfa files for the user-confirmed loadable
            // items only (previously done before the dialog, leaving
            // orphan .rfa in _stage/ on cancel). Now we land them
            // directly in managed storage.
            await StageLoadableFamiliesFromMetadataAsync(loadableItems);

            var loadResult = await _loadableFamilyImportOrchestrator.ImportAndPersistTypesAsync(
                loadableItems, CurrentRevitVersion, defaultCategoryId);
            loadableTotalTypes = loadableItems.Sum(i => i.TypeCount ?? 0);
            loadableAttributeTasks = loadResult.AttributeTasks;
            SmartConLogger.Info(
                $"Loadable: imported={loadResult.ImportedCount}, skipped={loadResult.SkippedCount}, attrTasks={loadableAttributeTasks.Count}");

            // Issue #84 (Phase 24): re-sync FamilyVersion marker for every
            // loadable family that successfully made it into the catalog. The
            // "Импорт активного файла" / "Импорт выделенных" flows copy
            // in-project family bytes into managed storage and bump the
            // catalog version (v1 / vN+1), but do not touch the Family
            // element in the active project. Without re-writing the marker
            // the next "Проверить" immediately flags every freshly-imported
            // loadable as stale (NoEntityStorage), even though the in-project
            // family IS the authoritative vN+1 source for the new catalog
            // row. System families (FamilySource == "system") are skipped by
            // design — ADR-030 §Out of Scope: there is no in-project Family
            // element to write a marker onto (OST_PipeCurves etc. are
            // MEPCurve / Wall in Revit, not Family).
            await WriteVersionMarkersForImportedLoadablesAsync(
                loadableItems, loadableAttributeTasks, CancellationToken.None).ConfigureAwait(true);
            _staleDetector.InvalidateCache();
        }

        if (loadableAttributeTasks.Count > 0)
        {
            await ExtractAttributesForLoadableTasks(loadableAttributeTasks);
        }

        if (systemItems.Count > 0 || loadableItems.Count > 0)
        {
            await LoadTreeAsync();
        }

        var totalTypes = systemTotalTypes + loadableTotalTypes;
        StatusMessage = totalTypes > 0
            ? string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_SystemFamilyImported) ?? "Импортировано: {0}",
                totalTypes)
            : "Импорт завершён";
        }
        finally
        {
            await _preparationService.CloseAllPreparedDocumentsAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Issue #84 / Phase 24 (ADR-030): thin wrapper that delegates to
    /// <see cref="LoadableMarkerLogic.WriteMarkersForImportedLoadablesAsync"/>.
    /// Called from <see cref="ProcessProjectImportAsync"/> after a successful
    /// loadable import so that the next "Проверить" does not flag every
    /// freshly-imported loadable as stale (see ADR-030 + issue body for
    /// the full rationale). Pure logic lives in Core so it is unit-testable
    /// without spinning up the Revit API.
    /// </summary>
    private Task WriteVersionMarkersForImportedLoadablesAsync(
        List<FamilyBatchImportItem> loadableItems,
        IReadOnlyList<LoadableFamilyAttributeTask> attributeTasks,
        CancellationToken ct)
    {
        return LoadableMarkerLogic.WriteMarkersForImportedLoadablesAsync(
            loadableItems,
            attributeTasks,
            _versionWriter,
            CurrentRevitVersion,
            ct);
    }

    /// <summary>
    /// v2.0.0: post-dialog staging for system-family batch items. For each
    /// item with a <see cref="FamilyImportSource.SystemSource"/> payload,
    /// allocates a managed .rvt path, runs
    /// <c>CreateCleanProjectWithTypesAndInstances</c>, and rewrites the
    /// item's <c>FilePath</c> + <c>SourceTypes</c> in place. After this
    /// method returns, the items look exactly like the legacy "pre-staged"
    /// items, so the orchestrator's existing import path works unchanged.
    /// </summary>
    private async Task StageSystemFamiliesFromMetadataAsync(List<FamilyBatchImportItem> items)
    {
        if (items.Count == 0) return;

        using var _scope = SmartConLogger.BeginScope("FMImport",
            ("Method", "StageSystemFamiliesFromMetadataAsync"));

        await _awaitableEvent.RaiseAsync(_ =>
        {
            var activeDoc = _revitContext.GetDocument();
            if (activeDoc is null)
            {
                SmartConLogger.Warn("Active document is null — cannot stage system families [Action: откройте .rvt проект в Revit, затем повторите команду]");
                return;
            }

            // v2.0.0 regression: do NOT mutate `items` from inside a
            // `foreach`. List<T>'s indexer setter invalidates the
            // foreach enumerator and throws "Collection was modified;
            // enumeration operation may not execute" on the next
            // MoveNext(). Use an index-based for-loop and capture
            // rewrites in a side dictionary, then apply them after
            // the loop in a single pass.
            var rewrites = new Dictionary<int, FamilyBatchImportItem>(items.Count);

            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.Source is not FamilyImportSource.SystemSource source)
                {
                    SmartConLogger.Debug(
                        $"Skipping item '{item.FileName}': Source is not SystemSource (got {item.Source?.GetType().Name ?? "null"})");
                    continue;
                }
                if (item.FilePath.StartsWith("system://", StringComparison.OrdinalIgnoreCase) == false)
                {
                    SmartConLogger.Debug(
                        $"Skipping item '{item.FileName}': FilePath '{item.FilePath}' does not start with 'system://' (already staged?)");
                    continue;
                }

                // v2.0.0: prefer the canonical managed path that the VM
                // pre-computed up front (BuildSystemFamilyBatchRowVirtualAsync).
                // For an existing item, this is files/<existingItem.Id>/<vN+1>;
                // for a new item, files/<fresh GUID>/v1. Falling back to the
                // legacy allocator when no precomputed path is present keeps
                // unit tests and direct callers working.
                //
                // ADR-040: for OverwriteCurrent, the precomputed path points
                // to vN+1 (precomputer does not know about Action). We must
                // overwrite the CURRENT version's file (v1) instead, so the
                // managed .rvt at the current version's path is replaced and
                // OverwriteCurrentAsync can UPDATE catalog_versions in place
                // without creating an orphan vN+1 file.
                string managedRvtPath;
                if (item.Action == FamilyBatchImportAction.OverwriteCurrent
                    && !string.IsNullOrEmpty(item.ExistingCatalogItemId)
                    && !string.IsNullOrEmpty(item.ExistingVersionLabel))
                {
                    managedRvtPath = _importService.ComputeManagedFilePath(
                        item.ExistingCatalogItemId!,
                        item.ExistingVersionLabel!,
                        SafeFileName.SanitizeFileName(source.DisplayName),
                        ".rvt") ?? string.Empty;
                    if (string.IsNullOrEmpty(managedRvtPath))
                    {
                        SmartConLogger.Warn(
                            $"OverwriteCurrent staging for '{source.DisplayName}': ComputeManagedFilePath returned null " +
                            $"(ExistingCatalogItemId='{item.ExistingCatalogItemId}', ExistingVersionLabel='{item.ExistingVersionLabel}') " +
                            $"[Action: check active catalog DB is selected and pathResolver is configured]");
                    }
                    else
                    {
                        // I-16 / ADR-016: managed files are ReadOnly. Remove
                        // the attribute before CreateCleanProject overwrites
                        // the file; restore it after.
                        if (File.Exists(managedRvtPath))
                        {
                            File.SetAttributes(managedRvtPath, File.GetAttributes(managedRvtPath) & ~FileAttributes.ReadOnly);
                        }
                        SmartConLogger.Info(
                            $"Staging system family '{source.DisplayName}' for OverwriteCurrent: reusing current version path " +
                            $"(ExistingCatalogItemId='{item.ExistingCatalogItemId}', ExistingVersionLabel='{item.ExistingVersionLabel}', " +
                            $"target='{managedRvtPath}') — no orphan vN+1 file will be created");
                    }
                }
                else
                {
                    managedRvtPath = !string.IsNullOrEmpty(item.PrecomputedManagedPath)
                        ? item.PrecomputedManagedPath!
                        : (ComputeSystemFamilyManagedPath(source.DisplayName) ?? string.Empty);
                }
                if (string.IsNullOrEmpty(managedRvtPath))
                {
                    SmartConLogger.Warn($"Cannot compute managed path for '{source.DisplayName}' — skipping [Action: check active catalog DB is selected]");
                    continue;
                }

                SmartConLogger.Info(
                    $"Staging system family '{source.DisplayName}': " +
                    $"item.PrecomputedCatalogItemId='{item.PrecomputedCatalogItemId ?? "<null>"}', " +
                    $"item.PrecomputedManagedPath='{item.PrecomputedManagedPath ?? "<null>"}', " +
                    $"using={(item.Action == FamilyBatchImportAction.OverwriteCurrent && !string.IsNullOrEmpty(item.ExistingCatalogItemId) ? "overwrite-current" : (item.PrecomputedManagedPath is not null ? "precomputed" : "fallback"))}, " +
                    $"target='{managedRvtPath}'");

                CreateCleanProjectResult createResult;
                try
                {
                    var categoryEnum = (BuiltInCategory)source.CategoryId;
                    createResult = _systemFamilyIsolationProject.CreateCleanProjectWithTypesAndInstances(
                        activeDoc, source.TypeUniqueIds, categoryEnum, source.DisplayName, managedRvtPath!);
                }
                catch (Exception ex)
                {
                    SmartConLogger.Error(
                        $"CreateCleanProjectWithTypesAndInstances threw for '{source.DisplayName}': {ex.GetType().Name}: {ex.Message} [Action: verify category has at least one placeable type in the active project]");
                    continue;
                }

                if (!createResult.Success || string.IsNullOrEmpty(createResult.FilePath))
                {
                    SmartConLogger.Warn(
                        $"CreateCleanProjectWithTypesAndInstances returned Success=false for '{source.DisplayName}' [Action: see prior log lines from SystemRevitOps for the underlying cause]");
                    continue;
                }

                // ADR-040 / I-16: restore ReadOnly on the overwritten
                // managed file so it stays immutable outside FamilyManager.
                if (item.Action == FamilyBatchImportAction.OverwriteCurrent
                    && !string.IsNullOrEmpty(item.ExistingCatalogItemId)
                    && File.Exists(managedRvtPath!))
                {
                    File.SetAttributes(managedRvtPath!, File.GetAttributes(managedRvtPath!) | FileAttributes.ReadOnly);
                }

                rewrites[i] = item with
                {
                    FilePath = createResult.FilePath!,
                    SourceTypes = source.TypeNames
                        .Zip(source.TypeUniqueIds, (name, uid) => new FamilySourceTypeInfo(
                            uid, name, source.DisplayName, source.CategoryId))
                        .ToList()
                };
                SmartConLogger.Info(
                    $"Staged system family '{source.DisplayName}' -> '{createResult.FilePath}'");
            }

            // Apply rewrites after the for-loop, in a single pass.
            // The for-loop above does not enumerate `items` so this
            // assignment is safe.
            foreach (var kvp in rewrites)
            {
                items[kvp.Key] = kvp.Value;
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// v2.0.0: post-dialog staging for loadable-family batch items. For
    /// each item with a <see cref="FamilyImportSource.LoadableSource"/>
    /// payload, allocates a managed .rfa path, calls
    /// <c>EditFamily</c> + <c>SaveAs</c> via
    /// <see cref="StageLoadableFamilyFromProject"/>, and rewrites the
    /// item's <c>FilePath</c> in place. After this method returns, the
    /// items look like the legacy "pre-staged" items and the
    /// orchestrator proceeds normally.
    /// </summary>
    private async Task StageLoadableFamiliesFromMetadataAsync(List<FamilyBatchImportItem> items)
    {
        if (items.Count == 0) return;

        using var _scope = SmartConLogger.BeginScope("FMImport",
            ("Method", "StageLoadableFamiliesFromMetadataAsync"));

        await _awaitableEvent.RaiseAsync(_ =>
        {
            var activeDoc = _revitContext.GetDocument();
            if (activeDoc is null)
            {
                SmartConLogger.Warn("Active document is null — cannot stage loadable families [Action: откройте .rvt проект в Revit, затем повторите команду]");
                return;
            }

            // v2.0.0 regression: see StageSystemFamiliesFromMetadataAsync
            // for the full rationale. Mutating items[] from inside a
            // foreach invalidates List<T>'s enumerator. We stage each
            // item into a side dictionary and apply rewrites in a
            // single pass after the staging loop.
            var rewrites = new Dictionary<int, FamilyBatchImportItem>(items.Count);

            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.Source is not FamilyImportSource.LoadableSource source)
                {
                    SmartConLogger.Debug(
                        $"Skipping item '{item.FileName}': Source is not LoadableSource (got {item.Source?.GetType().Name ?? "null"})");
                    continue;
                }
                if (item.FilePath.StartsWith("loadable://", StringComparison.OrdinalIgnoreCase) == false)
                {
                    SmartConLogger.Debug(
                        $"Skipping item '{item.FileName}': FilePath '{item.FilePath}' does not start with 'loadable://' (already staged?)");
                    continue;
                }

                // v2.0.0: prefer the precomputed canonical managed path.
                //
                // ADR-040: for OverwriteCurrent, the precomputed path points
                // to vN+1 (precomputer does not know about Action). We must
                // overwrite the CURRENT version's file (v1) instead, so
                // OverwriteCurrentAsync can UPDATE catalog_versions in place
                // without creating an orphan vN+1 file. StageLoadableFamilyFromProject
                // already handles ReadOnly removal/restoration internally.
                string managedRfaPath;
                if (item.Action == FamilyBatchImportAction.OverwriteCurrent
                    && !string.IsNullOrEmpty(item.ExistingCatalogItemId)
                    && !string.IsNullOrEmpty(item.ExistingVersionLabel))
                {
                    managedRfaPath = _importService.ComputeManagedFilePath(
                        item.ExistingCatalogItemId!,
                        item.ExistingVersionLabel!,
                        SafeFileName.SanitizeFileName(source.FamilyName),
                        ".rfa") ?? string.Empty;
                    if (string.IsNullOrEmpty(managedRfaPath))
                    {
                        SmartConLogger.Warn(
                            $"OverwriteCurrent staging for '{source.FamilyName}': ComputeManagedFilePath returned null " +
                            $"(ExistingCatalogItemId='{item.ExistingCatalogItemId}', ExistingVersionLabel='{item.ExistingVersionLabel}') " +
                            $"[Action: check active catalog DB is selected and pathResolver is configured]");
                    }
                    else
                    {
                        SmartConLogger.Info(
                            $"Staging loadable family '{source.FamilyName}' for OverwriteCurrent: reusing current version path " +
                            $"(ExistingCatalogItemId='{item.ExistingCatalogItemId}', ExistingVersionLabel='{item.ExistingVersionLabel}', " +
                            $"target='{managedRfaPath}') — no orphan vN+1 file will be created");
                    }
                }
                else
                {
                    managedRfaPath = !string.IsNullOrEmpty(item.PrecomputedManagedPath)
                        ? item.PrecomputedManagedPath!
                        : (ComputeLoadableFamilyManagedPath(source.FamilyName) ?? string.Empty);
                }
                if (string.IsNullOrEmpty(managedRfaPath))
                {
                    SmartConLogger.Warn($"Cannot compute managed path for '{source.FamilyName}' — skipping [Action: проверьте, что активная БД каталога выбрана и доступна для записи]");
                    continue;
                }

                var info = new LoadableFamilyInfo(
                    source.FamilyName, source.FamilyUniqueId, source.CategoryName, item.TypeCount ?? 0);
                string? rfaPath;
                try
                {
                    rfaPath = StageLoadableFamilyFromProject(info, managedRfaPath!, item.FilePath);
                }
                catch (Exception ex)
                {
                    SmartConLogger.Error(
                        $"StageLoadableFamilyFromProject threw for '{source.FamilyName}': {ex.GetType().Name}: {ex.Message} [Action: verify the family is still loaded in the active project]");
                    continue;
                }
                if (string.IsNullOrEmpty(rfaPath) || !File.Exists(rfaPath))
                {
                    SmartConLogger.Warn(
                        $"StageLoadableFamilyFromProject returned empty/missing file for '{source.FamilyName}' [Action: see prior log lines from the staging helper for the underlying cause]");
                    continue;
                }

                rewrites[i] = item with { FilePath = rfaPath! };
                SmartConLogger.Info(
                    $"Staged loadable family '{source.FamilyName}' -> '{rfaPath}'");
            }

            // Apply rewrites after the for-loop, in a single pass.
            foreach (var kvp in rewrites)
            {
                items[kvp.Key] = kvp.Value;
            }
        }, CancellationToken.None);
    }

    private async Task ExtractAttributesForLoadableTasks(IReadOnlyList<LoadableFamilyAttributeTask> tasks)
    {
        using var _scope = SmartConLogger.BeginScope("FMLoadable",
            ("Method", "ExtractAttributesForLoadableTasks"),
            ("Count", tasks.Count));
        foreach (var task in tasks)
        {
            try
            {
                // Phase 27: use the in-memory snapshot from Prepare to produce
                // FamilyExtractionResult WITHOUT re-opening the managed .rfa
                // via ExtractFromManagedFile. The snapshot already contains
                // every type, parameter value, and shared-nested family name
                // that the old re-open path would extract. This eliminates the
                // 42× ExtractFromManagedFile.OpenDocumentFile calls (100-330ms
                // each) seen in the post-import flow. Pure C# — no ExternalEvent
                // needed (no Revit API).
                if (task.Snapshot is null)
                {
                    SmartConLogger.Warn(
                        $"Snapshot is null for '{task.CatalogItemId}' — cannot extract attributes without re-open. " +
                        "[Action: check Prepare logs — snapshot extraction may have failed; re-import the family to fix]");
                    continue;
                }

                var extraction = SnapshotExtractionMapper.ToExtractionResult(
                    task.Snapshot, CurrentRevitVersion);

                if (extraction.Success)
                {
                    await _dataImportService.SaveExtractionResultAsync(
                        task.CatalogItemId, extraction, task.VersionId, task.FileId, CancellationToken.None);
                    SmartConLogger.Info(
                        $"Extracted {extraction.Types.Count} type(s) from snapshot for '{Path.GetFileName(task.ManagedRfaPath)}' " +
                        $"(CatalogItemId={task.CatalogItemId}) [no re-open]");

                    await SaveSharedNestedNamesAsync(
                        task.CatalogItemId,
                        task.VersionId,
                        extraction.SharedNestedFamilyNamesSafe,
                        CancellationToken.None);
                }
                else
                {
                    SmartConLogger.Warn(
                        $"Snapshot extraction reported failure for '{task.CatalogItemId}': {extraction.ErrorMessage} " +
                        "[Action: check snapshot mapper logs for details]");
                }
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"Extraction failed for '{task.CatalogItemId}': {ex.Message} [Action: проверьте, что .rfa не повреждён и Revit может открыть его вручную]");
            }
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

    public async Task MoveFamilyToCategoryAsync(string familyId, string? targetCategoryId)
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

        var categoryId = target.CategoryId == "__no_category__"
            ? null
            : target.CategoryId;

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

