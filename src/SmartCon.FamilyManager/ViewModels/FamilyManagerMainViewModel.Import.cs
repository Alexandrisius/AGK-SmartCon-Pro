using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyManagerMainViewModel
{
    [RelayCommand(CanExecute = nameof(CanImportFiles))]
    private async Task ImportFilesAsync()
    {
        using var _scope = SmartConLogger.BeginScope("FMImport",
            ("Method", "ImportFilesAsync"));
        var title = LanguageManager.GetString(StringLocalization.Keys.FM_ImportFile) ?? "Import Files";
        var paths = _dialogService.ShowImportFilesDialog(title);
        if (paths is null || paths.Length == 0) return;

        await ShowBatchImportDialogAsync(paths, null);
    }

    [RelayCommand(CanExecute = nameof(CanImportToCategoryWithAccess))]
    private async Task ImportFileToCategoryAsync()
    {
        if (SelectedTreeNode is not CategoryNodeViewModel categoryNode) return;
        if (categoryNode.CategoryId == "__no_category__") return;

        var title = LanguageManager.GetString(StringLocalization.Keys.FM_ImportFile) ?? "Import File";
        var paths = _dialogService.ShowImportFilesDialog(title);
        if (paths is null || paths.Length == 0) return;

        await ShowBatchImportDialogAsync(paths, categoryNode.CategoryId);
    }

    /// <summary>
    /// Shows the batch import dialog for the given file paths.
    /// v2.0.0: no SHA-256 dedup, no file size — status is computed from
    /// normalized name only (New / Existing). Runs <c>ImportBatchAsync</c>
    /// after user confirmation.
    /// </summary>
    private async Task ShowBatchImportDialogAsync(string[] paths, string? categoryId, string? forcedExistingItemId = null)
    {
        IsLoading = true;
        try
        {
            var items = new List<FamilyBatchImportItem>();
            string? categoryName = null;
            if (!string.IsNullOrEmpty(categoryId))
            {
                try
                {
                    var cat = await _categoryRepository.GetByIdAsync(categoryId!, CancellationToken.None);
                    categoryName = cat?.Name;
                }
                catch (Exception ex)
                {
                    SmartConLogger.Warn($"Failed to resolve category name for '{categoryId}': {ex.Message} [Action: проверьте, что категория существует в каталоге и БД доступна]");
                }
            }

            foreach (var path in paths)
            {
                try
                {
                    var metadata = await _metadataService.ExtractAsync(path, CancellationToken.None);
                    var revitVersion = _fileInfoReader.ReadRevitVersion(path) ?? CurrentRevitVersion;

                    // v2.0.0: no SHA-256 dedup. Same-name families get a new
                    // version (vN+1) or OverwriteCurrent if the user picks it.
                    var normalizedName = Core.Services.FamilyManager.FamilyNameNormalizer.Normalize(SafeFileName.GetBaseName(path));
                    var existingByName = await _catalogProvider.FindByNormalizedNameAsync(normalizedName, CancellationToken.None);
                    if (existingByName is not null || forcedExistingItemId is not null)
                    {
                        var existingCategoryId = existingByName?.CategoryId;
                        var existingCategoryName = existingByName?.CategoryPath;
                        // category_name in DB can be NULL while category_id is set — resolve name from repository
                        if (existingCategoryId is not null && existingCategoryName is null)
                        {
                            try
                            {
                                var cat = await _categoryRepository.GetByIdAsync(existingCategoryId, CancellationToken.None);
                                existingCategoryName = cat?.Name;
                            }
                            catch (Exception ex)
                            {
                                SmartConLogger.Warn($"Failed to resolve category name for '{existingCategoryId}': {ex.Message} [Action: проверьте, что категория существует в каталоге и БД доступна]");
                            }
                        }
                        items.Add(new FamilyBatchImportItem(
                            path, SafeFileName.GetBaseName(path), revitVersion,
                            FamilyBatchImportStatus.Existing,
                            forcedExistingItemId ?? existingByName!.Id, existingByName?.CurrentVersionLabel,
                            existingCategoryId ?? categoryId, existingCategoryName ?? categoryName,
                            FamilySource: "loadable",
                            TypeCount: null));
                    }
                    else
                    {
                        items.Add(new FamilyBatchImportItem(
                            path, SafeFileName.GetBaseName(path), revitVersion,
                            FamilyBatchImportStatus.New,
                            null, null, categoryId, categoryName,
                            FamilySource: "loadable",
                            TypeCount: null));
                    }
                }
                catch (Exception ex)
                {
                    SmartConLogger.Warn($"Failed to analyze file '{path}': {ex.Message} [Action: проверьте, что файл доступен для чтения и не повреждён]");
                    items.Add(new FamilyBatchImportItem(
                        path, SafeFileName.GetBaseName(path), 0,
                        FamilyBatchImportStatus.Error,
                        FamilySource: "loadable",
                        TypeCount: null));
                }
            }

            using var vm = new FamilyBatchImportViewModel(
                items,
                _dialogService,
                _viewModelFactory,
                categoryId,
                categoryName,
                _catalogProvider,
                importPrecomputer: _importPrecomputer);
            var result = _dialogService.ShowBatchImportDialog(vm);
            if (result != true) return;

            var selectedItems = vm.GetResultItems();
            var progress = new Progress<FamilyImportProgress>(p =>
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_ImportProgress) ?? "Importing {0} of {1}...",
                    p.CurrentFileIndex + 1, p.TotalFiles);
            });

            var importResult = await _importService.ImportBatchAsync(selectedItems, categoryId, progress, CancellationToken.None);

            // Extract types/attributes for successfully imported families
            var successfulItems = importResult.Results.Where(r => r.Success && !r.WasSkipped).ToList();
            if (successfulItems.Count > 0)
            {
                _ = ExtractTypesForImportedFamilies(successfulItems, importResult.SuccessCount, importResult.SkippedCount, importResult.ErrorCount, importResult.TotalFiles);
            }
            else
            {
                StatusMessage = BuildImportStatusMessage(
                    importResult.SuccessCount, importResult.SkippedCount, importResult.ErrorCount, importResult.TotalFiles);
                await LoadTreeAsync();
            }

            // Single LoadTreeAsync: types appear after save via dispatcher.
            // Double call (before+after) doubled main-thread work in net48 and
            // caused WPF render thread to fall behind — see ADR-031.

            // Auto-clear status after 10 seconds
            FireAndForget(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                StatusMessage = string.Empty;
            }, nameof(ImportFilesAsync));
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Import error: {0}",
                ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Extracts types and attributes for successfully imported families via ExternalEvent.
    /// </summary>
    private async Task ExtractTypesForImportedFamilies(
        List<FamilyImportResult> importedItems,
        int successCount, int skippedCount, int errorCount, int total)
    {
        await _awaitableEvent.RaiseAsyncTask(async _ =>
        {
            // Update status inside ExternalEvent to avoid WPF render thread freeze
            // when OpenDocumentFile triggers MFC family upgrade dialog
            StatusMessage = BuildImportStatusMessage(successCount, skippedCount, errorCount, total);

            var extractionResults = new List<(string CatalogItemId, FamilyExtractionResult Result, string? VersionLabel, string? FileId)>();

            try
            {
                foreach (var item in importedItems)
                {
                    if (string.IsNullOrEmpty(item.CatalogItemId)) continue;
                    var catalogItemId = item.CatalogItemId!;

                    // ThreadPool: file resolution (SQLite/async)
                    var resolved = await _fileResolver
                        .ResolveForLoadAsync(catalogItemId, CurrentRevitVersion, CancellationToken.None)
                        .ConfigureAwait(true);

                    if (string.IsNullOrEmpty(resolved.AbsolutePath)) continue;

                    var extractionResult = await ExtractFromManagedFileAsync(resolved.AbsolutePath, Array.Empty<string>(), CancellationToken.None);
                    if (extractionResult.Success)
                    {
                        extractionResults.Add((catalogItemId, extractionResult, item.VersionId, item.FileId));

                        // ADR-034: persist shared-nested names in the same
                        // ExtractFromManagedFile call (V3 — no second
                        // OpenDocumentFile, no extra MFC family-upgrade
                        // dialog). Graceful degradation if the repository is
                        // unavailable (legacy catalog without V13 migration)
                        // or the .rfa declares no shared nested families.
                        await SaveSharedNestedNamesAsync(
                            catalogItemId,
                            item.VersionId,
                            extractionResult.SharedNestedFamilyNamesSafe,
                            CancellationToken.None);
                    }
                }
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn($"ExtractTypesForImportedFamilies extraction failed: {ex.Message} [Action: проверьте логи Revit и состояние .rfa, повторите импорт]");
            }

            // FireAndForget: SQLite save + tree reload on UI thread.
            // The save runs in Task.Run (ConfigureAwait(false) inside FireAndForget),
            // so TreeNodes setter must be marshalled to the dispatcher explicitly —
            // ConfigureAwait(false) drops the UI SyncContext that LoadTreeAsync needs.
            FireAndForget(async () =>
            {
                SmartConLogger.FreezeThreadPool("ExtractTypesForImportedFamilies.start");
                var saveSw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    foreach (var (catalogItemId, result, versionId, fileId) in extractionResults)
                    {
                        // v2.0.0: Type Catalog (.txt) no longer stored in managed
                        // storage. Baker baked the types into the .rfa itself
                        // (ADR-033), so the extraction result is always the
                        // authoritative data.
                        await _dataImportService.SaveExtractionResultAsync(
                            catalogItemId, result, versionId, fileId, CancellationToken.None);
                    }
                    saveSw.Stop();
                    SmartConLogger.Freeze($"ExtractTypesForImportedFamilies: Save took {saveSw.ElapsedMilliseconds}ms, count={extractionResults.Count}");
                    SmartConLogger.Debug($"ExtractTypesForImportedFamilies: save complete, scheduling UI tree refresh");
                }
                catch (Exception ex)
                {
                    saveSw.Stop();
                    SmartConLogger.FreezeFail("ExtractTypesForImportedFamilies.Save", $"after {saveSw.ElapsedMilliseconds}ms: {ex.Message}");
                    SmartConLogger.Warn($"ExtractTypesForImportedFamilies save failed: {ex.Message} [Action: типы могут быть неполными; нажмите Refresh]");
                }

                try
                {
                    // v2.0.0 (ADR-036, M-019-003): use the injected IDispatcher
                    // instead of the removed _uiDispatcher (System.Windows.Threading.Dispatcher).
                    var dispatcher = _dispatcher;
                    var beforeThread = Environment.CurrentManagedThreadId;
                    var treeSw = System.Diagnostics.Stopwatch.StartNew();
                    SmartConLogger.Debug($"ExtractTypesForImportedFamilies: about to dispatcher.InvokeAsync(LoadTreeAsync) — caller thread={beforeThread}");
                    // IDispatcher.InvokeAsync takes an Action; LoadTreeAsync returns
                    // Task. Wrap in fire-and-forget so the marshalled Action is sync.
                    await dispatcher.InvokeAsync(() => { _ = LoadTreeAsync(); });
                    treeSw.Stop();
                    SmartConLogger.Debug($"ExtractTypesForImportedFamilies: dispatcher.InvokeAsync(LoadTreeAsync) returned on thread {Environment.CurrentManagedThreadId}, elapsed={treeSw.ElapsedMilliseconds}ms");
                }
                catch (Exception ex)
                {
                    SmartConLogger.FreezeFail("ExtractTypesForImportedFamilies.LoadTree", $"{ex.GetType().Name}: {ex.Message}");
                    SmartConLogger.Warn($"Tree reload after extract failed: {ex.Message} [Action: нажмите Refresh чтобы обновить дерево]");
                }
            }, nameof(ExtractTypesForImportedFamilies));
        });
    }

    private bool CanImportToCategory() =>
        SelectedTreeNode is CategoryNodeViewModel cat && cat.CategoryId != "__no_category__";

    private string BuildImportStatusMessage(int successCount, int skipCount, int errorCount, int total)
    {
        var parts = new List<string>();

        var importPart = string.Format(
            LanguageManager.GetString(StringLocalization.Keys.FM_ImportStatusImport) ?? "импорт: {0}/{1}",
            successCount, total);
        parts.Add(importPart);

        if (skipCount > 0)
        {
            var skipPart = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportStatusSkipped) ?? "пропущено: {0}",
                skipCount);
            skipPart += LanguageManager.GetString(StringLocalization.Keys.FM_ImportStatusSkippedIdentical) ?? " (идентично)";
            parts.Add(skipPart);
        }

        if (errorCount > 0)
        {
            var errorPart = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportStatusErrors) ?? "ошибок: {0}",
                errorCount);
            parts.Add(errorPart);
        }

        return string.Join(", ", parts);
    }

    private bool CanImportFiles() => CanImport;

    private bool CanImportToCategoryWithAccess() => CanImport && CanImportToCategory();

    [RelayCommand(CanExecute = nameof(CanEditOps))]
    private async Task ImportSelectedElementsAsync()
    {
        IsLoading = true;
        StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_SelectElementsPrompt)
            ?? "Выберите элементы в Revit (системные или загружаемые семейства)...";

        SelectedElementsAnalysis? analysis = null;

        await _awaitableEvent.RaiseAsync(_ =>
        {
            try
            {
                var doc = _revitContext.GetDocument();
                if (doc is null || doc.IsFamilyDocument)
                {
                    _dialogService.ShowWarning(
                        LanguageManager.GetString(StringLocalization.Keys.FM_ImportSelectedElements) ?? "Импорт выделенных элементов",
                        LanguageManager.GetString(StringLocalization.Keys.FM_ActiveDocNotProject)
                            ?? "Активный документ не является проектом. Откройте проект Revit.");
                    IsLoading = false;
                    return;
                }

                analysis = _systemFamilyRevitOps.PickSelectedElements();
            }
            catch (Exception ex)
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Ошибка импорта: {0}",
                    ex.Message);
                IsLoading = false;
            }
        });

        if (analysis is null || analysis.IsEmpty)
        {
            if (analysis is not null)
            {
                StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_SystemFamilyImportFailed)
                    ?? "Не удалось подготовить элементы";
                IsLoading = false;
            }
            return;
        }

        try
        {
            var batchItems = await BuildSelectedElementsBatchItemsAsync(analysis);

            if (batchItems.Count == 0)
            {
                StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_SystemFamilyImportFailed)
                    ?? "Не удалось подготовить элементы";
                IsLoading = false;
                return;
            }

            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_SystemFamilyPreparing)
                    ?? "Импорт {0} элементов...",
                batchItems.Count);

            await ProcessProjectImportAsync(batchItems);
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"failed: {ex.Message}");
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Ошибка импорта: {0}",
                ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Returns display name for the single-category case: the .rvt file
    /// name of the active project, without extension. Falls back to
    /// <see cref="Autodesk.Revit.DB.Document.Title"/> for unsaved projects.
    /// </summary>
    private static string ResolveActiveProjectDisplayName(Autodesk.Revit.DB.Document activeDoc)
    {
        var pathName = activeDoc.PathName;
        if (!string.IsNullOrEmpty(pathName))
        {
            var name = SafeFileName.GetBaseName(pathName);
            if (!string.IsNullOrEmpty(name)) return name;
        }
        return activeDoc.Title;
    }

    private async Task<List<FamilyBatchImportItem>> BuildSelectedElementsBatchItemsAsync(
        SelectedElementsAnalysis analysis)
    {
        // I-01: SystemFamily isolation (CreateCleanProjectWithTypesAndInstances)
        // and loadable staging (EditFamily + SaveAs) both touch the Revit API.
        // They must run on the Revit UI thread. The WPF DockablePane
        // SynchronizationContext is NOT the Revit UI thread, so we route the
        // whole build through IFamilyManagerAwaitableEvent, which is bound
        // to an ExternalEvent
        // handler. Mirrors the pattern in ExtractTypesForImportedFamilies
        // and AnalyzeActiveProjectAsync. We use the async overload
        // (RaiseAsync<Task<List<…>>>) so the caller's await unwraps the
        // inner task before returning the list.
        var inner = await _awaitableEvent.RaiseAsync(
            _ => BuildSelectedElementsBatchItemsCoreAsync(analysis),
            CancellationToken.None);
        return await inner;
    }

    private async Task<List<FamilyBatchImportItem>> BuildSelectedElementsBatchItemsCoreAsync(
        SelectedElementsAnalysis analysis)
    {
        var result = new List<FamilyBatchImportItem>();
        var ct = CancellationToken.None;

        // v2.0.0: this method is now metadata-only — it does NOT call any
        // Revit-API staging helper. The post-dialog flow (see
        // ProcessProjectImportAsync) reads FamilyBatchImportItem.Source
        // and stages the .rvt / .rfa into managed storage AFTER the user
        // confirms the dialog. This brings the dialog open-time from
        // 20-30s to <2s for 50+ families, and avoids orphan files when
        // the user cancels the dialog.
        //
        // We can therefore drop ConfigureAwait(true): the orchestrator's
        // post-dialog work will route its own Revit-API calls through
        // IFamilyManagerAwaitableEvent.
        //
        // Load all categories once and reuse the dictionary for every row
        // builder. Eliminates an N+1 query pattern (each GetByIdAsync was
        // doing one targeted SELECT plus one full table scan to build the
        // FullPath; with 42+ families this turned into ~84 SQL round-trips).
        // We populate the dictionary with whatever the categories table
        // currently has — a row's "real" category is identified by
        // CategoryId, and the dictionary is the single source of truth for
        // the display label (FullPath). This is the same lookup the
        // legacy ShowBatchImportDialogAsync used to do via
        // _categoryRepository.GetByIdAsync, but amortised across the batch.
        var allCategories = await _categoryRepository.GetAllAsync(ct).ConfigureAwait(false);
        var categoriesById = allCategories.ToDictionary(c => c.Id);

        if (analysis.SystemTypes.Count > 0)
        {
            var groups = analysis.SystemTypes
                .GroupBy(s => s.Category)
                .OrderBy(g => g.Key.ToString(), StringComparer.Ordinal);

            foreach (var group in groups)
            {
                var category = group.Key;
                var types = group.ToList();
                var displayName = types[0].CategoryName;

                var coreTypes = types
                    .Select(t => new FamilySourceTypeInfo(
                        t.UniqueId, t.Name, t.CategoryName, (int)t.Category))
                    .ToList();

                var source = new FamilyImportSource.SystemSource(
                    DisplayName: displayName,
                    CategoryId: (int)category,
                    TypeUniqueIds: types.Select(t => t.UniqueId).ToList(),
                    TypeNames: types.Select(t => t.Name).ToList());

                var placeholderPath =
                    $"system://selected-elements/{SafeFileName.GetBaseName(displayName)}";

                var row = await BuildSystemFamilyBatchRowVirtualAsync(
                    displayName, coreTypes, source, placeholderPath, ct, categoriesById);
                if (row is not null) result.Add(row);
            }
        }

        foreach (var loadable in analysis.LoadableFamilies)
        {
            var placeholderPath = $"loadable://{SafeFileName.GetBaseName(loadable.FamilyName)}";
            var source = new FamilyImportSource.LoadableSource(
                FamilyName: loadable.FamilyName,
                FamilyUniqueId: loadable.FamilyUniqueId,
                CategoryName: loadable.CategoryName);

            var row = await BuildLoadableFamilyBatchRowVirtualAsync(
                loadable, source, placeholderPath, ct, categoriesById);
            if (row is not null) result.Add(row);
        }

        return result;
    }

    /// <summary>
    /// v2.0.0: compute the managed storage path for a system family mini-rvt.
    /// Allocated by the orchestrator AFTER the user confirms the dialog
    /// (see <c>SystemFamilyImportOrchestrator</c>). This helper still
    /// exists for callers that need to allocate a path up front
    /// (e.g. legacy tests) — production flow uses
    /// <c>LocalCatalogProvider.ComputeCatalogItemId</c>.
    /// </summary>
    private string? ComputeSystemFamilyManagedPath(string displayName)
    {
        var dbRoot = _databaseManager.GetActiveDatabasePath();
        if (string.IsNullOrEmpty(dbRoot)) return null;
        var catalogItemId = Guid.NewGuid().ToString("N");
        var versionDir = Path.Combine(dbRoot, "files", catalogItemId, "v1");
        var safeName = SafeFileName.SanitizeFileName(SafeFileName.GetBaseName(displayName));
        if (string.IsNullOrEmpty(safeName)) safeName = "Family";
        return Path.Combine(versionDir, safeName + ".rvt");
    }

    /// <summary>
    /// v2.0.0: compute the managed storage path for a loadable family .rfa
    /// staged from an active project. Like
    /// <see cref="ComputeSystemFamilyManagedPath"/>, this is now allocated
    /// by the orchestrator after the dialog confirms; the helper is kept
    /// for tests and edge callers.
    /// </summary>
    private string? ComputeLoadableFamilyManagedPath(string familyName)
    {
        var dbRoot = _databaseManager.GetActiveDatabasePath();
        if (string.IsNullOrEmpty(dbRoot)) return null;
        var catalogItemId = Guid.NewGuid().ToString("N");
        var versionDir = Path.Combine(dbRoot, "files", catalogItemId, "v1");
        var safeName = SafeFileName.SanitizeFileName(SafeFileName.GetBaseName(familyName));
        if (string.IsNullOrEmpty(safeName)) safeName = "Family";
        return Path.Combine(versionDir, safeName + ".rfa");
    }

    private async Task<List<FamilyBatchImportItem>> BuildActiveProjectBatchItemsAsync(
        IReadOnlyList<CategoryAnalysis> systemAnalyses,
        IReadOnlyList<LoadableFamilyInfo> loadableFamilies)
    {
        // I-01: see the matching comment in BuildSelectedElementsBatchItemsAsync.
        // Route the whole build through IFamilyManagerAwaitableEvent so we keep
        // a single Revit-aware context for the analysis. The build itself is
        // now metadata-only — no staging — so the dialog opens in 1-2s even
        // for 50+ families (previously 20-30s due to per-family
        // CreateCleanProjectWithTypesAndInstances + StageLoadableFamilyFromProject).
        var inner = await _awaitableEvent.RaiseAsync(
            _ => BuildActiveProjectBatchItemsCoreAsync(systemAnalyses, loadableFamilies),
            CancellationToken.None);
        return await inner;
    }

    private async Task<List<FamilyBatchImportItem>> BuildActiveProjectBatchItemsCoreAsync(
        IReadOnlyList<CategoryAnalysis> systemAnalyses,
        IReadOnlyList<LoadableFamilyInfo> loadableFamilies)
    {
        var result = new List<FamilyBatchImportItem>();
        var ct = CancellationToken.None;

        // Load all categories once. See BuildSelectedElementsBatchItemsAsync
        // for the full rationale (N+1 elimination + source-of-truth for the
        // display label). The build is now metadata-only — we no longer
        // touch the Revit API for staging, so we can use ConfigureAwait(false)
        // to release the UI thread between awaits.
        var allCategories = await _categoryRepository.GetAllAsync(ct).ConfigureAwait(false);
        var categoriesById = allCategories.ToDictionary(c => c.Id);

        if (systemAnalyses.Count > 0)
        {
            // Use project name as displayName ONLY when the project is a "pure" system-family
            // mini-rvt: exactly one system category and zero loadable families. In that case
            // the .rvt was likely named after the category (e.g. "Трубы пластиковые.rvt") and
            // renaming to "Трубы" on every re-import would be annoying. If the project also
            // has loadable families, fall back to the category DisplayName ("Трубы", "Гибкие
            // трубы") so the system family is identified by its category, not the project name.
            //
            // The project display name is also stored on the placeholder
            // FilePath (system://<ProjectName>/<Category>) so the post-dialog
            // flow can read it back when staging.
            string? projectName = null;
            if (systemAnalyses.Count == 1 && loadableFamilies.Count == 0)
            {
                var activeDoc = _revitContext.GetDocument();
                if (activeDoc is not null)
                {
                    projectName = ResolveActiveProjectDisplayName(activeDoc);
                    SmartConLogger.Info(
                        $"[FMImport] Single system category with no loadable families — " +
                        $"using source file name '{projectName}' as displayName");
                }
            }
            else if (systemAnalyses.Count == 1 && loadableFamilies.Count > 0)
            {
                SmartConLogger.Info(
                    $"[FMImport] Single system category BUT {loadableFamilies.Count} loadable " +
                    $"families present — using category DisplayName '{systemAnalyses[0].DisplayName}' " +
                    $"instead of project name to keep identity stable across re-imports");
            }

            foreach (var analysis in systemAnalyses)
            {
                var displayName = projectName ?? analysis.DisplayName;
                var types = analysis.Types
                    .Select(t => new SelectedSystemType(
                        t.UniqueId, t.Name, analysis.DisplayName, analysis.Category))
                    .ToList();

                // Map SelectedSystemType (Revit-bound) into the Core-level
                // FamilySourceTypeInfo DTO before crossing the
                // FamilyManager → Core boundary. Orchestrator and
                // extractor only ever see the DTO.
                var coreTypes = types
                    .Select(t => new FamilySourceTypeInfo(
                        t.UniqueId, t.Name, t.CategoryName, (int)t.Category))
                    .ToList();

                var source = new FamilyImportSource.SystemSource(
                    DisplayName: displayName,
                    CategoryId: (int)analysis.Category,
                    TypeUniqueIds: types.Select(t => t.UniqueId).ToList(),
                    TypeNames: types.Select(t => t.Name).ToList());

                var placeholderPath =
                    $"system://{(projectName is not null ? SafeFileName.GetBaseName(projectName) : "active-project")}/" +
                    $"{SafeFileName.GetBaseName(displayName)}";

                var row = await BuildSystemFamilyBatchRowVirtualAsync(
                    displayName, coreTypes, source, placeholderPath, ct, categoriesById);
                if (row is not null) result.Add(row);
            }
        }

        if (loadableFamilies.Count > 0)
        {
            foreach (var loadable in loadableFamilies)
            {
                var placeholderPath = $"loadable://{SafeFileName.GetBaseName(loadable.FamilyName)}";
                var source = new FamilyImportSource.LoadableSource(
                    FamilyName: loadable.FamilyName,
                    FamilyUniqueId: loadable.FamilyUniqueId,
                    CategoryName: loadable.CategoryName);

                var row = await BuildLoadableFamilyBatchRowVirtualAsync(
                    loadable, source, placeholderPath, ct, categoriesById);
                if (row is not null) result.Add(row);
            }
        }

        return result;
    }

    private async Task<FamilyBatchImportItem?> BuildSystemFamilyBatchRowVirtualAsync(
        string displayName,
        IReadOnlyList<FamilySourceTypeInfo> coreTypes,
        FamilyImportSource source,
        string placeholderPath,
        CancellationToken ct,
        IReadOnlyDictionary<string, CategoryNode>? categoriesById = null)
    {
        // v2.0.0: virtual batch row. No managed file is on disk yet, so
        // we cannot read its Revit version — the orchestrator writes the
        // .rvt AFTER the dialog confirms, then determines the version.
        // We use CurrentRevitVersion as a reasonable default for the
        // preview column (the user sees the version of Revit they're
        // running, which matches the version of the .rvt we will write).
        var revitVersion = CurrentRevitVersion;

        var normalizedName = Core.Services.FamilyManager.FamilyNameNormalizer.Normalize(displayName);
        var existingByName = await _catalogProvider.FindByNormalizedNameAsync(normalizedName, ct).ConfigureAwait(false);

        FamilyBatchImportStatus status;
        string? existingId = null;
        string? existingVersionLabel = null;
        string? targetCategoryId = null;
        string? targetCategoryName = null;

        if (existingByName is not null)
        {
            status = FamilyBatchImportStatus.Existing;
            existingId = existingByName.Id;
            existingVersionLabel = existingByName.CurrentVersionLabel;
            targetCategoryId = existingByName.CategoryId;
            targetCategoryName = existingByName.CategoryPath;
        }
        else
        {
            status = FamilyBatchImportStatus.New;
        }

        if (targetCategoryId is not null && categoriesById is not null
            && categoriesById.TryGetValue(targetCategoryId, out var cat) && cat is not null)
        {
            targetCategoryName = cat.FullPath ?? cat.Name;
        }

        // v2.0.0: precompute the canonical (catalogItemId, versionLabel,
        // managedRfaPath) triple through the precomputer — the SAME
        // service the dialog rename handler uses, so the initial build
        // and the post-rename re-derivation agree on every value (id +
        // version + path all move together, never piecemeal). The
        // precomputer itself re-runs FindByNormalizedNameAsync, so we
        // pay one extra read here; the trade-off is worth it for the
        // invariant guarantee.
        var precomputed = await _importPrecomputer
            .BuildPrecomputedTripleAsync(displayName, ".rvt", ct)
            .ConfigureAwait(false);

        SmartConLogger.Info(
            $"[FMImport.BuildSystem] displayName='{displayName}', " +
            $"existingByName={(existingByName?.Id ?? "<null>")}, " +
            $"precomputedCatalogItemId='{precomputed?.CatalogItemId ?? "<null>"}', " +
            $"precomputedVersionLabel='{precomputed?.VersionLabel ?? "<null>"}', " +
            $"precomputedManagedPath='{precomputed?.ManagedPath ?? "<null>"}'");

        return new FamilyBatchImportItem(
            FilePath: placeholderPath,
            FileName: displayName,
            RevitMajorVersion: revitVersion,
            Status: status,
            ExistingCatalogItemId: existingId,
            ExistingVersionLabel: existingVersionLabel,
            TargetCategoryId: targetCategoryId,
            TargetCategoryName: targetCategoryName,
            FamilySource: "system",
            TypeCount: coreTypes.Count,
            RevitCategory: displayName,
            SourceTypes: coreTypes,
            Source: source,
            PrecomputedCatalogItemId: precomputed?.CatalogItemId,
            PrecomputedVersionLabel: precomputed?.VersionLabel,
            PrecomputedManagedPath: precomputed?.ManagedPath);
    }

    private async Task<FamilyBatchImportItem?> BuildLoadableFamilyBatchRowVirtualAsync(
        LoadableFamilyInfo loadable,
        FamilyImportSource source,
        string placeholderPath,
        CancellationToken ct,
        IReadOnlyDictionary<string, CategoryNode>? categoriesById = null)
    {
        // v2.0.0: virtual batch row. No managed .rfa is on disk yet, so
        // the orchestrator's post-dialog flow will allocate the path,
        // call EditFamily + SaveAs into managed storage, and determine
        // the actual Revit version from the resulting file. For the
        // preview column we use the running Revit's version, which
        // matches the version of the .rfa we will produce.
        var revitVersion = CurrentRevitVersion;

        var normalizedName = Core.Services.FamilyManager.FamilyNameNormalizer.Normalize(loadable.FamilyName);
        var existingByName = await _catalogProvider.FindByNormalizedNameAsync(normalizedName, ct).ConfigureAwait(false);

        SmartConLogger.Debug(
            $"file='{loadable.FamilyName}' normalized='{normalizedName}' " +
            $"byName={(existingByName is null ? "null" : $"Id={existingByName.Id} CatId={existingByName.CategoryId ?? "<null>"} CatPath={existingByName.CategoryPath ?? "<null>"}")}");

        FamilyBatchImportStatus status;
        string? existingId = null;
        string? existingVersionLabel = null;
        string? targetCategoryId = null;
        string? targetCategoryName = null;

        if (existingByName is not null)
        {
            status = FamilyBatchImportStatus.Existing;
            existingId = existingByName.Id;
            existingVersionLabel = existingByName.CurrentVersionLabel;
            targetCategoryId = existingByName.CategoryId;
            targetCategoryName = existingByName.CategoryPath;
        }
        else
        {
            status = FamilyBatchImportStatus.New;
        }

        if (targetCategoryId is not null && categoriesById is not null
            && categoriesById.TryGetValue(targetCategoryId, out var cat) && cat is not null)
        {
            targetCategoryName = cat.FullPath ?? cat.Name;
        }

        // v2.0.0: same rationale as BuildSystemFamilyBatchRowVirtualAsync
        // — go through the precomputer so the initial build and the
        // post-rename re-derivation cannot drift apart.
        var precomputed = await _importPrecomputer
            .BuildPrecomputedTripleAsync(loadable.FamilyName, ".rfa", ct)
            .ConfigureAwait(false);

        return new FamilyBatchImportItem(
            FilePath: placeholderPath,
            FileName: loadable.FamilyName,
            RevitMajorVersion: revitVersion,
            Status: status,
            ExistingCatalogItemId: existingId,
            ExistingVersionLabel: existingVersionLabel,
            TargetCategoryId: targetCategoryId,
            TargetCategoryName: targetCategoryName,
            FamilySource: "loadable",
            TypeCount: loadable.TypeCount,
            RevitCategory: loadable.CategoryName,
            OriginalSourcePath: null,
            SourceTypes: null,
            Source: source,
            PrecomputedCatalogItemId: precomputed?.CatalogItemId,
            PrecomputedVersionLabel: precomputed?.VersionLabel,
            PrecomputedManagedPath: precomputed?.ManagedPath);
    }

    private string? StageLoadableFamilyFromProject(LoadableFamilyInfo info, string managedRfaPath)
    {
        if (string.IsNullOrEmpty(managedRfaPath))
        {
            throw new ArgumentException(
                "managedRfaPath is required — caller must compute it via ComputeLoadableFamilyManagedPath",
                nameof(managedRfaPath));
        }

        var doc = _revitContext.GetDocument();
        if (doc is null) return null;

        var family = doc.GetElement(info.FamilyUniqueId) as Autodesk.Revit.DB.Family;
        if (family is null)
        {
            SmartConLogger.Warn(
                $"Family '{info.FamilyName}' (uid='{info.FamilyUniqueId}') not found in active project [Action: убедитесь, что семейство размещено в активном проекте]");
            return null;
        }
        if (family.IsInPlace)
        {
            SmartConLogger.Warn(
                $"Skipping in-place family '{info.FamilyName}'");
            return null;
        }

        Document? familyDoc = null;
        try
        {
            familyDoc = doc.EditFamily(family);
            if (familyDoc is null || !familyDoc.IsFamilyDocument)
            {
                SmartConLogger.Warn(
                    $"EditFamily returned null/non-family for '{info.FamilyName}' [Action: проверьте, что семейство валидно и не заблокировано другим процессом]");
                return null;
            }

            // v2.0.0: caller pre-computed the managed path; SaveAs writes
            // straight into catalog storage — no transient _stage/ folder.
            var rfaPath = managedRfaPath;
            var parent = Path.GetDirectoryName(rfaPath);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            if (File.Exists(rfaPath))
            {
                File.SetAttributes(rfaPath, File.GetAttributes(rfaPath) & ~FileAttributes.ReadOnly);
                File.Delete(rfaPath);
            }

            familyDoc.SaveAs(rfaPath, new SaveAsOptions { OverwriteExistingFile = true });
            File.SetAttributes(rfaPath, File.GetAttributes(rfaPath) | FileAttributes.ReadOnly);
            SmartConLogger.Debug(
                $"Staged '{info.FamilyName}' → '{rfaPath}'");
            return rfaPath;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Failed to stage '{info.FamilyName}': {ex.Message} [Action: проверьте логи Revit и повторите импорт]");
            return null;
        }
        finally
        {
            if (familyDoc is not null)
            {
                try { familyDoc.Close(false); } catch { }
                // Defensive ReleaseComObject — required for batch processing of
                // 100+ families to prevent family-upgrade freeze (REVIT-237190).
                // Document is a RCW; without explicit release the runtime keeps
                // a reference until GC, which can hang Revit on shutdown.
                try { Marshal.ReleaseComObject(familyDoc); } catch { }
            }
        }
    }

}

