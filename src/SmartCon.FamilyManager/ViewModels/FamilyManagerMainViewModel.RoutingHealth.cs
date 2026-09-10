using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// Routing-phantom health of the catalog (#133, «Очистить недоступные
/// записи»): routing rules whose "Family:Type" token references a family
/// that no longer exists (purged as missing / deleted manually). Detected
/// with a pure catalog pass after every tree load and right after a purge;
/// affected families get a badge that opens the properties directly on the
/// Routing tab at the broken type — the fix is to pick a replacement
/// fitting there.
/// </summary>
public sealed partial class FamilyManagerMainViewModel
{
    private IReadOnlyList<SmartCon.Core.Models.FamilyManager.RoutingPhantomInfo> _lastRoutingPhantoms
        = Array.Empty<SmartCon.Core.Models.FamilyManager.RoutingPhantomInfo>();

    /// <summary>
    /// Re-runs the phantom detector and repaints the badges (leaf + category
    /// roll-up). Failures are logged, not thrown — the tree must never fail
    /// because of the health pass.
    /// </summary>
    private async Task ApplyRoutingHealthToTreeAsync(CancellationToken ct = default)
    {
        try
        {
            var phantoms = await _routingEditorService
                .FindRoutingPhantomsAsync(ct)
                .ConfigureAwait(true);
            _lastRoutingPhantoms = phantoms;

            var missingByItem = phantoms
                .GroupBy(p => p.CatalogItemId, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(p => p.MissingPartFamilyName)
                        .Distinct(StringComparer.Ordinal)
                        .ToList(),
                    StringComparer.Ordinal);

            foreach (var category in TreeNodes.OfType<CategoryNodeViewModel>())
            {
                category.RoutingIssueCount = CountRoutingIssues(category, missingByItem);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Routing phantom detection failed: {ex.Message} [Action: значки проблем трассировки не обновлены — перезагрузите дерево]");
        }
    }

    private static int CountRoutingIssues(
        CategoryNodeViewModel category,
        IReadOnlyDictionary<string, List<string>> missingByItem)
    {
        var count = 0;
        foreach (var child in category.Children)
        {
            if (child is FamilyLeafNodeViewModel leaf)
            {
                if (missingByItem.TryGetValue(leaf.CatalogItemId, out var missing))
                {
                    leaf.HasRoutingIssues = true;
                    leaf.MissingRoutingFamilies = missing;
                    count++;
                }
                else
                {
                    leaf.HasRoutingIssues = false;
                    leaf.MissingRoutingFamilies = null;
                }
            }
            else if (child is CategoryNodeViewModel subCategory)
            {
                count += CountRoutingIssues(subCategory, missingByItem);
            }
        }
        return count;
    }

    /// <summary>
    /// #133: badge click — opens the family properties directly on the
    /// Routing tab, focused at the first type holding a dead part reference.
    /// </summary>
    [RelayCommand]
    private async Task OpenRoutingPropertiesAsync(object? parameter)
    {
        if (parameter is not FamilyLeafNodeViewModel leaf) return;

        using var _scope = SmartConLogger.BeginScope("DbActualize",
            ("Method", nameof(OpenRoutingPropertiesAsync)),
            ("ItemId", leaf.CatalogItemId));

        var item = await _catalogProvider.GetItemAsync(leaf.CatalogItemId).ConfigureAwait(true);
        if (item is null)
        {
            SmartConLogger.Warn(
                $"OpenRoutingProperties: item {leaf.CatalogItemId} not found [Action: обновите дерево — семейство уже удалено из каталога]");
            return;
        }

        var createdAt = item.CreatedAtUtc != default
            ? item.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : null;
        var updatedAt = item.UpdatedAtUtc != default
            ? item.UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : null;
        var focus = _lastRoutingPhantoms.FirstOrDefault(
            p => string.Equals(p.CatalogItemId, leaf.CatalogItemId, StringComparison.Ordinal));

        FamilyPropertiesViewModel vm;
        try
        {
            vm = _viewModelFactory.CreatePropertiesViewModel(
                item.Id,
                item.Name,
                item.Description,
                item.CategoryId,
                item.CategoryPath,
                item.Tags,
                item.ContentStatus,
                item.CurrentVersionLabel,
                createdAt,
                updatedAt,
                item.RevitCategory,
                isReadOnly: !CanEdit,
                familySource: item.FamilySource,
                revitCategoryId: item.RevitCategoryId,
                focusRoutingTypeKey: focus is null
                    ? null
                    : RoutingTypeItem.KeyOf(focus.TypeName, focus.FamilyKey));
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"OpenRoutingProperties: VM construction failed: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        try
        {
            vm.InitializeCommand.Execute(null);
            var result = _dialogService.ShowProperties(vm);

            // Routing edits drift the loaded project types right away and
            // may heal the very phantom the badge points at — refresh both.
            if (vm.RoutingLinksChanged || vm.ActiveVersionChanged)
            {
                await RunPostImportStaleCheckAsync(new[]
                {
                    new SmartCon.Core.Models.FamilyManager.ImportedCatalogItem(item.Id, item.Name, item.FamilySource),
                }).ConfigureAwait(true);
            }
            if (result != true && !vm.ActiveVersionChanged && !vm.VersionsChanged && !vm.RoutingLinksChanged) return;

            await LoadTreeAsync().ConfigureAwait(true);
            ExpandAndSelectItem(item.Id);
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"OpenRoutingProperties: ShowProperties failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
