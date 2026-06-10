using System.Collections.ObjectModel;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;
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
        if (SelectedItem is null) return;

        var itemId = SelectedItem.Id;
        var updatedAt = SelectedItem.UpdatedAtUtc != default
            ? SelectedItem.UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : null;

        var vm = _viewModelFactory.CreatePropertiesViewModel(
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

        vm.InitializeCommand.Execute(null);
        var result = _dialogService.ShowProperties(vm);
        if (result != true) return;

        await LoadTreeAsync();
        ExpandAndSelectItem(itemId);
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
        ActiveFamilyPreparationResult? familyPreparation = null;
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
                    familyPreparation = await _activeFamilyFilePreparer.PrepareActiveFamilyAsync();
                    if (familyPreparation is null)
                    {
                        SmartConLogger.Warn("Preparer returned null — aborting");
                        return;
                    }
                    SmartConLogger.Info(
                        $"Family prepared: tempRfa='{familyPreparation.TempRfaPath}', " +
                        $"tempTxt='{familyPreparation.TempTxtPath ?? "<none>"}', " +
                        $"originalRfa='{familyPreparation.OriginalRfaPath ?? "<untitled>"}', " +
                        $"originalTxt='{familyPreparation.OriginalTxtPath ?? "<none>"}'");
                    await ProcessFamilyImportAsync(familyPreparation);
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
            var capturedFamilyPath = familyPreparation?.TempRfaPath;
            if (!string.IsNullOrEmpty(capturedFamilyPath))
            {
                SmartConLogger.Info(
                    $"Cleanup phase 1/2: closing family document at '{capturedFamilyPath}'");
                await CloseFamilyDocumentAsync(capturedFamilyPath!);
            }
            else
            {
                SmartConLogger.Debug(
                    "Cleanup phase 1/2: no family preparation to close (project or abort path)");
            }

            // Temp folder cleanup runs on the thread pool — it is pure I/O
            // and does not require the Revit UI thread.
            SmartConLogger.Debug(
                "Cleanup phase 2/2: removing temp staging folders");
            try
            {
                await _activeImportCleanupService.CleanupAfterImportAsync();
                SmartConLogger.Info(
                    "✓ Cleanup phase 2/2 complete (see details above)");
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn($"Temp cleanup failed: {ex.Message}");
            }

            IsLoading = false;
            SmartConLogger.LogSessionEnd("ImportActiveFile", sessionStart);
        }
    }

    /// <summary>
    /// Switches focus to the project document and closes the previously
    /// saved family. Runs on the Revit UI thread via the awaitable
    /// external event. Tolerates missing documents gracefully (Revit
    /// may have already closed them).
    /// </summary>
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

                SmartConLogger.Info(
                    $"Cleanup(family): activeBeforeSwitch='{activeBeforeSwitch}', " +
                    $"projectToSwitch='{projectDoc?.PathName}'");

                if (projectDoc != null)
                {
                    try
                    {
                        uiApp.OpenAndActivateDocument(projectDoc.PathName);
                        SmartConLogger.Info(
                            $"Re-activated project: '{projectDoc.PathName}'");
                    }
                    catch (Exception activateEx)
                    {
                        SmartConLogger.Warn(
                            $"Activate project failed: {activateEx.Message}");
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
                            $"PostCommand Close failed: {postEx.Message}");
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
                                $"✓ Closed family file: {capturedFamilyPath}");
                        }
                        else
                        {
                            SmartConLogger.Debug(
                                $"Family document not found in app.Documents (already closed?)");
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
            SmartConLogger.Error($"Document cleanup failed: {ex.Message}");
        }
    }

    private async Task ProcessFamilyImportAsync(ActiveFamilyPreparationResult preparation)
    {
        using var _ = SmartConLogger.BeginScope("FMImport",
            ("Method", "ProcessFamilyImportAsync"));
        var familyRfaPath = preparation.TempRfaPath;
        SmartConLogger.Debug(
            $"ProcessFamilyImport: tempRfa='{familyRfaPath}', " +
            $"originalRfa='{preparation.OriginalRfaPath ?? "<untitled>"}', " +
            $"tempTxt='{preparation.TempTxtPath ?? "<none>"}'");

        var metadata = await _metadataService.ExtractAsync(familyRfaPath, CancellationToken.None);
        var revitVersion = _fileInfoReader.ReadRevitVersion(familyRfaPath) ?? CurrentRevitVersion;
        var normalizedName = Core.Services.FamilyManager.FamilyNameNormalizer.Normalize(
            SafeFileName.GetBaseName(familyRfaPath));
        var existingByName = await _catalogProvider.FindByNormalizedNameAsync(normalizedName, CancellationToken.None);

        var existingCategoryId = existingByName?.CategoryId;
        string? existingCategoryName = null;

        // Mirror BuildLoadableFamilyBatchRowAsync: do NOT trust the
        // denormalised CategoryPath stored in catalog_items — it can
        // contain the placeholder "Без категории" written by the picker.
        // Use the categories table as the single source of truth for the
        // display label (FullPath or Name fallback). Pre-load all
        // categories in one query so we don't pay an N+1 here.
        if (existingCategoryId is not null)
        {
            try
            {
                var allCategories = await _categoryRepository
                    .GetAllAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                var categoriesById = allCategories.ToDictionary(c => c.Id);
                if (categoriesById.TryGetValue(existingCategoryId, out var cat) && cat is not null)
                {
                    existingCategoryName = cat.FullPath ?? cat.Name;
                }
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn($"Failed to resolve category name: {ex.Message}");
            }
        }

        var item = new FamilyBatchImportItem(
            familyRfaPath,
            SafeFileName.GetBaseName(familyRfaPath),
            metadata.Sha256,
            revitVersion,
            new FileInfo(familyRfaPath).Length,
            existingByName is not null ? FamilyBatchImportStatus.Existing : FamilyBatchImportStatus.New,
            existingByName?.Id,
            existingByName?.CurrentVersionLabel,
            existingCategoryId,
            existingCategoryName,
            FamilySource: "loadable",
            TypeCount: null,
            RevitCategory: null,
            OriginalSourcePath: preparation.OriginalRfaPath);

        using var vm = new FamilyBatchImportViewModel(new[] { item }, _dialogService, _viewModelFactory, _catalogProvider);
        if (_dialogService.ShowBatchImportDialog(vm) != true) return;

        var selectedItems = vm.GetResultItems();
        var progress = new Progress<FamilyImportProgress>(p =>
        {
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportProgress) ?? "Importing {0} of {1}...",
                p.CurrentFileIndex + 1, p.TotalFiles);
        });

        var importResult = await _importService.ImportBatchAsync(selectedItems, null, progress, CancellationToken.None);

        var successfulItems = importResult.Results
            .Where(r => r.Success && !r.WasSkippedAsDuplicate).ToList();

        if (successfulItems.Count > 0) await LoadTreeAsync();

        await ExtractAttributesForImportedFamilies(importResult.Results);

        StatusMessage = BuildImportStatusMessage(
            importResult.SuccessCount, importResult.SkippedCount,
            importResult.ErrorCount, importResult.TotalFiles);
    }

    private async Task ProcessProjectImportAsync(IReadOnlyList<FamilyBatchImportItem> batchItems)
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
            batchItems, _dialogService, _viewModelFactory, _catalogProvider,
            defaultCategoryId, defaultCategoryName);
        if (_dialogService.ShowBatchImportDialog(vm) != true) return;

        var selectedItems = vm.GetResultItems();
        var toImport = selectedItems.Where(i => i.Action != FamilyBatchImportAction.Skip).ToList();
        if (toImport.Count == 0) return;

        var systemItems = toImport.Where(i => i.FamilySource == "system").ToList();
        var loadableItems = toImport.Where(i => i.FamilySource == "loadable").ToList();

        StatusMessage = string.Format(
            LanguageManager.GetString(StringLocalization.Keys.FM_SystemFamilyPreparing) ?? "Импорт {0} семейств...",
            toImport.Count);

        var systemTotalTypes = 0;
        if (systemItems.Count > 0)
        {
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
            var loadResult = await _loadableFamilyImportOrchestrator.ImportAndPersistTypesAsync(
                loadableItems, CurrentRevitVersion, defaultCategoryId);
            loadableTotalTypes = loadableItems.Sum(i => i.TypeCount ?? 0);
            loadableAttributeTasks = loadResult.AttributeTasks;
            SmartConLogger.Info(
                $"Loadable: imported={loadResult.ImportedCount}, skipped={loadResult.SkippedCount}, attrTasks={loadableAttributeTasks.Count}");
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

    private async Task ExtractAttributesForLoadableTasks(IReadOnlyList<LoadableFamilyAttributeTask> tasks)
    {
        using var _scope = SmartConLogger.BeginScope("FMLoadable",
            ("Method", "ExtractAttributesForLoadableTasks"),
            ("Count", tasks.Count));
        foreach (var task in tasks)
        {
            try
            {
                if (!File.Exists(task.ManagedRfaPath))
                {
                    SmartConLogger.Warn(
                        $"Managed .rfa missing: '{task.ManagedRfaPath}'");
                    continue;
                }

                var extraction = _extractionService.Extract(task.ManagedRfaPath, Array.Empty<string>());
                if (extraction.Success)
                {
                    if (task.HasTypeCatalog)
                    {
                        await _dataImportService.MergeMissingValuesAsync(
                            task.CatalogItemId, extraction, task.VersionId, task.FileId, CancellationToken.None);
                    }
                    else
                    {
                        await _dataImportService.SaveExtractionResultAsync(
                            task.CatalogItemId, extraction, task.VersionId, task.FileId, CancellationToken.None);
                    }
                    SmartConLogger.Info(
                        $"Extracted {extraction.Types.Count} type(s) from '{Path.GetFileName(task.ManagedRfaPath)}' (CatalogItemId={task.CatalogItemId})");
                }
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"Extraction failed for '{task.CatalogItemId}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Извлекает атрибуты (Type parameters) из импортированных .rfa-файлов.
    /// Зеркалит поведение старого Import.cs (ExtractTypesForImportedFamilies):
    /// после успешного импорта резолвит файл в managed storage, вызывает
    /// <see cref="IFamilyDataExtractionService.Extract"/> и сохраняет результат
    /// через <see cref="IFamilyDataImportService.SaveExtractionResultAsync"/>.
    /// </summary>
    private async Task ExtractAttributesForImportedFamilies(IReadOnlyList<FamilyImportResult> importResults)
    {
        using var _scope = SmartConLogger.BeginScope("FMLoadable",
            ("Method", "ExtractAttributesForImportedFamilies"),
            ("Count", importResults.Count));
        var extractionResults = new List<(string CatalogItemId, FamilyExtractionResult Result, string? VersionId, string? FileId, bool HasTypeCatalog)>();

        try
        {
            foreach (var item in importResults)
            {
                if (!item.Success || item.WasSkippedAsDuplicate) continue;
                if (string.IsNullOrEmpty(item.CatalogItemId)) continue;
                var catalogItemId = item.CatalogItemId!;

                var resolved = await _fileResolver.ResolveForLoadAsync(catalogItemId, CurrentRevitVersion, CancellationToken.None).ConfigureAwait(true);

                if (string.IsNullOrEmpty(resolved.AbsolutePath)) continue;

                var txtPath = Path.ChangeExtension(resolved.AbsolutePath, ".txt");
                var hasTypeCatalog = File.Exists(txtPath);

                var extraction = _extractionService.Extract(resolved.AbsolutePath, Array.Empty<string>());
                if (extraction.Success)
                {
                    extractionResults.Add((catalogItemId, extraction, item.VersionId, item.FileId, hasTypeCatalog));
                    SmartConLogger.Info(
                        $"Extracted {extraction.Types.Count} type(s) from '{Path.GetFileName(resolved.AbsolutePath)}'");
                }
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"Attribute extraction failed: {ex.Message}");
        }

        if (extractionResults.Count == 0) return;

        FireAndForget(async () =>
        {
            try
            {
                foreach (var (catalogItemId, result, versionId, fileId, hasTypeCatalog) in extractionResults)
                {
                    if (hasTypeCatalog)
                    {
                        await _dataImportService.MergeMissingValuesAsync(
                            catalogItemId, result, versionId, fileId, CancellationToken.None);
                    }
                    else
                    {
                        await _dataImportService.SaveExtractionResultAsync(
                            catalogItemId, result, versionId, fileId, CancellationToken.None);
                    }
                }
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn($"SaveExtraction failed: {ex.Message}");
            }
        }, nameof(ExtractAttributesForImportedFamilies));
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
            SmartConLogger.Warn($"MoveFamilyToCategoryAsync blocked: user lacks edit permissions.");
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

