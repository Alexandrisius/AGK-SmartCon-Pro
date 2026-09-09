using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Models.Metadata;
using SmartCon.FamilyManager.Services;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class CategoryTreeEditorViewModel
{
    private async Task LoadAttributesForCategoryAsync(CategoryNodeViewModel? categoryNode)
    {
        if (categoryNode is null)
        {
            HasSelectedCategory = false;
            AttributeItems = [];
            AvailableGroups = [];
            _allAttributeItems = [];
            return;
        }

        try
        {
            var categoryId = categoryNode.CategoryId;
            var allDefs = await _attributeDefRepository.GetAllAsync();
            var categories = await _categoryRepository.GetAllAsync();
            var categoryNameById = categories.ToDictionary(c => c.Id, c => c.Name);

            // Full-immediate editor: everything reads straight from the DB.
            var effectiveAttrs = await _bindingService.GetEffectiveAttributesAsync(categoryId);
            var directBindings = await _bindingService.GetDirectBindingsAsync(categoryId);

            var effectiveByAttrId = effectiveAttrs.ToDictionary(e => e.AttributeId);
            var bindingByAttrId = directBindings.ToDictionary(b => b.AttributeId);

            var items = new List<AttributeListItemViewModel>();
            foreach (var def in allDefs.Where(d => d.IsActive))
            {
                effectiveByAttrId.TryGetValue(def.Id, out var effective);
                bindingByAttrId.TryGetValue(def.Id, out var binding);

                var sourceName = effective?.SourceCategoryId is not null
                    && categoryNameById.TryGetValue(effective.SourceCategoryId, out var catName)
                    ? catName : null;

                items.Add(new AttributeListItemViewModel
                {
                    AttributeId = def.Id,
                    Name = def.Name,
                    Group = def.Group,
                    IsBound = effective is not null,
                    IsInherited = effective?.IsInherited ?? false,
                    SourceCategoryName = sourceName,
                    // Direct binding first; for inherited attributes the
                    // effective row carries the PARENT's binding id —
                    // rules edit targets that parent binding.
                    BindingId = binding?.Id ?? effective?.BindingId,
                    IsEnabled = effective?.IsEnabled ?? true,
                    Parent = this
                });
            }

            _allAttributeItems = items;
            await LoadRuleCountsAsync(items);

            var groups = allDefs
                .Where(d => d.IsActive && d.Group is not null)
                .Select(d => d.Group!)
                .Distinct()
                .OrderBy(g => g)
                .ToList();

            var allGroupsLabel = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_AllGroups) ?? "Все атрибуты";
            var allGroups = new List<string> { allGroupsLabel };
            allGroups.AddRange(groups);

            AvailableGroups = new ObservableCollection<string>(allGroups);
            SelectedGroupFilter = allGroupsLabel;
            AttributeFilterText = string.Empty;
            ApplyAttributeFilter();
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"LoadAttributesForCategoryAsync failed: {ex.Message} [Action: закройте и откройте editor; проверьте БД каталога]");
        }
    }

    /// <summary>
    /// Import Validation Gate: loads per-binding rule counts for the
    /// shield badges. Failures degrade to no badges (badges are
    /// informational — the editor must still open).
    /// </summary>
    private async Task LoadRuleCountsAsync(List<AttributeListItemViewModel> items)
    {
        try
        {
            var bindingIds = items
                .Where(i => i.BindingId is not null)
                .Select(i => i.BindingId!)
                .ToList();
            if (bindingIds.Count == 0) return;

            var counts = await _ruleRepository.GetRuleCountsForBindingsAsync(bindingIds);
            foreach (var item in items)
            {
                if (item.BindingId is not null && counts.TryGetValue(item.BindingId, out var count))
                {
                    item.RuleCount = count.Total;
                    item.DisabledRuleCount = count.Disabled;
                }
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"LoadRuleCountsAsync failed: {ex.Message} [Action: значки правил не отображены; проверьте БД каталога]");
        }
    }

    /// <summary>
    /// Import Validation Gate: opens the rules editor for the attribute's
    /// binding (own or inherited-from-parent). Refreshes the badge count
    /// afterwards.
    /// </summary>
    internal async Task OpenValidationRulesEditorAsync(AttributeListItemViewModel item)
    {
        if (item.BindingId is null)
        {
            return;
        }

        try
        {
            var vm = _viewModelFactory.CreateValidationRulesEditorViewModel(
                item.BindingId, item.Name, SelectedCategoryPath);
            await vm.InitializeAsync();
            var saved = _dialogService.ShowValidationRulesEditor(vm);

            if (saved == true)
            {
                // Same semantics as LoadRuleCountsAsync: the badge counts
                // ALL rules of the binding (a disabled rule still exists).
                var rules = await _ruleRepository.GetRulesForBindingAsync(item.BindingId);
                item.RuleCount = rules.Count;
                item.DisabledRuleCount = rules.Count(r => !r.IsEnabled);
                _metadataMediator.RaiseMetadataChanged();
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"OpenValidationRulesEditorAsync failed for '{item.Name}': {ex.Message}");
        }
    }

    /// <summary>
    /// Binding toggle (full-immediate editor): the binding is created /
    /// deleted in the catalog database right away (same atomic semantics
    /// as the metadata import) — the rules shield activates at once and
    /// there is no "save and reopen to edit rules" gap.
    /// </summary>
    internal async Task HandleBindingToggleAsync(AttributeListItemViewModel item, bool shouldBeBound)
    {
        if (SelectedNode is null) return;

        // Inherited bindings belong to the PARENT category — never let an
        // unbind here delete another category's binding (with its rules
        // and every descendant's effective attribute). The checkbox is
        // disabled in XAML for inherited+bound; this is the cheap guard
        // in case that trigger is ever relaxed.
        if (item.IsInherited) return;

        // Import Validation Gate: unbinding deletes the binding's
        // validation rules (FK CASCADE). Warn before destroying them.
        if (!shouldBeBound && item.HasRules)
        {
            var title = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_UnbindRulesTitle)
                ?? "Правила валидации будут удалены";
            var message = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                LanguageManager.GetString(StringLocalization.Keys.FM_CTE_UnbindRulesMessage)
                    ?? "У атрибута \"{0}\" заданы правила валидации ({1}). При отвязке они будут удалены без возможности восстановления. Продолжить?",
                item.Name,
                item.RuleCount);
            if (!_dialogService.ShowConfirmation(title, message))
            {
                item.NotifyBoundStateChanged();
                return;
            }
        }

        var categoryId = SelectedNode.CategoryId;
        var attributeId = item.AttributeId;

        try
        {
            if (shouldBeBound)
            {
                var sortOrder = (await _bindingService.GetDirectBindingsAsync(categoryId)).Count;
                var created = await _bindingService.CreateBindingAsync(categoryId, attributeId, sortOrder);
                item.BindingId = created.Id;
                item.IsBound = true;
                item.RuleCount = 0;
                item.DisabledRuleCount = 0;
            }
            else
            {
                if (item.BindingId is not null)
                {
                    await _bindingService.DeleteBindingAsync(item.BindingId);
                }

                item.BindingId = null;
                item.IsBound = false;
                item.RuleCount = 0;
                item.DisabledRuleCount = 0;
            }

            _metadataMediator.RaiseMetadataChanged();
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Binding toggle failed for '{item.Name}' in '{SelectedNode.FullPath}': {ex.Message} " +
                "[Action: проверьте БД каталога и повторите]");
            item.NotifyBoundStateChanged();
            return;
        }

        ApplyAttributeFilter();
    }

    private void ApplyAttributeFilter()
    {
        var filtered = _allAttributeItems.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(AttributeFilterText))
        {
            var filter = AttributeFilterText.Trim().ToUpperInvariant();
            filtered = filtered.Where(x =>
                x.Name.ToUpperInvariant().Contains(filter) ||
                (x.Group is not null && x.Group.ToUpperInvariant().Contains(filter)));
        }

        var allGroupsLabel = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_AllGroups) ?? "Все атрибуты";
        if (!string.IsNullOrWhiteSpace(SelectedGroupFilter) && SelectedGroupFilter != allGroupsLabel)
        {
            filtered = filtered.Where(x => x.Group == SelectedGroupFilter);
        }

        AttributeItems = new ObservableCollection<AttributeListItemViewModel>(filtered);
    }

    [RelayCommand]
    private async Task OpenAttributeLibraryAsync()
    {
        var libraryVm = _viewModelFactory.CreateAttributeLibraryViewModel();
        libraryVm.RequestClose += _ => libraryVm.Detach();
        await libraryVm.InitializeAsync();
        _dialogService.ShowAttributeLibrary(libraryVm);

        if (SelectedNode is not null)
            await LoadAttributesForCategoryAsync(SelectedNode);
    }
}
