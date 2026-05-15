using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;
using SmartCon.UI;
using SmartCon.UI.Behaviors;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class AttributeLibraryViewModel : ObservableObject, IObservableRequestClose
{
    private readonly IAttributeDefinitionRepository _attributeDefRepository;
    private readonly ICategoryAttributeBindingService _bindingService;
    private readonly IFamilyManagerDialogService _dialogService;
    private readonly ICategoryRepository _categoryRepository;
    private readonly List<AttributeDefinitionDraft> _pendingDeletions = [];

    [ObservableProperty] private ObservableCollection<AttributeDefinitionDraft> _items = [];
    [ObservableProperty] private AttributeDefinitionDraft? _selectedItem;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public event Action<bool?>? RequestClose;

    public AttributeLibraryViewModel(
        IAttributeDefinitionRepository attributeDefRepository,
        ICategoryAttributeBindingService bindingService,
        IFamilyManagerDialogService dialogService,
        ICategoryRepository categoryRepository)
    {
        _attributeDefRepository = attributeDefRepository;
        _bindingService = bindingService;
        _dialogService = dialogService;
        _categoryRepository = categoryRepository;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            var allDefs = await _attributeDefRepository.GetAllAsync(ct);
            var bindingCounts = await _bindingService.GetBindingCountsAsync(allDefs.Select(d => d.Id), ct);

            var drafts = allDefs.Select(def =>
            {
                bindingCounts.TryGetValue(def.Id, out var count);
                return new AttributeDefinitionDraft
                {
                    OriginalId = def.Id,
                    Name = def.Name,
                    Group = def.Group,
                    IsActive = def.IsActive,
                    OriginalIsActive = def.IsActive,
                    BindingCount = count,
                    IsNew = false,
                    IsDirty = false
                };
            }).ToList();

            Items = new ObservableCollection<AttributeDefinitionDraft>(drafts);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"AttributeLibrary InitializeAsync failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void AddAttribute()
    {
        var draft = new AttributeDefinitionDraft
        {
            OriginalId = null,
            Name = string.Empty,
            Group = null,
            IsActive = true,
            OriginalIsActive = true,
            BindingCount = 0,
            IsNew = true,
            IsDirty = false
        };
        Items.Add(draft);
        SelectedItem = draft;
    }

    [RelayCommand]
    private void Delete()
    {
        if (SelectedItem is null) return;

        var draft = SelectedItem;

        var title = LanguageManager.GetString(StringLocalization.Keys.FM_AL_Delete) ?? "Delete";
        var message = draft.BindingCount > 0
            ? string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_AL_ConfirmDelete) ?? "Delete attribute \"{0}\"? It is used in {1} categories.", draft.Name, draft.BindingCount)
            : string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_AL_ConfirmDeleteEmpty) ?? "Delete attribute \"{0}\"?", draft.Name);

        if (!_dialogService.ShowConfirmation(title, message)) return;

        if (draft.IsNew)
        {
            Items.Remove(draft);
            SelectedItem = null;
        }
        else
        {
            _pendingDeletions.Add(draft);
            Items.Remove(draft);
            SelectedItem = null;
        }
        StatusMessage = string.Empty;
    }



    public bool ConfirmDeactivation(int bindingCount)
    {
        var title = LanguageManager.GetString(StringLocalization.Keys.FM_AL_DeactivateTitle) ?? "Deactivate";
        var msg = string.Format(
            LanguageManager.GetString(StringLocalization.Keys.FM_AL_DeactivateMessage) ?? "Used in {0} categories. Continue?",
            bindingCount);
        return _dialogService.ShowConfirmation(title, msg);
    }

    public bool TrySetActive(AttributeDefinitionDraft draft, bool newActive)
    {
        if (!newActive && !draft.IsNew && draft.BindingCount > 0)
        {
            if (!ConfirmDeactivation(draft.BindingCount))
                return false;
        }

        draft.IsActive = newActive;
        draft.IsDirty = true;
        return true;
    }

    [RelayCommand]
    private void ToggleActive(CheckBoxTogglePayload payload)
    {
        if (payload.Parameter is not AttributeDefinitionDraft draft) return;

        if (!TrySetActive(draft, payload.NewValue))
        {
            // Command did not execute — UI stays unchanged
            return;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            foreach (var draft in Items.ToList())
            {
                if (draft.IsNew && string.IsNullOrWhiteSpace(draft.Name))
                {
                    Items.Remove(draft);
                }
            }

            foreach (var draft in _pendingDeletions)
            {
                await _bindingService.DeleteBindingsForAttributeAsync(draft.OriginalId!);
                await _attributeDefRepository.DeleteAsync(draft.OriginalId!);
            }
            _pendingDeletions.Clear();

            foreach (var draft in Items.Where(d => d.IsNew).ToList())
            {
                if (string.IsNullOrWhiteSpace(draft.Name)) continue;

                var exists = await _attributeDefRepository.NameExistsAsync(draft.Name, null);
                if (exists)
                {
                    var errorTitle = LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Error";
                    var errorMsg = string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_CTE_AttrExists) ?? "Attribute \"{0}\" already exists.", draft.Name);
                    _dialogService.ShowWarning(errorTitle, errorMsg);
                    return;
                }

                var created = await _attributeDefRepository.CreateAsync(draft.Name, draft.Group);
                draft.OriginalId = created.Id;
                draft.IsNew = false;
                draft.IsDirty = false;
                draft.OriginalIsActive = created.IsActive;
            }

            foreach (var draft in Items.Where(d => !d.IsNew && d.IsDirty).ToList())
            {
                if (!string.IsNullOrWhiteSpace(draft.Name) && draft.Name != draft.OriginalName)
                {
                    var exists = await _attributeDefRepository.NameExistsAsync(draft.Name, draft.OriginalId);
                    if (exists)
                    {
                        var errorTitle = LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Error";
                        var errorMsg = string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_CTE_AttrExists) ?? "Attribute \"{0}\" already exists.", draft.Name);
                        _dialogService.ShowWarning(errorTitle, errorMsg);
                        return;
                    }
                }

                if (draft.OriginalId is not null)
                {
                    await _attributeDefRepository.UpdateAsync(draft.OriginalId, draft.Name, draft.Group, draft.IsActive);

                    if (draft.OriginalIsActive && !draft.IsActive)
                    {
                        await _bindingService.DeleteBindingsForAttributeAsync(draft.OriginalId);
                    }

                    draft.OriginalIsActive = draft.IsActive;
                }

                draft.IsDirty = false;
            }

            StatusMessage = string.Empty;
            RequestClose?.Invoke(true);
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_ErrorFormat) ?? "Error: {0}", ex.Message);
        }
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(false);

    public void HandleInlineRename(string itemId, string newName)
    {
        var draft = Items.FirstOrDefault(d => d.OriginalId == itemId || (d.IsNew && d == SelectedItem));
        if (draft is not null)
        {
            draft.Name = newName;
            draft.IsDirty = true;
        }
    }

    public void HandleInlineGroupChange(string itemId, string? newGroup)
    {
        var draft = Items.FirstOrDefault(d => d.OriginalId == itemId || (d.IsNew && d == SelectedItem));
        if (draft is not null)
        {
            draft.Group = newGroup;
            draft.IsDirty = true;
        }
    }

    public sealed partial class AttributeDefinitionDraft : ObservableObject
    {
        [ObservableProperty] private string? _originalId;
        [ObservableProperty] private string _name = string.Empty;
        [ObservableProperty] private string? _group;
        [ObservableProperty] private bool _isActive = true;
        [ObservableProperty] private bool _originalIsActive = true;
        [ObservableProperty] private int _bindingCount;
        [ObservableProperty] private bool _isNew;
        [ObservableProperty] private bool _isDirty;

        public string OriginalName { get; set; } = string.Empty;

        partial void OnNameChanged(string value)
        {
            IsDirty = true;
        }

        partial void OnGroupChanged(string? value)
        {
            IsDirty = true;
        }
    }
}
