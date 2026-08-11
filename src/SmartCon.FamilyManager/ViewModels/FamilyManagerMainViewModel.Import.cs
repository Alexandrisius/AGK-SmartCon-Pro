using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using SmartCon.FamilyManager.Services.Import;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyManagerMainViewModel
{
    [RelayCommand(CanExecute = nameof(CanImportFiles))]
    private async Task ImportFilesAsync()
    {
        using var _scope = SmartConLogger.BeginScope("FMImport",
            ("Method", "ImportFilesAsync"));
        if (!await EnsureDatabaseUpToDateAsync().ConfigureAwait(true)) return;
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
        if (!await EnsureDatabaseUpToDateAsync().ConfigureAwait(true)) return;

        var title = LanguageManager.GetString(StringLocalization.Keys.FM_ImportFile) ?? "Import File";
        var paths = _dialogService.ShowImportFilesDialog(title);
        if (paths is null || paths.Length == 0) return;

        await ShowBatchImportDialogAsync(paths, categoryNode.CategoryId);
    }

    private bool _batchDialogOpen;

    /// <summary>
    /// Shows the batch import dialog for the given file paths.
    /// Phase 27: uses FamilyImportPreparationService for unified
    /// open → extract → hash → dedup in a single pass. Documents are
    /// held open until the dialog is confirmed or cancelled.
    /// </summary>
    private async Task ShowBatchImportDialogAsync(string[] paths, string? categoryId, string? forcedExistingItemId = null)
    {
        using var _scope = SmartConLogger.BeginScope("BatchImport",
            ("Method", nameof(ShowBatchImportDialogAsync)),
            ("Paths", paths.Length),
            ("CategoryId", categoryId ?? "<none>"));

        if (_batchDialogOpen)
        {
            SmartConLogger.Warn(
                "Batch import dialog is already open — ignoring re-entry " +
                "[Action: дождитесь завершения текущего импорта или закройте его диалог]");
            return;
        }
        _batchDialogOpen = true;

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

            SmartConLogger.Info($"Preparing {paths.Length} file(s)...");
            var preparedItems = await _preparationService.PrepareForFileImportAsync(paths, CancellationToken.None);
            SmartConLogger.Info($"Prepared {preparedItems.Count} items");
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

                var precomputed = await _importPrecomputer
                    .BuildPrecomputedTripleAsync(p.DisplayName, ".rfa", p.FamilySource, p.ExistingCatalogItemId, CancellationToken.None)
                    .ConfigureAwait(false);

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
                    TypeCount: SnapshotExtractionMapper.ResolveTypeCount(
                        p.LoadableSnapshot, p.SystemSnapshot, p.SourceTypes),
                    RevitCategory: p.LoadableSnapshot?.Category,
                    OriginalSourcePath: null,
                    SourceTypes: p.SourceTypes,
                    Source: p.Source,
                    PrecomputedCatalogItemId: precomputed?.CatalogItemId,
                    PrecomputedVersionLabel: precomputed?.VersionLabel,
                    PrecomputedManagedPath: precomputed?.ManagedPath,
                    ContentHash: p.ContentHash?.HexString,
                    HashFormatVersion: p.ContentHash?.FormatVersion,
                    MatchedVersionLabel: p.MatchedVersionLabel,
                    LoadableSnapshot: p.LoadableSnapshot,
                    SystemSnapshot: p.SystemSnapshot,
                    GeometryPerType: p.GeometryPerType,
                    IsCrossNameDuplicate: p.IsCrossNameDuplicate,
                    MatchedItemName: p.MatchedItemName,
                    ExistingCategoryId: existingCategoryId,
                    ExistingCategoryPath: existingCategoryName,
                    HealthReport: p.HealthReport,
                    DependencyLinks: p.DependencyLinks)
                {
                    // ADR-066: dependency rows exist to guarantee PRESENCE in
                    // the catalog. Duplicates default to Skip (dedup-link).
                    // E2 (#209, owner decision): a shared-nested row with NEW
                    // content of an existing item imports like a regular
                    // family — new ACTIVE version (other parents embedding
                    // the older copy get the drift badge); routing rows stay
                    // conservative (Skip) — system parents resolve children
                    // dynamically at their active version anyway.
                    Action = status == FamilyBatchImportStatus.Duplicate
                        ? FamilyBatchImportAction.Skip
                        : p.DependencyLinks is not null && status == FamilyBatchImportStatus.Existing
                            ? (p.DependencyLinks.Any(l => l.Kind == FamilyDependencyKind.SharedNested)
                                ? FamilyBatchImportAction.IncrementVersion
                                : FamilyBatchImportAction.Skip)
                            : FamilyBatchImportAction.IncrementVersion
                });
            }

            SmartConLogger.Info($"Built {items.Count} batch items, creating ViewModel...");
            var staging = new FileFamilyStagingService(
                _awaitableEvent, _preparationService, _importService);
            var executor = new FileFamilyBatchImportExecutor(
                staging,
                _importService,
                _familyDependencyRepository,
                _dataImportService,
                _sharedNestedRepository,
                CurrentRevitVersion);
            using var vm = new FamilyBatchImportViewModel(
                items,
                _dialogService,
                _viewModelFactory,
                categoryId,
                categoryName,
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
                return;
            }

            StatusMessage = BuildImportStatusMessage(
                vm.ImportSuccessCount, vm.ImportSkippedCount, vm.ImportErrorCount, items.Count);

            if (vm.ImportSuccessCount > 0)
            {
                await LoadTreeAsync();
            }

            var completedMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportCompleted) ?? "Import {0}/{1} families completed",
                vm.ImportSuccessCount, items.Count);
            _freezeRecovery.Nudge(completedMessage);

            // Auto-clear status after 10 seconds
            FireAndForget(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                SmartConLogger.Debug(
                    $"Auto-clearing StatusMessage (thread={Environment.CurrentManagedThreadId}, marshalling via _dispatcher)");
                _ = _dispatcher.InvokeAsync(() => StatusMessage = string.Empty);
            }, nameof(ImportFilesAsync));
        }
        catch (Exception ex)
        {
            SmartConLogger.Error(
                $"ImportFilesAsync failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace} " +
                "[Action: check smartcon.log for the failing family; prepared documents will be closed]");
            await _preparationService.CloseAllPreparedDocumentsAsync(CancellationToken.None);
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Import error: {0}",
                ex.Message);
        }
        finally
        {
            IsLoading = false;
            _batchDialogOpen = false;
        }
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
        if (!await EnsureDatabaseUpToDateAsync().ConfigureAwait(true)) return;
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
                SmartConLogger.Error(
                    $"ImportSelectedElementsAsync (pick elements) failed: {ex.GetType().Name}: {ex.Message}");
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Ошибка импорта: {0}",
                    ex.Message);
                IsLoading = false;
            }
        });

        if (analysis is null)
        {
            // Pick cancelled (Esc) — a normal gesture, stay silent.
            IsLoading = false;
            return;
        }

        if (analysis.IsEmpty)
        {
            IsLoading = false;
            StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_ImportSelectedNothingImportable)
                ?? "Ни один из выбранных элементов не подлежит импорту";
            _dialogService.ShowInfo(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportSelectedElements) ?? "Импорт выделенных элементов",
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportSelectedNothingImportableBody)
                    ?? "Ни один из выбранных элементов не подлежит импорту.\n\nВыбирайте элементы системных категорий (трубы, воздуховоды, стены и т.д.) или экземпляры загружаемых семейств. Подробности — в логе.");
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

            // #186: "Импорт выделенных элементов" НЕ закрывает мини-проект
            // после импорта (в отличие от "Импорт активного файла").
            var outcome = await ProcessProjectImportAsync(batchItems);

            // #185: automatic stale check after the import (active document
            // stays the user's project — its other types of the reimported
            // items may carry markers of older versions).
            if (outcome.ImportStarted && outcome.SuccessCount > 0)
            {
                await RunPostImportStaleCheckAsync(outcome.ImportedItems);
            }
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
                g.Select(t => new SystemTypeInfo(t.Name, t.UniqueId, t.FamilyName, t.FamilyKey)).ToList()))
            .ToList();

        systemAnalyses = ApplyPlacementVersionGate(systemAnalyses);

        var prepared = await _preparationService.PrepareProjectImportAsync(
            systemAnalyses, analysis.LoadableFamilies, ct);

        return await MapPreparedItemsToBatchItemsAsync(prepared, ct);
    }

    /// <summary>
    /// Версионный гейт размещения (ADR-027 Phase 2): категории, чей placement
    /// API недоступен на текущей версии Revit (потолки — 2022+, ограждения —
    /// 2025+), исключаются из импорта с styled-диалогом. Эталонный
    /// мини-проект без размещённых инстансов не считается валидным —
    /// категория не попадает в библиотеку на этой версии Revit.
    /// </summary>
    private List<CategoryAnalysis> ApplyPlacementVersionGate(
        IReadOnlyList<CategoryAnalysis> systemAnalyses)
    {
        if (systemAnalyses.Count == 0 || CurrentRevitVersion <= 0)
        {
            return systemAnalyses as List<CategoryAnalysis> ?? systemAnalyses.ToList();
        }

        var gated = systemAnalyses
            .Where(a => !SystemCategoryPlacementAvailability.IsSupported(a.Category, CurrentRevitVersion))
            .ToList();
        if (gated.Count == 0)
        {
            return systemAnalyses as List<CategoryAnalysis> ?? systemAnalyses.ToList();
        }

        var items = string.Join("\n", gated.Select(g => string.Format(
            LocalizationService.GetString("FM_ImportVersionGateItem") ?? "• {0} — требуется Revit {1}+",
            g.DisplayName,
            SystemCategoryPlacementAvailability.GetMinRevitVersion(g.Category))));

        SmartConLogger.Info(
            $"Placement version gate: excluded {gated.Count} categories on Revit {CurrentRevitVersion}: " +
            string.Join(", ", gated.Select(g => g.Category.ToString())));

        _dialogService.ShowInfo(
            LocalizationService.GetString("FM_ImportVersionGateTitle") ?? "Импорт ограничен версией Revit",
            string.Format(
                LocalizationService.GetString("FM_ImportVersionGateBody")
                    ?? "Следующие системные категории нельзя загрузить в библиотеку на Revit {0}:\n{1}\n\nОни не будут импортированы.",
                CurrentRevitVersion, items));

        return systemAnalyses
            .Where(a => SystemCategoryPlacementAvailability.IsSupported(a.Category, CurrentRevitVersion))
            .ToList();
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

        var singleSystemItem = prepared.Count(p => p.FamilySource == "system") == 1
            ? prepared.First(p => p.FamilySource == "system")
            : null;
        if (projectNameOverride is not null && singleSystemItem is not null)
        {
            var oldSourcePath = singleSystemItem.SourcePath;
            var newSourcePath = $"system://{SafeFileName.GetBaseName(projectNameOverride)}";
            prepared = prepared
                .Select(p =>
                {
                    if (p.SourcePath == oldSourcePath && p.FamilySource == "system")
                    {
                        return p with
                        {
                            DisplayName = projectNameOverride,
                            SourcePath = newSourcePath,
                            // The staged pipeline reads SystemSource.DisplayName — the
                            // override must reach it too, otherwise the staged
                            // mini-project is logged/marked under the category name
                            // instead of the user-facing family name.
                            Source = p.Source is SmartCon.Core.Models.FamilyManager.FamilyImportSource.SystemSource sys
                                ? sys with { DisplayName = projectNameOverride }
                                : p.Source
                        };
                    }

                    // ADR-066: dependency rows point at the parent by its
                    // SourcePath — the rename must rewrite their links too,
                    // otherwise Phase 3 cannot match parent→child.
                    return p.DependencyLinks is null
                        ? p
                        : p with
                        {
                            DependencyLinks = p.DependencyLinks
                                .Select(l => l.ParentSourcePath == oldSourcePath
                                    ? l with { ParentSourcePath = newSourcePath }
                                    : l)
                                .ToList()
                        };
                })
                .ToList();
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
            // Issue #126: when dedup matched this row to an existing item
            // by content hash (possibly under a different name), the
            // precomputed triple must target THAT item, not a name lookup.
            var precomputed = await _importPrecomputer
                .BuildPrecomputedTripleAsync(p.DisplayName, extension, p.FamilySource, p.ExistingCatalogItemId, ct)
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
                TypeCount: SnapshotExtractionMapper.ResolveTypeCount(
                    p.LoadableSnapshot, p.SystemSnapshot, p.SourceTypes),
                RevitCategory: p.SystemSnapshot?.CategoryName ?? p.LoadableSnapshot?.Category,
                OriginalSourcePath: null,
                SourceTypes: p.SourceTypes,
                Source: p.Source,
                PrecomputedCatalogItemId: precomputed?.CatalogItemId,
                PrecomputedVersionLabel: precomputed?.VersionLabel,
                PrecomputedManagedPath: precomputed?.ManagedPath,
                ContentHash: p.ContentHash?.HexString,
                HashFormatVersion: p.ContentHash?.FormatVersion,
                MatchedVersionLabel: p.MatchedVersionLabel,
                LoadableSnapshot: p.LoadableSnapshot,
                SystemSnapshot: p.SystemSnapshot,
                IsCrossNameDuplicate: p.IsCrossNameDuplicate,
                MatchedItemName: p.MatchedItemName,
                ExistingCategoryId: existingCategoryId,
                ExistingCategoryPath: existingCategoryName,
                HealthReport: p.HealthReport,
                DependencyLinks: p.DependencyLinks)
            {
                // ADR-066: dependency rows (routing fittings, shared nested)
                // exist to guarantee PRESENCE in the catalog. Duplicates
                // default to Skip (dedup-link). E2 (#209, owner decision):
                // a shared-nested row with NEW content of an existing item
                // imports like a regular family — new ACTIVE version (other
                // parents embedding the older copy get the drift badge);
                // routing rows stay conservative (Skip).
                Action = status == FamilyBatchImportStatus.Duplicate
                    ? FamilyBatchImportAction.Skip
                    : p.DependencyLinks is not null && status == FamilyBatchImportStatus.Existing
                        ? (p.DependencyLinks.Any(l => l.Kind == FamilyDependencyKind.SharedNested)
                            ? FamilyBatchImportAction.IncrementVersion
                            : FamilyBatchImportAction.Skip)
                        : FamilyBatchImportAction.IncrementVersion
            });
        }

        return result;
    }
}
