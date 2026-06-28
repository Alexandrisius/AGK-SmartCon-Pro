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
    /// Phase 27: uses FamilyImportPreparationService for unified
    /// open → extract → hash → dedup in a single pass. Documents are
    /// held open until the dialog is confirmed or cancelled.
    /// </summary>
    private async Task ShowBatchImportDialogAsync(string[] paths, string? categoryId, string? forcedExistingItemId = null)
    {
        IsLoading = true;
        try
        {
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

            var preparedItems = await _preparationService.PrepareForFileImportAsync(paths, CancellationToken.None);
            var items = new List<FamilyBatchImportItem>(preparedItems.Count);

            foreach (var p in preparedItems)
            {
                string? existingCategoryId = null;
                string? existingCategoryName = null;

                if (p.ExistingCatalogItemId is not null)
                {
                    try
                    {
                        var existingItem = await _catalogProvider.GetItemAsync(p.ExistingCatalogItemId, CancellationToken.None);
                        existingCategoryId = existingItem?.CategoryId;
                        existingCategoryName = existingItem?.CategoryPath;
                        if (existingCategoryId is not null && existingCategoryName is null)
                        {
                            var cat = await _categoryRepository.GetByIdAsync(existingCategoryId, CancellationToken.None);
                            existingCategoryName = cat?.Name;
                        }
                    }
                    catch (Exception ex)
                    {
                        SmartConLogger.Warn($"Failed to resolve category for existing item '{p.ExistingCatalogItemId}': {ex.Message} [Action: проверьте, что БД доступна]");
                    }
                }

                var status = p.ErrorMessage is not null
                    ? FamilyBatchImportStatus.Error
                    : p.Status;

                items.Add(new FamilyBatchImportItem(
                    FilePath: p.SourcePath,
                    FileName: p.DisplayName,
                    RevitMajorVersion: p.RevitMajorVersion,
                    Status: status,
                    ExistingCatalogItemId: forcedExistingItemId ?? p.ExistingCatalogItemId,
                    ExistingVersionLabel: p.ExistingVersionLabel,
                    TargetCategoryId: categoryId ?? existingCategoryId,
                    TargetCategoryName: categoryName ?? existingCategoryName,
                    FamilySource: p.FamilySource,
                    TypeCount: null,
                    RevitCategory: null,
                    OriginalSourcePath: null,
                    SourceTypes: p.SourceTypes,
                    Source: p.Source,
                    ContentHash: p.ContentHash?.HexString,
                    HashFormatVersion: p.ContentHash?.FormatVersion,
                    MatchedVersionLabel: p.MatchedVersionLabel)
                {
                    Action = status == FamilyBatchImportStatus.Duplicate
                        ? FamilyBatchImportAction.Skip
                        : FamilyBatchImportAction.IncrementVersion
                });
            }

            using var vm = new FamilyBatchImportViewModel(
                items,
                _dialogService,
                _viewModelFactory,
                categoryId,
                categoryName,
                _catalogProvider,
                importPrecomputer: _importPrecomputer,
                dedupService: _dedupService);
            var result = _dialogService.ShowBatchImportDialog(vm);
            if (result != true)
            {
                await _preparationService.CloseAllPreparedDocumentsAsync(CancellationToken.None);
                return;
            }

            var selectedItems = vm.GetResultItems();
            var progress = new Progress<FamilyImportProgress>(p =>
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_ImportProgress) ?? "Importing {0} of {1}...",
                    p.CurrentFileIndex + 1, p.TotalFiles);
            });

            var importResult = await _importService.ImportBatchAsync(selectedItems, categoryId, progress, CancellationToken.None);

            await _preparationService.CloseAllPreparedDocumentsAsync(CancellationToken.None);

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

            // Auto-clear status after 10 seconds
            FireAndForget(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                StatusMessage = string.Empty;
            }, nameof(ImportFilesAsync));
        }
        catch (Exception ex)
        {
            await _preparationService.CloseAllPreparedDocumentsAsync(CancellationToken.None);
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
        var ct = CancellationToken.None;

        var systemAnalyses = analysis.SystemTypes
            .GroupBy(s => s.Category)
            .Select(g => new CategoryAnalysis(
                g.Key,
                g.First().CategoryName,
                g.Select(t => new SystemTypeInfo(t.Name, t.UniqueId)).ToList()))
            .ToList();

        var prepared = await _preparationService.PrepareProjectImportAsync(
            systemAnalyses, analysis.LoadableFamilies, ct);

        return await MapPreparedItemsToBatchItemsAsync(prepared, ct);
    }

    private async Task<List<FamilyBatchImportItem>> BuildActiveProjectBatchItemsAsync(
        IReadOnlyList<CategoryAnalysis> systemAnalyses,
        IReadOnlyList<LoadableFamilyInfo> loadableFamilies)
    {
        var ct = CancellationToken.None;

        string? projectNameOverride = null;
        if (systemAnalyses.Count == 1 && loadableFamilies.Count == 0)
        {
            var activeDoc = _revitContext.GetDocument();
            if (activeDoc is not null)
            {
                projectNameOverride = ResolveActiveProjectDisplayName(activeDoc);
                SmartConLogger.Info(
                    $"[FMImport] Single system category with no loadable families — " +
                    $"using source file name '{projectNameOverride}' as displayName");
            }
        }

        var prepared = await _preparationService.PrepareProjectImportAsync(
            systemAnalyses, loadableFamilies, ct);

        if (projectNameOverride is not null && prepared.Count == 1 && prepared[0].FamilySource == "system")
        {
            prepared = new List<PreparedFamilyItem>
            {
                prepared[0] with
                {
                    DisplayName = projectNameOverride,
                    SourcePath = $"system://{SafeFileName.GetBaseName(projectNameOverride)}"
                }
            };
        }

        return await MapPreparedItemsToBatchItemsAsync(prepared, ct);
    }

    /// <summary>
    /// Maps PreparedFamilyItem list to FamilyBatchImportItem list.
    /// Does precompute triple + category resolution for each item.
    /// </summary>
    private async Task<List<FamilyBatchImportItem>> MapPreparedItemsToBatchItemsAsync(
        IReadOnlyList<PreparedFamilyItem> prepared, CancellationToken ct)
    {
        var allCategories = await _categoryRepository.GetAllAsync(ct).ConfigureAwait(false);
        var categoriesById = allCategories.ToDictionary(c => c.Id);

        var result = new List<FamilyBatchImportItem>(prepared.Count);

        foreach (var p in prepared)
        {
            string? existingCategoryId = null;
            string? existingCategoryName = null;

            if (p.ExistingCatalogItemId is not null)
            {
                try
                {
                    var existingItem = await _catalogProvider.GetItemAsync(p.ExistingCatalogItemId, ct);
                    existingCategoryId = existingItem?.CategoryId;
                    existingCategoryName = existingItem?.CategoryPath;
                    if (existingCategoryId is not null && existingCategoryName is null && categoriesById.TryGetValue(existingCategoryId, out var cat))
                        existingCategoryName = cat.FullPath ?? cat.Name;
                }
                catch (Exception ex)
                {
                    SmartConLogger.Warn($"Failed to resolve category for existing item '{p.ExistingCatalogItemId}': {ex.Message} [Action: проверьте, что БД доступна]");
                }
            }

            var extension = p.FamilySource == "system" ? ".rvt" : ".rfa";
            var precomputed = await _importPrecomputer
                .BuildPrecomputedTripleAsync(p.DisplayName, extension, ct)
                .ConfigureAwait(false);

            var status = p.ErrorMessage is not null
                ? FamilyBatchImportStatus.Error
                : p.Status;

            result.Add(new FamilyBatchImportItem(
                FilePath: p.SourcePath,
                FileName: p.DisplayName,
                RevitMajorVersion: p.RevitMajorVersion,
                Status: status,
                ExistingCatalogItemId: p.ExistingCatalogItemId,
                ExistingVersionLabel: p.ExistingVersionLabel,
                TargetCategoryId: existingCategoryId,
                TargetCategoryName: existingCategoryName,
                FamilySource: p.FamilySource,
                TypeCount: p.SourceTypes?.Count,
                RevitCategory: null,
                OriginalSourcePath: null,
                SourceTypes: p.SourceTypes,
                Source: p.Source,
                PrecomputedCatalogItemId: precomputed?.CatalogItemId,
                PrecomputedVersionLabel: precomputed?.VersionLabel,
                PrecomputedManagedPath: precomputed?.ManagedPath,
                ContentHash: p.ContentHash?.HexString,
                HashFormatVersion: p.ContentHash?.FormatVersion,
                MatchedVersionLabel: p.MatchedVersionLabel)
            {
                Action = status == FamilyBatchImportStatus.Duplicate
                    ? FamilyBatchImportAction.Skip
                    : FamilyBatchImportAction.IncrementVersion
            });
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

    private string? StageLoadableFamilyFromProject(LoadableFamilyInfo info, string managedRfaPath, string? sourcePath = null)
    {
        if (string.IsNullOrEmpty(managedRfaPath))
        {
            throw new ArgumentException(
                "managedRfaPath is required — caller must compute it via ComputeLoadableFamilyManagedPath",
                nameof(managedRfaPath));
        }

        var rfaPath = managedRfaPath;
        var parent = Path.GetDirectoryName(rfaPath);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        if (File.Exists(rfaPath))
        {
            File.SetAttributes(rfaPath, File.GetAttributes(rfaPath) & ~FileAttributes.ReadOnly);
            File.Delete(rfaPath);
        }

        var heldDoc = sourcePath is not null
            ? _preparationService.GetOpenedDocument(sourcePath)
            : null;

        if (heldDoc is not null)
        {
            try
            {
                heldDoc.SaveAs(rfaPath, new SaveAsOptions { OverwriteExistingFile = true });
                File.SetAttributes(rfaPath, File.GetAttributes(rfaPath) | FileAttributes.ReadOnly);
                if (sourcePath is not null) _preparationService.ReleaseDocument(sourcePath);
                SmartConLogger.Debug($"Staged '{info.FamilyName}' from held doc → '{rfaPath}'");
                return rfaPath;
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"SaveAs from held doc failed for '{info.FamilyName}': {ex.Message} — falling back to EditFamily [Action: проверьте логи Revit]");
                if (sourcePath is not null) _preparationService.ReleaseDocument(sourcePath);
            }
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

