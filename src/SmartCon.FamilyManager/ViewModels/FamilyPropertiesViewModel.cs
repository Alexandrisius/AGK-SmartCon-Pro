using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Helpers;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyPropertiesViewModel : ObservableObject, IObservableRequestClose, ICloseAwareViewModel, ISaveableViewModel, IDisposable
{
    private readonly string _catalogItemId;
    private readonly IWritableFamilyCatalogProvider _writableProvider;
    private readonly IFamilyCatalogProvider _catalogProvider;
    private readonly ICategoryRepository _categoryRepository;
    private readonly IFamilyAssetService _assetService;
    private readonly IAttributePresetService _presetService;
    private readonly IFamilyManagerDialogService _dialogService;
    private readonly ICategoryAttributeBindingService _bindingService;
    private readonly IAttributeValueRepository _valueRepository;
    private readonly IFamilyDataImportRunRepository _runRepository;
    private readonly IFamilyTypeRepository _typeRepository;
    private readonly IAttributeDefinitionRepository _attributeDefRepository;
    private readonly IFamilyManagerViewModelFactory _viewModelFactory;
    private readonly IFamilyStorageRenameService _renameService;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string? _description;
    [ObservableProperty] private string? _categoryId;
    [ObservableProperty] private string? _categoryPath;
    [ObservableProperty] private ObservableCollection<string> _tags = [];
    [ObservableProperty] private string _tagInput = string.Empty;
    [ObservableProperty] private IReadOnlyList<string> _availableTags = [];
    [ObservableProperty] private string? _selectedSuggestion;
    [ObservableProperty] private ContentStatus _contentStatus;
    [ObservableProperty] private StatusOption? _selectedStatus;
    [ObservableProperty] private string? _versionLabel;

    partial void OnVersionLabelChanged(string? value)
    {
        // A new version/family was selected: the next 3D preview load should
        // fit the camera to the new scene rather than keep the old camera.
        _isFirst3DLoad = true;
    }
    [ObservableProperty] private string? _createdAtText;
    [ObservableProperty] private string? _updatedAtText;

    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _avatarImagePath;
    [ObservableProperty] private bool _hasAvatar;
    [ObservableProperty] private ObservableCollection<FamilyAsset> _imageAssets = [];
    [ObservableProperty] private ObservableCollection<FamilyAsset> _videoAssets = [];
    [ObservableProperty] private ObservableCollection<FamilyAsset> _documentAssets = [];
    [ObservableProperty] private ObservableCollection<FamilyAsset> _lookupAssets = [];
    [ObservableProperty] private ObservableCollection<FamilyAsset> _spreadsheetAssets = [];
    [ObservableProperty] private ObservableCollection<FamilyAsset> _model3DAssets = [];
    [ObservableProperty] private ObservableCollection<FamilyAsset> _otherAssets = [];
    [ObservableProperty] private ObservableCollection<AttributePresetParameter> _effectiveParameters = [];
    [ObservableProperty] private string? _presetCategoryInfo;
    [ObservableProperty] private bool _hasPresets;
    [ObservableProperty] private FamilyAsset? _selectedAsset;

    [ObservableProperty] private ObservableCollection<FamilyTypeSelectorItem> _availableTypes = [];
    [ObservableProperty] private FamilyTypeSelectorItem? _selectedType;
    [ObservableProperty] private ObservableCollection<AttributeValueRow> _attributeRows = [];
    [ObservableProperty] private string _attributesStatusMessage = string.Empty;
    [ObservableProperty] private bool _hasAttributeData;
    [ObservableProperty] private bool _hasNoCategory;
    [ObservableProperty] private bool _hasNoBindings;
    [ObservableProperty] private bool _hasNotImported;
    [ObservableProperty] private string _importRunInfo = string.Empty;
    [ObservableProperty] private int _attributesFoundCount;
    [ObservableProperty] private int _attributesMissingCount;
    [ObservableProperty] private bool _hasTypes;
    [ObservableProperty] private bool _isReadOnly;

    partial void OnIsReadOnlyChanged(bool value)
    {
        OkCommand.NotifyCanExecuteChanged();
        PickCategoryCommand.NotifyCanExecuteChanged();
        ChangeAvatarCommand.NotifyCanExecuteChanged();
        RemoveAvatarCommand.NotifyCanExecuteChanged();
        AddAssetCommand.NotifyCanExecuteChanged();
        DeleteAssetCommand.NotifyCanExecuteChanged();
        SetAsPrimaryCommand.NotifyCanExecuteChanged();
        MakeActiveCommand.NotifyCanExecuteChanged();
        DeleteVersionCommand.NotifyCanExecuteChanged();
    }

    private IReadOnlyList<EffectiveCategoryAttribute> _effectiveAttributes = [];
    private IReadOnlyList<ExtractedAttributeValue> _allValues = [];

    // Original values for dirty tracking (primary tab only)
    private readonly string _originalName;
    private readonly string? _originalDescription;
    private readonly string? _originalCategoryId;
    private readonly List<string> _originalTags;
    private readonly ContentStatus _originalContentStatus;

    public bool HasUnsavedChanges => !IsReadOnly && (
        Name != _originalName
        || Description != _originalDescription
        || CategoryId != _originalCategoryId
        || !Tags.SequenceEqual(_originalTags)
        || ContentStatus != _originalContentStatus);

    partial void OnSelectedStatusChanged(StatusOption? value)
    {
        if (value is not null)
            ContentStatus = value.Value;
    }

    partial void OnTagInputChanged(string value)
    {
        OnPropertyChanged(nameof(FilteredTagSuggestions));
        OnPropertyChanged(nameof(HasTagSuggestions));
    }

    partial void OnSelectedSuggestionChanged(string? value)
    {
        if (value is not null)
        {
            AddTag(value);
            SelectedSuggestion = null;
        }
    }

    public IReadOnlyList<string> FilteredTagSuggestions
    {
        get
        {
            if (string.IsNullOrWhiteSpace(TagInput)) return [];
#pragma warning disable CA2249 // IndexOf used for net48 compat — string.Contains(string, StringComparison) is net8+ only
            return AvailableTags
                .Where(t => !Tags.Any(existing => string.Equals(existing, t, StringComparison.OrdinalIgnoreCase)))
                .Where(t => t.IndexOf(TagInput, StringComparison.OrdinalIgnoreCase) >= 0)
                .Take(10)
                .ToList();
#pragma warning restore CA2249
        }
    }

    public bool HasTagSuggestions => !string.IsNullOrWhiteSpace(TagInput) && FilteredTagSuggestions.Count > 0;

    private void OnTagsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(FilteredTagSuggestions));
        OnPropertyChanged(nameof(HasTagSuggestions));
    }

    [RelayCommand]
    private void AddTag(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        // Split on comma so users accustomed to the old comma-separated TextBox
        // can still paste "tag1, tag2, tag3" and get three chips, not one.
        foreach (var piece in text!.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var tag = piece.Trim();
            if (tag.Length == 0) continue;
            if (!Tags.Any(existing => string.Equals(existing, tag, StringComparison.OrdinalIgnoreCase)))
                Tags.Add(tag);
        }
        TagInput = string.Empty;
    }

    [RelayCommand]
    private void RemoveTag(string? tag)
    {
        if (tag is not null)
            Tags.Remove(tag);
    }

    public IReadOnlyList<StatusOption> AvailableStatuses { get; } = new[]
    {
        new StatusOption(ContentStatus.Active, LanguageManager.GetString(StringLocalization.Keys.FM_Status_Active) ?? "Current"),
        new StatusOption(ContentStatus.Deprecated, LanguageManager.GetString(StringLocalization.Keys.FM_Status_Deprecated) ?? "Deprecated")
    };

    public event Action<bool?>? RequestClose;

    public FamilyPropertiesViewModel(
        string catalogItemId,
        string name,
        string? description,
        string? categoryId,
        string? categoryPath,
        IReadOnlyList<string> tags,
        ContentStatus contentStatus,
        string? versionLabel,
        string? createdAtText,
        string? updatedAtText,
        IWritableFamilyCatalogProvider writableProvider,
        IFamilyCatalogProvider catalogProvider,
        ICategoryRepository categoryRepository,
        IFamilyAssetService assetService,
        IAttributePresetService presetService,
        IFamilyManagerDialogService dialogService,
        ICategoryAttributeBindingService bindingService,
        IAttributeValueRepository valueRepository,
        IFamilyDataImportRunRepository runRepository,
        IFamilyTypeRepository typeRepository,
        IAttributeDefinitionRepository attributeDefRepository,
        IFamilyManagerViewModelFactory viewModelFactory,
        IFamilyStorageRenameService renameService)
    {
        SmartConLogger.Info($"FamilyPropertiesViewModel ctor: start for itemId={catalogItemId} name='{name}'");
        _catalogItemId = catalogItemId;
        _writableProvider = writableProvider;
        _catalogProvider = catalogProvider;
        _categoryRepository = categoryRepository;
        _assetService = assetService;
        _presetService = presetService;
        _dialogService = dialogService;
        _bindingService = bindingService;
        _valueRepository = valueRepository;
        _runRepository = runRepository;
        _typeRepository = typeRepository;
        _attributeDefRepository = attributeDefRepository;
        _viewModelFactory = viewModelFactory;
        _renameService = renameService;

        Name = name;
        Description = description;
        CategoryId = categoryId;
        CategoryPath = categoryPath ?? LanguageManager.GetString(StringLocalization.Keys.FM_NoCategory) ?? "No category";
        Tags = new ObservableCollection<string>(tags ?? []);
        _originalTags = Tags.ToList();
        Tags.CollectionChanged += OnTagsCollectionChanged;
        var displayStatus = contentStatus == ContentStatus.Retired ? ContentStatus.Deprecated : contentStatus;
        ContentStatus = displayStatus;
        SelectedStatus = AvailableStatuses.FirstOrDefault(s => s.Value == displayStatus) ?? AvailableStatuses[0];
        VersionLabel = versionLabel;
        CreatedAtText = createdAtText;
        UpdatedAtText = updatedAtText;

        _originalName = name;
        _originalDescription = description;
        _originalCategoryId = categoryId;
        _originalContentStatus = displayStatus;

        SmartConLogger.Info($"FamilyPropertiesViewModel ctor: done for itemId={catalogItemId}");
    }

    [RelayCommand]
    private async Task InitializeAsync(CancellationToken ct)
    {
        using var _scope = SmartConLogger.BeginScope("FMProperties",
            ("Method", "InitializeAsync"));
        IsBusy = true;
        try
        {
            await LoadAssetsAsync(ct);
            await LoadPresetsAsync(ct);
            await LoadAttributesDataAsync(ct);
            await LoadVersionsAsync(ct);
            await LoadAvailableTagsAsync(ct);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadPresetsAsync(CancellationToken ct)
    {
        try
        {
            var parameters = await _presetService.GetEffectiveParametersAsync(CategoryId, ct);
            EffectiveParameters = new ObservableCollection<AttributePresetParameter>(parameters);
            HasPresets = parameters.Count > 0;

            if (CategoryId is not null)
            {
                var categories = await _categoryRepository.GetAllAsync(ct);
                var cat = categories.FirstOrDefault(c => c.Id == CategoryId);
                PresetCategoryInfo = cat?.FullPath ?? CategoryPath;
            }
            else
            {
                PresetCategoryInfo = LanguageManager.GetString(StringLocalization.Keys.FM_NoCategory);
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"LoadPresetsAsync failed: {ex.Message} [Action: закройте и откройте properties снова; проверьте БД каталога]");
            HasPresets = false;
            EffectiveParameters = [];
        }
    }

    private async Task LoadAvailableTagsAsync(CancellationToken ct)
    {
        try
        {
            var allTags = await _catalogProvider.GetAllTagsAsync(ct).ConfigureAwait(true);
            AvailableTags = allTags ?? [];
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"LoadAvailableTagsAsync failed: {ex.Message} [Action: автодополнение тегов будет недоступно; проверьте БД каталога]");
            AvailableTags = [];
        }
    }

    private async Task LoadAttributesDataAsync(CancellationToken ct)
    {
        try
        {
            if (CategoryId is null)
            {
                HasNoCategory = true;
                return;
            }

            var effectiveAttrs = await _bindingService.GetEffectiveAttributesAsync(CategoryId, ct);
            var allDefs = await _attributeDefRepository.GetAllAsync(ct);
            var activeAttrIds = allDefs.Where(a => a.IsActive).Select(a => a.Id).ToHashSet();
            _effectiveAttributes = effectiveAttrs.Where(a => a.IsEnabled && activeAttrIds.Contains(a.AttributeId)).ToList();

            if (_effectiveAttributes.Count == 0)
            {
                HasNoBindings = true;
                return;
            }

            var run = await _runRepository.GetLatestRunForActiveVersionAsync(_catalogItemId, ct);
            if (run is null)
            {
                HasNotImported = true;
                return;
            }

            var completedText = run.CompletedAtUtc.HasValue
                ? run.CompletedAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : "—";
            ImportRunInfo = $"Импорт {completedText} • Revit {run.RevitMajorVersion} • {run.TypesCount} типов";

            var types = await _typeRepository.GetTypesForItemAsync(_catalogItemId, ct);
            AvailableTypes = new ObservableCollection<FamilyTypeSelectorItem>(
                types.Select(t => new FamilyTypeSelectorItem { TypeId = t.Id, TypeName = t.Name }));
            HasTypes = AvailableTypes.Count > 0;

            if (!HasTypes)
            {
                AvailableTypes.Add(new FamilyTypeSelectorItem { TypeId = null, TypeName = Name });
                HasTypes = true;
            }

            var allValues = await _valueRepository.GetValuesForItemAsync(_catalogItemId, run.VersionId, ct);
            _allValues = allValues;

            // Property loading diagnostic logs removed

            var firstTypeId = HasTypes ? AvailableTypes[0].TypeId : null;
            var typeValues = firstTypeId is not null
                ? allValues.Where(v => v.TypeId == firstTypeId).ToList()
                : allValues.Where(v => v.TypeId is null).ToList();
            var found = typeValues.Count(v => v.Status == AttributeValueStatus.Found);
            var missing = _effectiveAttributes.Count - found;
            if (missing < 0) missing = 0;
            AttributesFoundCount = found;
            AttributesMissingCount = missing;

            HasAttributeData = true;

            if (HasTypes)
            {
                SelectedType = AvailableTypes[0];
            }
            else
            {
                LoadAttributesWithoutType(typeValues);
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"LoadAttributesDataAsync failed: {ex.Message} [Action: закройте и откройте properties снова; проверьте БД каталога]");
            AttributesStatusMessage = ex.Message;
        }
    }

    private static string LocalizeStatus(AttributeValueStatus status) => status switch
    {
        AttributeValueStatus.Found => LanguageManager.GetString(StringLocalization.Keys.FM_AttrStatus_Found) ?? "Найдено",
        AttributeValueStatus.MissingParameter => LanguageManager.GetString(StringLocalization.Keys.FM_AttrStatus_MissingParameter) ?? "Параметр не найден",
        AttributeValueStatus.EmptyValue => LanguageManager.GetString(StringLocalization.Keys.FM_AttrStatus_EmptyValue) ?? "Пустое значение",
        AttributeValueStatus.UnsupportedStorageType => LanguageManager.GetString(StringLocalization.Keys.FM_AttrStatus_UnsupportedType) ?? "Неподдерживаемый тип",
        AttributeValueStatus.ReadError => LanguageManager.GetString(StringLocalization.Keys.FM_AttrStatus_ReadError) ?? "Ошибка чтения",
        AttributeValueStatus.NotInFamily => LanguageManager.GetString(StringLocalization.Keys.FM_AttrStatus_NotInFamily) ?? "Нет в семействе",
        _ => status.ToString()
    };

    private void LoadAttributesWithoutType(IReadOnlyList<ExtractedAttributeValue> typeValues)
    {
        var rows = new List<AttributeValueRow>();

        var extractionParamNames = _allValues
            .Where(v => v.Status != AttributeValueStatus.NotInFamily)
            .Select(v => v.ParameterName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var attr in _effectiveAttributes.OrderBy(a => a.SortOrder))
        {
            var match = typeValues.FirstOrDefault(v => v.AttributeId == attr.AttributeId)
                ?? typeValues.FirstOrDefault(v => v.ParameterName == attr.Name);

            var isNotInFamily = match is null && !extractionParamNames.Contains(attr.Name);
            var status = match?.Status ?? (isNotInFamily ? AttributeValueStatus.NotInFamily : AttributeValueStatus.MissingParameter);

            rows.Add(new AttributeValueRow
            {
                AttributeName = attr.Name,
                Value = match?.ValueText,
                Status = LocalizeStatus(status),
                StatusDetail = match?.Message,
                IsFound = match is not null && match.Status == AttributeValueStatus.Found,
                IsInherited = attr.IsInherited,
                Group = attr.Group
            });
        }

        AttributeRows = new ObservableCollection<AttributeValueRow>(rows);
    }

    partial void OnSelectedTypeChanged(FamilyTypeSelectorItem? value)
    {
        LoadTypeAttributes(value);
    }

    private void LoadTypeAttributes(FamilyTypeSelectorItem? selected)
    {
        if (selected is null || _effectiveAttributes.Count == 0)
        {
            AttributeRows = [];
            return;
        }

        var typeValues = _allValues.Where(v => v.TypeId == selected.TypeId).ToList();
        var rows = new List<AttributeValueRow>();

        var extractionParamNames = _allValues
            .Where(v => v.Status != AttributeValueStatus.NotInFamily)
            .Select(v => v.ParameterName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var attr in _effectiveAttributes.OrderBy(a => a.SortOrder))
        {
            var match = typeValues.FirstOrDefault(v => v.AttributeId == attr.AttributeId)
                ?? typeValues.FirstOrDefault(v => v.ParameterName == attr.Name);

            var isNotInFamily = match is null && !extractionParamNames.Contains(attr.Name);
            var status = match?.Status ?? (isNotInFamily ? AttributeValueStatus.NotInFamily : AttributeValueStatus.MissingParameter);

            rows.Add(new AttributeValueRow
            {
                AttributeName = attr.Name,
                Value = match?.ValueText,
                Status = LocalizeStatus(status),
                StatusDetail = match?.Message,
                IsFound = match is not null && match.Status == AttributeValueStatus.Found,
                IsInherited = attr.IsInherited,
                Group = attr.Group
            });
        }

        AttributeRows = new ObservableCollection<AttributeValueRow>(rows);
    }

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task PickCategory()
    {
        var pickerVm = _viewModelFactory.CreateCategoryPickerViewModel();
        await pickerVm.InitializeAsync();
        var result = _dialogService.ShowCategoryPicker(pickerVm);
        if (result is not null)
        {
            if (string.IsNullOrEmpty(result))
            {
                CategoryId = null;
                CategoryPath = LanguageManager.GetString(StringLocalization.Keys.FM_NoCategory) ?? "No category";
            }
            else
            {
                CategoryId = result;
                CategoryPath = pickerVm.SelectedPath;
            }
        }
    }

    public async Task SaveAsync()
    {
        try
        {
            SmartConLogger.Info($"Saving for {_catalogItemId}, new name='{Name}'");

            var tags = Tags.ToList();

            await _writableProvider.UpdateItemAsync(
                _catalogItemId,
                Name,
                Description,
                CategoryId,
                tags,
                ContentStatus);

            SmartConLogger.Info($"DB updated, renaming files...");
            await _renameService.RenameFamilyFilesAsync(_catalogItemId, Name);
            SmartConLogger.Info($"Rename completed");

            RequestClose?.Invoke(true);
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"FAILED: {ex.Message}\n{ex.StackTrace}");
            _dialogService.ShowError("Family Manager", $"Failed to save: {ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task Ok() => await SaveAsync();

    public void ConfirmClose(CloseConfirmationArgs args) =>
        this.ConfirmUnsavedChanges(
            args,
            _dialogService.ShowYesNoCancel,
            LanguageManager.GetString(StringLocalization.Keys.FM_CTE_UnsavedChangesTitle) ?? "Unsaved Changes",
            LanguageManager.GetString(StringLocalization.Keys.FM_CTE_UnsavedChangesMessage) ?? "You have unsaved changes. Save before closing?");

    [RelayCommand]
    private async Task CancelAsync()
    {
        if (HasUnsavedChanges)
        {
            var result = _dialogService.ShowYesNoCancel(
                LanguageManager.GetString(StringLocalization.Keys.FM_CTE_UnsavedChangesTitle) ?? "Unsaved Changes",
                LanguageManager.GetString(StringLocalization.Keys.FM_CTE_UnsavedChangesMessage) ?? "You have unsaved changes. Save before closing?");

            if (result == Core.Services.Interfaces.DialogResult.Yes)
            {
                await SaveAsync();
                return;
            }

            if (result == Core.Services.Interfaces.DialogResult.Cancel)
                return;
        }

        RequestClose?.Invoke(null);
    }

    private bool CanWrite() => !IsReadOnly;

    public bool CanWriteProperty => !IsReadOnly;
}

public sealed class FamilyTypeSelectorItem
{
    public string? TypeId { get; init; }
    public string TypeName { get; init; } = string.Empty;
    public override string ToString() => TypeName;
}

public sealed class AttributeValueRow
{
    public string AttributeName { get; init; } = string.Empty;
    public string? Value { get; init; }
    public string Status { get; init; } = LanguageManager.GetString(StringLocalization.Keys.FM_AttrStatus_Found) ?? "Найдено";
    public string? StatusDetail { get; init; }
    public bool IsFound { get; init; }
    public bool IsInherited { get; init; }
    public string? Group { get; init; }
}

/// <summary>
/// UI-facing status option for the Properties dialog ComboBox. Pairs a
/// <see cref="ContentStatus"/> value with a localized display name. Equality
/// is by <see cref="Value"/> only so that ComboBox selection works even if
/// the display language changes the label.
/// </summary>
public sealed class StatusOption
{
    public ContentStatus Value { get; }
    public string DisplayName { get; }
    public StatusOption(ContentStatus value, string displayName)
    {
        Value = value;
        DisplayName = displayName;
    }
    public override string ToString() => DisplayName;
    public override bool Equals(object? obj) => obj is StatusOption other && Value == other.Value;
    public override int GetHashCode() => Value.GetHashCode();
}

