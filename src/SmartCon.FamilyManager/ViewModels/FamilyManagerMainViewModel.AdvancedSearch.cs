using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// Расширенный поиск (#87): категория-охват + условия по атрибутам («И»),
/// комбинируется с обычным поиском по имени тоже по «И». Диалог собирает
/// <see cref="AdvancedSearchFilter"/>, а дерево фильтруется на уровне SQL
/// (<c>LocalCatalogQueryBuilder</c>) — тот же путь, что и текстовый поиск.
/// </summary>
public sealed partial class FamilyManagerMainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAdvancedFilterActive))]
    [NotifyPropertyChangedFor(nameof(AdvancedFilterCount))]
    [NotifyPropertyChangedFor(nameof(HasAdvancedFilterCount))]
    [NotifyPropertyChangedFor(nameof(AdvancedFilterTooltip))]
    [NotifyPropertyChangedFor(nameof(IsAnyFilterActive))]
    [NotifyPropertyChangedFor(nameof(ItemCountDisplay))]
    [NotifyPropertyChangedFor(nameof(NoResultsText))]
    private AdvancedSearchFilter? _advancedSearchFilter;

    public bool IsAdvancedFilterActive => AdvancedSearchFilter is not null;

    /// <summary>#87: обычный поиск ИЛИ расширенный фильтр активны.</summary>
    public bool IsAnyFilterActive =>
        !string.IsNullOrWhiteSpace(SearchText) || AdvancedSearchFilter is not null;

    /// <summary>Семейств, показанных в дереве с учётом поиска/фильтра (#87).</summary>
    [ObservableProperty]
    private int _visibleItemCount;

    /// <summary>Счётчик внизу панели: при фильтре «X / Y», иначе общий Y.</summary>
    public string ItemCountDisplay => IsAnyFilterActive
        ? $"{VisibleItemCount} / {TotalItemCount}"
        : TotalItemCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string ItemCountTooltip => string.Format(
        LanguageManager.GetString(StringLocalization.Keys.FM_ItemCountTooltipFormat)
            ?? "Показано семейств: {0} из {1} в каталоге",
        VisibleItemCount,
        TotalItemCount);

    /// <summary>#87: пустое дерево — показать локализованный placeholder
    /// вместо молчаливой пустоты (поиск/фильтр ничего не нашли).</summary>
    public bool ShowNoResultsPlaceholder => TreeNodes.Count == 0;

    public string NoResultsText => IsAnyFilterActive
        ? LanguageManager.GetString(StringLocalization.Keys.FM_NoResultsPlaceholder)
            ?? "Ничего не найдено — измените запрос или сбросьте фильтр"
        : LanguageManager.GetString(StringLocalization.Keys.FM_EmptyCatalogPlaceholder)
            ?? "В каталоге нет семейств";

    public int AdvancedFilterCount => AdvancedSearchFilter?.Conditions.Count ?? 0;

    public bool HasAdvancedFilterCount => AdvancedFilterCount > 0;

    public string AdvancedFilterTooltip =>
        AdvancedSearchFilter is null
            ? LanguageManager.GetString(StringLocalization.Keys.FM_AdvSearch_OpenTooltip)
                ?? "Расширенный поиск по атрибутам"
            : AdvancedFilterCount == 0
                ? LanguageManager.GetString(StringLocalization.Keys.FM_AdvSearch_CategoryOnlyTooltip)
                    ?? "Фильтр по категории активен — нажмите, чтобы изменить или сбросить"
                : string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_AdvSearch_ActiveTooltipFormat)
                        ?? "Активных условий: {0} — нажмите, чтобы изменить или сбросить",
                    AdvancedFilterCount);

    [RelayCommand]
    private async Task OpenAdvancedSearchAsync()
    {
        using var _scope = SmartConLogger.BeginScope("FMVM",
            ("Method", nameof(OpenAdvancedSearchAsync)),
            ("ActiveConditions", AdvancedFilterCount));

        try
        {
            var vm = _viewModelFactory.CreateAdvancedSearchViewModel();
            await vm.InitializeAsync(AdvancedSearchFilter);
            var result = _dialogService.ShowAdvancedSearch(vm);
            if (result != true)
            {
                SmartConLogger.Debug("AdvancedSearch: dialog cancelled, tree untouched");
                return;
            }

            ApplyAdvancedFilter(vm.BuildFilter());
        }
        catch (Exception ex)
        {
            SmartConLogger.Error(
                $"AdvancedSearch: open failed: {ex.Message} [Action: повторите открытие расширенного поиска; при повторе пришлите лог]");
        }
    }

    private void ApplyAdvancedFilter(AdvancedSearchFilter filter)
    {
        AdvancedSearchFilter = filter.IsEmpty ? null : filter;

        // Тот же контракт сохранения/восстановления раскрытости категорий,
        // что и у текстового поиска (OnSearchTextChanged): вход в
        // «фильтрующий» режим запоминает текущее раскрытие, LoadTreeAsync
        // при выходе восстановит его (CollapseAll + Restore*).
        var isActive = IsAdvancedFilterActive || !string.IsNullOrWhiteSpace(SearchText);
        if (isActive && !_lastSearchActive)
        {
            _savedExpandedCategoryIds.Clear();
            _savedExpandedFamilyIds.Clear();
            CollectExpandedIds(TreeNodes, _savedExpandedCategoryIds, _savedExpandedFamilyIds);
        }
        _lastSearchActive = isActive;

        SmartConLogger.Info(
            $"FMTree.AdvancedFilterApplied: categoryId='{AdvancedSearchFilter?.CategoryId}' " +
            $"conditions={AdvancedFilterCount} combinedWithSearch={!string.IsNullOrWhiteSpace(SearchText)}");

        _ = ReloadTreeAfterFilterChangeAsync();
    }

    private async Task ReloadTreeAfterFilterChangeAsync()
    {
        try
        {
            var newCts = new CancellationTokenSource();
            var oldCts = Interlocked.Exchange(ref _searchCts, newCts);
            oldCts?.Cancel();
            oldCts?.Dispose();
            await LoadTreeAsync(newCts.Token);
        }
        catch (OperationCanceledException)
        {
            SmartConLogger.Debug("FMTree.AdvancedFilterReload: cancelled (newer reload took over)");
        }
    }

    /// <summary>
    /// Снимает расширенный фильтр без перезагрузки дерева — вызывается при
    /// смене активной БД (issue #87: условия привязаны к каталогу, после
    /// переключения сбрасываются), дерево перезагружает сам переключатель.
    /// </summary>
    internal void ResetAdvancedFilter()
    {
        if (AdvancedSearchFilter is null)
        {
            return;
        }

        AdvancedSearchFilter = null;
        _lastSearchActive = !string.IsNullOrWhiteSpace(SearchText);
        SmartConLogger.Info("FMTree.AdvancedFilterReset: active database changed");
    }
}
