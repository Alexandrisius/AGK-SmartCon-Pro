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
            // ADR-072 Phase 3: the routing tab needs the item's Revit
            // category ordinal — the row model doesn't carry it, one cheap
            // single-row read here.
            var catalogItem = await _catalogProvider.GetItemAsync(itemId);
            // «Создано» (owner stress test 2026-09-01): the field was ALWAYS
            // empty — a hardcoded null was passed here while the DB column
            // holds the real timestamp; source it from the loaded item.
            var createdAt = catalogItem is not null && catalogItem.CreatedAtUtc != default
                ? catalogItem.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : null;
            vm = _viewModelFactory.CreatePropertiesViewModel(
                SelectedItem.Id,
                SelectedItem.Name,
                SelectedItem.Description,
                SelectedItem.CategoryId,
                SelectedItem.CategoryName,
                SelectedItem.Tags,
                SelectedItem.ContentStatus,
                SelectedItem.VersionLabel,
                createdAt,
                updatedAt,
                SelectedItem.RevitCategory,
                isReadOnly: !CanEdit,
                familySource: SelectedItem.FamilySource,
                revitCategoryId: catalogItem?.RevitCategoryId);
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

            // ADR-072 World B: routing edits make the loaded project types
            // drift RIGHT AWAY — refresh the stale snapshot before the tree
            // rebuild so the badges repaint without a manual «Проверить».
            // Owner stress test 2026-09-01 (баг 4): MakeActive on the
            // Versions tab has the same effect (catalog active moved, the
            // project markers did not) — recheck through the same
            // post-import path (both system and loadable, presence-aware).
            if (vm.RoutingLinksChanged || vm.ActiveVersionChanged)
            {
                await RunPostImportStaleCheckAsync(new[]
                {
                    new ImportedCatalogItem(itemId, SelectedItem.Name, SelectedItem.FamilySource),
                });
            }

            // MakeActive on the Versions tab commits to the DB immediately —
            // even a Cancelled dialog may have changed the active version's
            // Revit major version, which drives the tree's availability badge.
            // E5 (#213): a deleted parent version frees dependency links —
            // the tree must rebuild to clear the freed child's paperclip.
            if (result != true && !vm.ActiveVersionChanged && !vm.VersionsChanged && !vm.RoutingLinksChanged) return;

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
                var uiDoc = app.OpenAndActivateDocument(resolved.AbsolutePath);
                // #205: some catalog rows point at mini-project .rvt files
                // while family_source != "system" (classification residue),
                // so the loadable edit path opens minis too. The saved
                // starting view makes them open nicely already; the
                // activator adds ZoomToFit. Guarded by the managed-storage
                // path pattern — regular .rfa files are untouched.
                if (MiniProjectPathPattern.IsMiniProjectPath(resolved.AbsolutePath))
                {
                    MiniProjectViewActivator.Activate(uiDoc);
                }
            }
            catch (Exception ex)
            {
                SmartConLogger.Error($"EditFamily OpenAndActivateDocument failed: {ex.Message}");
            }
        });
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
                var uiDoc = app.OpenAndActivateDocument(resolved.AbsolutePath);
                // #205: переключаем на сохранённый стартовый вид мини-проекта
                // (3D Fine/ShadedWithEdges; проводам — план) без транзакции.
                MiniProjectViewActivator.Activate(uiDoc);
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
        // self-sufficient (unified rule for routing and shared_nested
        // links). Release path: delete the referencing parent versions
        // (properties dialog) or the parents themselves.
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

