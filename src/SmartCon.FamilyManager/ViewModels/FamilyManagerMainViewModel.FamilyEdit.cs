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

        // Phase 1 — gather metadata from the active document (UI thread).
        var snapshot = await _awaitableEvent.RaiseAsync<ActiveFamilySnapshot>(obj =>
        {
            var uiApp = (Autodesk.Revit.UI.UIApplication)obj;
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc is null || !doc.IsFamilyDocument)
            {
                return new ActiveFamilySnapshot(null, string.Empty, 0, null);
            }
            var revitVersion = _fileInfoReader.ReadRevitVersion(doc.PathName) ?? CurrentRevitVersion;
            var baseName = SafeFileName.GetBaseName(
                string.IsNullOrEmpty(doc.PathName) ? doc.Title : doc.PathName);
            return new ActiveFamilySnapshot(doc, baseName, revitVersion, doc.PathName);
        });

        if (snapshot.Document is null)
        {
            SmartConLogger.Warn("No active family document at import time — aborting");
            return;
        }

        var normalizedName = Core.Services.FamilyManager.FamilyNameNormalizer.Normalize(snapshot.BaseName);
        var existingByName = await _catalogProvider.FindByNormalizedNameAsync(normalizedName, CancellationToken.None);

        var existingCategoryId = existingByName?.CategoryId;
        string? existingCategoryName = null;
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

        // Phase 2 — show batch dialog with a placeholder FilePath. The actual
        // managed copy is created only AFTER the user confirms. Cancellation
        // is safe — no temp file was created.
        var placeholderFilePath = snapshot.OriginalPathName ?? $"active://{snapshot.BaseName}";
        var item = new FamilyBatchImportItem(
            placeholderFilePath,
            snapshot.BaseName,
            snapshot.RevitVersion,
            existingByName is not null ? FamilyBatchImportStatus.Existing : FamilyBatchImportStatus.New,
            existingByName?.Id,
            existingByName?.CurrentVersionLabel,
            existingCategoryId,
            existingCategoryName,
            FamilySource: "loadable",
            TypeCount: null,
            RevitCategory: null,
            OriginalSourcePath: snapshot.OriginalPathName);

        using var vm = new FamilyBatchImportViewModel(new[] { item }, _dialogService, _viewModelFactory, catalogProvider: _catalogProvider);
        if (_dialogService.ShowBatchImportDialog(vm) != true)
        {
            // No cleanup needed — no temp file was created. The active
            // family document remains open and untouched. (Bug fix: in v1.x
            // the import flow created a temp file via SaveAs BEFORE the
            // dialog, so cancelling forced a close on the user's family
            // document and could discard their edits.)
            return;
        }

        var selectedItems = vm.GetResultItems();
        var toImport = selectedItems.Where(i => i.Action != FamilyBatchImportAction.Skip).ToList();
        if (toImport.Count == 0) return;

        // Phase 3 — compute the target managed path, then call SaveAs on the
        // active document on the Revit UI thread. The user-selected action
        // determines whether we create a new version or overwrite current.
        var isOverwrite = toImport[0].Action == FamilyBatchImportAction.OverwriteCurrent
            && existingByName is not null;
        var versionLabel = isOverwrite
            ? existingByName!.CurrentVersionLabel
            : (existingByName is null
                ? "v1"
                : null /* computed below */);

        var managedRfaPath = await _awaitableEvent.RaiseAsync<string?>(obj =>
        {
            try
            {
                var dbRoot = _databaseManager.GetActiveDatabasePath();
                if (string.IsNullOrEmpty(dbRoot))
                {
                    SmartConLogger.Error("No active database selected");
                    return null;
                }

                var catalogItemId = existingByName?.Id ?? Guid.NewGuid().ToString();
                var managedDir = Path.Combine(dbRoot, "files", catalogItemId);
                if (versionLabel is null)
                {
                    // IncrementVersion: figure out next version label.
                    var existingDirs = Directory.Exists(managedDir)
                        ? Directory.GetDirectories(managedDir)
                            .Select(p => Path.GetFileName(p) ?? string.Empty)
                            .ToList()
                        : new List<string>();
                    var next = existingDirs
#pragma warning disable CA1846 // Prefer AsSpan over Substring (net48 compat — no AsSpan on string in net48)
                        .Where(d => d.StartsWith("v") && d.Length > 1)
                        .Select(d => int.TryParse(d.Substring(1), out var n) ? n : 0)
                        .DefaultIfEmpty(0)
#pragma warning restore CA1846
                        .Max() + 1;
                    versionLabel = $"v{next}";
                }

                var versionDir = Path.Combine(managedDir, versionLabel);
                Directory.CreateDirectory(versionDir);

                var invalid = Path.GetInvalidFileNameChars();
                var safeName = string.Concat(snapshot.BaseName.Select(c => invalid.Contains(c) ? '_' : c));
                var managedPath = Path.Combine(versionDir, safeName + ".rfa");

                // Read-only managed files: clear before SaveAs (Revit's
                // SaveAs requires write access on the target).
                if (File.Exists(managedPath))
                {
                    File.SetAttributes(managedPath, File.GetAttributes(managedPath) & ~FileAttributes.ReadOnly);
                    File.Delete(managedPath);
                }

                snapshot.Document!.SaveAs(managedPath, new SaveAsOptions { OverwriteExistingFile = true });
                File.SetAttributes(managedPath, File.GetAttributes(managedPath) | FileAttributes.ReadOnly);

                return managedPath;
            }
            catch (Exception ex)
            {
                SmartConLogger.Error($"SaveAs to managed storage failed: {ex.Message}");
                return null;
            }
        });

        if (string.IsNullOrEmpty(managedRfaPath))
        {
            StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Import error";
            return;
        }

        // Phase 4 — record the file in the catalog. Reuse ImportFileAsync
        // with OriginalSourcePath pointing back to the user's original .rfa
        // (so the .txt Type Catalog sidecar lookup still works).
        var request = new FamilyImportRequest(
            FilePath: managedRfaPath!,
            RevitMajorVersion: snapshot.RevitVersion,
            Category: toImport[0].TargetCategoryName,
            Tags: null,
            Description: null,
            CategoryId: toImport[0].TargetCategoryId ?? existingCategoryId,
            FamilySource: "loadable",
            RevitCategory: null,
            FileName: snapshot.BaseName,
            OriginalSourcePath: snapshot.OriginalPathName);

        var progress = new Progress<FamilyImportProgress>(p =>
        {
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportProgress) ?? "Importing {0} of {1}...",
                p.CurrentFileIndex + 1, p.TotalFiles);
        });

        var importResult = await _importService.ImportFileAsync(request, CancellationToken.None);

        if (importResult.Success)
        {
            await LoadTreeAsync();
        }

        await ExtractAttributesForImportedFamilies(new[] { importResult });

        var total = 1;
        var success = importResult.Success ? 1 : 0;
        var skipped = 0;
        var errors = importResult.Success ? 0 : 1;
        StatusMessage = BuildImportStatusMessage(success, skipped, errors, total);

        // Phase 5 — close the active family document on the Revit UI thread
        // and return focus to the project (if one was open). The user has
        // finished with this family — it's now in the catalog, managed as
        // read-only, and there's no reason to keep it loaded. Pass the
        // post-SaveAs PathName so we close the right document even when
        // .SaveAs() switched active focus.
        if (importResult.Success && snapshot.Document is not null)
        {
            await CloseFamilyDocumentAsync(managedRfaPath!);
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
            SmartConLogger.Warn($"CloseFamilyDocumentAsync failed: {ex.Message}");
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
            batchItems, _dialogService, _viewModelFactory,
            defaultCategoryId, defaultCategoryName, _catalogProvider);
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
                SmartConLogger.Warn("Active document is null — cannot stage system families");
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

                var managedRvtPath = ComputeSystemFamilyManagedPath(source.DisplayName);
                if (string.IsNullOrEmpty(managedRvtPath))
                {
                    SmartConLogger.Warn($"Cannot compute managed path for '{source.DisplayName}' — skipping [Action: check active catalog DB is selected]");
                    continue;
                }

                SmartConLogger.Info(
                    $"Staging system family '{source.DisplayName}': categoryId={source.CategoryId}, typeCount={source.TypeUniqueIds.Count}, target='{managedRvtPath}'");

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
                SmartConLogger.Warn("Active document is null — cannot stage loadable families");
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

                var managedRfaPath = ComputeLoadableFamilyManagedPath(source.FamilyName);
                if (string.IsNullOrEmpty(managedRfaPath))
                {
                    SmartConLogger.Warn($"Cannot compute managed path for '{source.FamilyName}' — skipping");
                    continue;
                }

                var info = new LoadableFamilyInfo(
                    source.FamilyName, source.FamilyUniqueId, source.CategoryName, item.TypeCount ?? 0);
                string? rfaPath;
                try
                {
                    rfaPath = StageLoadableFamilyFromProject(info, managedRfaPath!);
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
                if (!File.Exists(task.ManagedRfaPath))
                {
                    SmartConLogger.Warn(
                        $"Managed .rfa missing: '{task.ManagedRfaPath}'");
                    continue;
                }

                var extraction = await ExtractFromManagedFileAsync(task.ManagedRfaPath, Array.Empty<string>(), CancellationToken.None);
                if (extraction.Success)
                {
                    await _dataImportService.SaveExtractionResultAsync(
                        task.CatalogItemId, extraction, task.VersionId, task.FileId, CancellationToken.None);
                    SmartConLogger.Info(
                        $"Extracted {extraction.Types.Count} type(s) from '{Path.GetFileName(task.ManagedRfaPath)}' (CatalogItemId={task.CatalogItemId})");

                    // ADR-034: persist shared-nested names extracted in the
                    // same call. This is the third call site (loadable path);
                    // the file is also opened by LoadableFamilyTypeResolver
                    // (ResolveTypesFromRfa) earlier in the pipeline, but the
                    // user reported issue is the 4-dialog MFC upgrade storm
                    // on a 2-file batch import — fixing that means
                    // deduping within ExtractFromManagedFile specifically.
                    await SaveSharedNestedNamesAsync(
                        task.CatalogItemId,
                        task.VersionId,
                        extraction.SharedNestedFamilyNamesSafe,
                        CancellationToken.None);
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
        var extractionResults = new List<(string CatalogItemId, FamilyExtractionResult Result, string? VersionId, string? FileId)>();

        try
        {
            foreach (var item in importResults)
            {
                if (!item.Success || item.WasSkippedAsDuplicate) continue;
                if (string.IsNullOrEmpty(item.CatalogItemId)) continue;
                var catalogItemId = item.CatalogItemId!;

                var resolved = await _fileResolver.ResolveForLoadAsync(catalogItemId, CurrentRevitVersion, CancellationToken.None).ConfigureAwait(true);

                if (string.IsNullOrEmpty(resolved.AbsolutePath)) continue;

                var extraction = await ExtractFromManagedFileAsync(resolved.AbsolutePath, Array.Empty<string>(), CancellationToken.None);
                if (extraction.Success)
                {
                    extractionResults.Add((catalogItemId, extraction, item.VersionId, item.FileId));
                    SmartConLogger.Info(
                        $"Extracted {extraction.Types.Count} type(s) from '{Path.GetFileName(resolved.AbsolutePath)}'");

                    // ADR-034: persist shared-nested names extracted in the
                    // same call (V3 — single OpenDocumentFile per .rfa).
                    await SaveSharedNestedNamesAsync(
                        catalogItemId,
                        item.VersionId,
                        extraction.SharedNestedFamilyNamesSafe,
                        CancellationToken.None);
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
                foreach (var (catalogItemId, result, versionId, fileId) in extractionResults)
                {
                    // v2.0.0: Type Catalog (.txt) no longer stored in managed
                    // storage. Save unconditionally (ADR-033 bake-in).
                    await _dataImportService.SaveExtractionResultAsync(
                        catalogItemId, result, versionId, fileId, CancellationToken.None);
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

