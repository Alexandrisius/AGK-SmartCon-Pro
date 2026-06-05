using System.Collections.ObjectModel;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.UI;
using SmartCon.UI.Behaviors;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyManagerMainViewModel
{
    [RelayCommand(CanExecute = nameof(CanEditOps))]
    private async Task OpenCategoryEditorAsync()
    {
        var editorVm = _viewModelFactory.CreateCategoryTreeEditorViewModel();
        editorVm.Saved += () => _ = LoadTreeAsync();
        await editorVm.InitializeAsync();
        _dialogService.ShowCategoryTreeEditor(editorVm);
    }

    [RelayCommand]
    private async Task OpenProperties()
    {
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
        IsLoading = true;
        ActiveFamilyPreparationResult? familyPreparation = null;
        try
        {
            SmartConLogger.LogSessionStart("ImportActiveFile");
            SmartConLogger.Info("[ImportActiveFile] === START ===");

            var kind = await _activeDocumentClassifier.ClassifyAsync();
            SmartConLogger.Info($"[ImportActiveFile] Active document kind: {kind}");

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
                        SmartConLogger.Warn("[ImportActiveFile] Preparer returned null — aborting");
                        return;
                    }
                    SmartConLogger.Info(
                        $"[ImportActiveFile] Family prepared: tempRfa='{familyPreparation.TempRfaPath}', " +
                        $"tempTxt='{familyPreparation.TempTxtPath ?? "<none>"}', " +
                        $"originalRfa='{familyPreparation.OriginalRfaPath ?? "<untitled>"}', " +
                        $"originalTxt='{familyPreparation.OriginalTxtPath ?? "<none>"}'");
                    await ProcessFamilyImportAsync(familyPreparation);
                    break;

                case ActiveDocumentKind.Project:
                    var pending = await _awaitableEvent.RaiseAsync<IReadOnlyList<SystemFamilyPendingImport>>(obj =>
                    {
                        try
                        {
                            var uiApp = (Autodesk.Revit.UI.UIApplication)obj;
                            var activeDoc = uiApp.ActiveUIDocument?.Document;
                            return activeDoc is null
                                ? Array.Empty<SystemFamilyPendingImport>()
                                : _systemFamilyImportService.AnalyzeAndPrepareForProject(activeDoc);
                        }
                        catch (Exception ex)
                        {
                            SmartConLogger.Error(
                                $"[ImportActiveFile] AnalyzeAndPrepareForProject failed: {ex.Message}");
                            return Array.Empty<SystemFamilyPendingImport>();
                        }
                    });
                    if (pending.Count == 0)
                    {
                        _dialogService.ShowError(
                            LanguageManager.GetString(StringLocalization.Keys.FM_NoSystemFamiliesFound) ?? "Error",
                            LanguageManager.GetString(StringLocalization.Keys.FM_NoSystemFamiliesFound)
                                ?? "В проекте не найдено размещённых системных семейств");
                        return;
                    }
                    await ProcessProjectImportAsync(pending);
                    break;
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"[ImportActiveFile] FAILED: {ex}");
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
                    $"[ImportActiveFile] Cleanup phase 1/2: closing family document at '{capturedFamilyPath}'");
                await CloseFamilyDocumentAsync(capturedFamilyPath!);
            }
            else
            {
                SmartConLogger.Debug(
                    "[ImportActiveFile] Cleanup phase 1/2: no family preparation to close (project or abort path)");
            }

            // Temp folder cleanup runs on the thread pool — it is pure I/O
            // and does not require the Revit UI thread.
            SmartConLogger.Debug(
                "[ImportActiveFile] Cleanup phase 2/2: removing temp staging folders");
            try
            {
                await _activeImportCleanupService.CleanupAfterImportAsync();
                SmartConLogger.Info(
                    "[ImportActiveFile] ✓ Cleanup phase 2/2 complete (see [ActiveCleanup] details above)");
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn($"[ImportActiveFile] Temp cleanup failed: {ex.Message}");
            }

            IsLoading = false;
            SmartConLogger.Info("[ImportActiveFile] === END ===");
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
        try
        {
            await _awaitableEvent.RaiseAsync(obj =>
            {
                var uiApp = (Autodesk.Revit.UI.UIApplication)obj;
                var app = uiApp.Application;
                var activeBeforeSwitch = uiApp.ActiveUIDocument?.Document?.PathName;
                var projectDoc = app.Documents.Cast<Document>()
                    .FirstOrDefault(d => !d.IsFamilyDocument && !d.IsLinked
                        && !string.IsNullOrEmpty(d.PathName)
                        && d.PathName != activeBeforeSwitch);

                SmartConLogger.Info(
                    $"[ImportActiveFile] Cleanup(family): activeBeforeSwitch='{activeBeforeSwitch}', " +
                    $"projectToSwitch='{projectDoc?.PathName}'");

                if (projectDoc != null)
                {
                    try
                    {
                        uiApp.OpenAndActivateDocument(projectDoc.PathName);
                        SmartConLogger.Info(
                            $"[ImportActiveFile] Re-activated project: '{projectDoc.PathName}'");
                    }
                    catch (Exception activateEx)
                    {
                        SmartConLogger.Warn(
                            $"[ImportActiveFile] Activate project failed: {activateEx.Message}");
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
                            "[ImportActiveFile] No project to switch to — posted Close command");
                    }
                    catch (Exception postEx)
                    {
                        SmartConLogger.Warn(
                            $"[ImportActiveFile] PostCommand Close failed: {postEx.Message}");
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
                                $"[ImportActiveFile] ✓ Closed family file: {capturedFamilyPath}");
                        }
                        else
                        {
                            SmartConLogger.Debug(
                                $"[ImportActiveFile] Family document not found in app.Documents (already closed?)");
                        }
                    }
                    catch (Exception closeEx)
                    {
                        SmartConLogger.Info(
                            $"[ImportActiveFile] Family close skipped: {closeEx.Message}");
                    }
                }
                else
                {
                    SmartConLogger.Debug(
                        $"[ImportActiveFile] Active document switched to '{activeAfterSwitch}' — no need to close family");
                }
            });
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"[ImportActiveFile] Document cleanup failed: {ex.Message}");
        }
    }

    private async Task ProcessFamilyImportAsync(ActiveFamilyPreparationResult preparation)
    {
        var familyRfaPath = preparation.TempRfaPath;
        SmartConLogger.Debug(
            $"[ImportActiveFile] ProcessFamilyImport: tempRfa='{familyRfaPath}', " +
            $"originalRfa='{preparation.OriginalRfaPath ?? "<untitled>"}', " +
            $"tempTxt='{preparation.TempTxtPath ?? "<none>"}'");

        var metadata = await _metadataService.ExtractAsync(familyRfaPath, CancellationToken.None);
        var revitVersion = _fileInfoReader.ReadRevitVersion(familyRfaPath) ?? CurrentRevitVersion;
        var normalizedName = Core.Services.FamilyManager.FamilyNameNormalizer.Normalize(
            Path.GetFileNameWithoutExtension(familyRfaPath));
        var existingByName = await _catalogProvider.FindByNormalizedNameAsync(normalizedName, CancellationToken.None);

        var existingCategoryId = existingByName?.CategoryId;
        var existingCategoryName = existingByName?.CategoryPath;
        if (existingCategoryId is not null && existingCategoryName is null)
        {
            try
            {
                var cat = await _categoryRepository.GetByIdAsync(existingCategoryId, CancellationToken.None);
                existingCategoryName = cat?.Name;
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn($"[ImportActiveFile] Failed to resolve category name: {ex.Message}");
            }
        }

        var item = new FamilyBatchImportItem(
            familyRfaPath,
            Path.GetFileNameWithoutExtension(familyRfaPath),
            metadata.Sha256,
            revitVersion,
            new FileInfo(familyRfaPath).Length,
            existingByName is not null ? FamilyBatchImportStatus.Existing : FamilyBatchImportStatus.New,
            existingByName?.Id,
            existingByName?.CurrentVersionLabel,
            existingCategoryId,
            existingCategoryName,
            FamilySource: "loadable",
            TypeCount: 0,
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

        ExtractAttributesForImportedFamilies(importResult.Results);

        StatusMessage = BuildImportStatusMessage(
            importResult.SuccessCount, importResult.SkippedCount,
            importResult.ErrorCount, importResult.TotalFiles);
    }

    private async Task ProcessProjectImportAsync(IReadOnlyList<SystemFamilyPendingImport> pendingItems)
    {
        var batchItems = new List<FamilyBatchImportItem>(pendingItems.Count);
        var pendingByFile = new Dictionary<string, SystemFamilyPendingImport>(StringComparer.OrdinalIgnoreCase);

        foreach (var pending in pendingItems)
        {
            pendingByFile[Path.GetFileName(pending.TempRvtPath)] = pending;

            if (!File.Exists(pending.TempRvtPath))
            {
                SmartConLogger.Warn(
                    $"[ImportActiveFile] Temp .rvt missing: {pending.TempRvtPath}");
                continue;
            }

            var metadata = await _metadataService.ExtractAsync(pending.TempRvtPath, CancellationToken.None);
            var revitVersion = _fileInfoReader.ReadRevitVersion(pending.TempRvtPath) ?? CurrentRevitVersion;
            var normalizedName = Core.Services.FamilyManager.FamilyNameNormalizer.Normalize(
                Path.GetFileNameWithoutExtension(pending.TempRvtPath));
            var existingByName = await _catalogProvider.FindByNormalizedNameAsync(normalizedName, CancellationToken.None);

            var existingCategoryId = existingByName?.CategoryId;
            var existingCategoryName = existingByName?.CategoryPath;
            if (existingCategoryId is not null && existingCategoryName is null)
            {
                try
                {
                    var cat = await _categoryRepository.GetByIdAsync(existingCategoryId, CancellationToken.None);
                    existingCategoryName = cat?.Name;
                }
                catch (Exception ex)
                {
                    SmartConLogger.Warn(
                        $"[ImportActiveFile] Failed to resolve category for '{pending.CategoryName}': {ex.Message}");
                }
            }

            var displayName = Path.GetFileNameWithoutExtension(pending.TempRvtPath);
            batchItems.Add(new FamilyBatchImportItem(
                pending.TempRvtPath,
                displayName,
                metadata.Sha256,
                revitVersion,
                new FileInfo(pending.TempRvtPath).Length,
                existingByName is not null ? FamilyBatchImportStatus.Existing : FamilyBatchImportStatus.New,
                existingByName?.Id,
                existingByName?.CurrentVersionLabel,
                existingCategoryId,
                existingCategoryName,
                FamilySource: "system",
                TypeCount: pending.Types.Count,
                RevitCategory: pending.CategoryName));
        }

        if (batchItems.Count == 0)
        {
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportPrepareError) ?? "Error",
                LanguageManager.GetString(StringLocalization.Keys.FM_NoSystemFamiliesFound) ?? "No system families found");
            return;
        }

        using var vm = new FamilyBatchImportViewModel(batchItems, _dialogService, _viewModelFactory, _catalogProvider);
        if (_dialogService.ShowBatchImportDialog(vm) != true) return;

        var selectedItems = vm.GetResultItems();
        var toImport = selectedItems.Where(i => i.Action != FamilyBatchImportAction.Skip).ToList();
        if (toImport.Count == 0) return;

        StatusMessage = string.Format(
            LanguageManager.GetString(StringLocalization.Keys.FM_SystemFamilyPreparing) ?? "Импорт {0} системных семейств...",
            toImport.Count);

        var result = await _systemFamilyImportService.ImportBatchItemsAsync(toImport);

        var totalTypes = pendingItems
            .Where(p => toImport.Any(i => string.Equals(
                Path.GetFileName(i.FilePath), Path.GetFileName(p.TempRvtPath), StringComparison.OrdinalIgnoreCase)))
            .Sum(p => p.Types.Count);

        StatusMessage = result.Success
            ? string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_SystemFamilyImported) ?? "Импортировано: {0}",
                totalTypes)
            : result.Message ?? "Ошибка импорта";

        if (result.Success) await LoadTreeAsync();

        // CRITICAL: await the extraction phase so that
        // 1) extraction + save completes BEFORE cleanup runs
        // 2) ImportActiveFileAsync's finally block only fires Phase 2
        //    cleanup (temp folders) after extraction is durably persisted
        // This eliminates the race condition where ActiveCleanupService
        // would delete .rvt files before ExternalEvent had a chance to
        // read them (causing "[WRN] Temp .rvt not found for extraction").
        await ExtractAttributesFromRvtsAsync(result.ExtractionTasks);
    }

    /// <summary>
    /// Extracts Type parameters from each staged .rvt and persists the
    /// result to the catalog. Awaits the in-flight saves so callers can
    /// rely on ordering (e.g. <i>extract</i> → <i>save</i> → <i>cleanup</i>).
    /// This is the awaitable version of the legacy
    /// <c>ExtractAttributesFromRvts</c> fire-and-forget helper.
    /// </summary>
    private async Task ExtractAttributesFromRvtsAsync(
        IReadOnlyList<SystemFamilyExtractionTask> tasks,
        CancellationToken ct = default)
    {
        if (tasks.Count == 0) return;

        SmartConLogger.Debug(
            $"[ImportActiveFile] Awaiting extraction for {tasks.Count} .rvt task(s) via AwaitableEvent...");

        // Captured inside the UI thread callback, awaited outside.
        var pendingSaves = new List<Task>();

        await _awaitableEvent.RaiseAsync(_ =>
        {
            foreach (var task in tasks)
            {
                try
                {
                    if (!File.Exists(task.TempRvtPath))
                    {
                        SmartConLogger.Warn(
                            $"[ImportActiveFile] Temp .rvt not found for extraction: {task.TempRvtPath}");
                        continue;
                    }

                    var extraction = _systemFamilyAttributeExtraction.ExtractFromRvt(
                        task.TempRvtPath, task.TypeNames);
                    if (extraction.Success)
                    {
                        var saveTask = Task.Run(async () =>
                        {
                            try
                            {
                                await _dataImportService.SaveExtractionResultAsync(
                                    task.CatalogItemId, extraction, task.VersionId, task.FileId,
                                    CancellationToken.None);
                                SmartConLogger.Debug(
                                    $"[ImportActiveFile] Saved extraction for '{Path.GetFileName(task.TempRvtPath)}': " +
                                    $"{extraction.Types.Count} types");
                            }
                            catch (Exception ex)
                            {
                                SmartConLogger.Warn(
                                    $"[ImportActiveFile] SaveExtractionResult failed: {ex.Message}");
                            }
                        });
                        pendingSaves.Add(saveTask);
                    }
                    else
                    {
                        SmartConLogger.Warn(
                            $"[ImportActiveFile] Extraction failed for '{Path.GetFileName(task.TempRvtPath)}': " +
                            $"{extraction.ErrorMessage}");
                    }
                }
                catch (Exception ex)
                {
                    SmartConLogger.Warn(
                        $"[ImportActiveFile] Extraction exception for '{task.TempRvtPath}': {ex.Message}");
                }
            }
        }, ct);

        // Wait for all saves to complete BEFORE deleting the temp files.
        // This guarantees the cleanup (which happens in
        // ImportActiveFileAsync.finally) never races with an in-flight
        // save. The saves are pure I/O on the thread pool.
        if (pendingSaves.Count > 0)
        {
            SmartConLogger.Debug(
                $"[ImportActiveFile] Waiting for {pendingSaves.Count} save(s) before cleanup...");
            try
            {
                await Task.WhenAll(pendingSaves);
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"[ImportActiveFile] One or more saves failed: {ex.Message}");
            }
        }

        foreach (var task in tasks)
        {
            try
            {
                if (File.Exists(task.TempRvtPath)) File.Delete(task.TempRvtPath);
                var metaPath = task.TempRvtPath + ".types.json";
                if (File.Exists(metaPath)) File.Delete(metaPath);
            }
            catch { }
        }

        SmartConLogger.Info(
            $"[ImportActiveFile] ✓ Extraction phase complete ({pendingSaves.Count} file(s) saved)");
    }

    /// <summary>
    /// Извлекает атрибуты (Type parameters) из импортированных .rfa-файлов.
    /// Зеркалит поведение старого Import.cs (ExtractTypesForImportedFamilies):
    /// после успешного импорта резолвит файл в managed storage, вызывает
    /// <see cref="IFamilyDataExtractionService.Extract"/> и сохраняет результат
    /// через <see cref="IFamilyDataImportService.SaveExtractionResultAsync"/>.
    /// </summary>
    private void ExtractAttributesForImportedFamilies(IReadOnlyList<FamilyImportResult> importResults)
    {
        var extractionResults = new List<(string CatalogItemId, FamilyExtractionResult Result, string? VersionId, string? FileId, bool HasTypeCatalog)>();

        try
        {
            foreach (var item in importResults)
            {
                if (!item.Success || item.WasSkippedAsDuplicate) continue;
                if (string.IsNullOrEmpty(item.CatalogItemId)) continue;
                var catalogItemId = item.CatalogItemId!;

                var resolved = Task.Run(() =>
                    _fileResolver.ResolveForLoadAsync(catalogItemId, CurrentRevitVersion, CancellationToken.None))
                    .GetAwaiter().GetResult();

                if (string.IsNullOrEmpty(resolved.AbsolutePath)) continue;

                var txtPath = Path.ChangeExtension(resolved.AbsolutePath, ".txt");
                var hasTypeCatalog = File.Exists(txtPath);

                var extraction = _extractionService.Extract(resolved.AbsolutePath, Array.Empty<string>());
                if (extraction.Success)
                {
                    extractionResults.Add((catalogItemId, extraction, item.VersionId, item.FileId, hasTypeCatalog));
                    SmartConLogger.Info(
                        $"[ImportActiveFile] Extracted {extraction.Types.Count} type(s) from '{Path.GetFileName(resolved.AbsolutePath)}'");
                }
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"[ImportActiveFile] Attribute extraction failed: {ex.Message}");
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
                SmartConLogger.Warn($"[ImportActiveFile] SaveExtraction failed: {ex.Message}");
            }
        });
    }

    [RelayCommand(CanExecute = nameof(CanEditOps))]
    private async Task EditSystemFamilyAsync()
    {
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
                SmartConLogger.Freeze($"[EditSystemFamily] OpenAndActivateDocument failed: {ex.Message}");
            }
        });
    }

    [RelayCommand(CanExecute = nameof(CanEditOps))]
    private async Task DeleteFamilyAsync()
    {
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
            SmartConLogger.Warn($"[FM] MoveFamilyToCategoryAsync blocked: user lacks edit permissions.");
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
