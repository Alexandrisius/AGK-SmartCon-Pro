using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyPropertiesViewModel : ObservableObject, IObservableRequestClose
{
    private readonly string _catalogItemId;
    private readonly IWritableFamilyCatalogProvider _writableProvider;
    private readonly ICategoryRepository _categoryRepository;
    private readonly IFamilyAssetService _assetService;
    private readonly IAttributePresetService _presetService;
    private readonly IFamilyManagerDialogService _dialogService;
    private readonly ICategoryAttributeBindingService _bindingService;
    private readonly IAttributeValueRepository _valueRepository;
    private readonly IFamilyDataImportRunRepository _runRepository;
    private readonly IFamilyTypeRepository _typeRepository;
    private readonly IAttributeDefinitionRepository _attributeDefRepository;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string? _description;
    [ObservableProperty] private string? _categoryId;
    [ObservableProperty] private string? _categoryPath;
    [ObservableProperty] private string _tagsText = string.Empty;
    [ObservableProperty] private ContentStatus _contentStatus;
    [ObservableProperty] private string? _manufacturer;
    [ObservableProperty] private string? _versionLabel;
    [ObservableProperty] private string? _fileSizeText;
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

    private IReadOnlyList<EffectiveCategoryAttribute> _effectiveAttributes = [];
    private IReadOnlyList<ExtractedAttributeValue> _allValues = [];

    public IReadOnlyList<ContentStatus> AvailableStatuses { get; } =
        Enum.GetValues(typeof(ContentStatus)).Cast<ContentStatus>().ToArray();

    public event Action<bool?>? RequestClose;

    public FamilyPropertiesViewModel(
        string catalogItemId,
        string name,
        string? description,
        string? categoryId,
        string? categoryPath,
        IReadOnlyList<string> tags,
        ContentStatus contentStatus,
        string? manufacturer,
        string? versionLabel,
        string? fileSizeText,
        string? createdAtText,
        string? updatedAtText,
        IWritableFamilyCatalogProvider writableProvider,
        ICategoryRepository categoryRepository,
        IFamilyAssetService assetService,
        IAttributePresetService presetService,
        IFamilyManagerDialogService dialogService,
        ICategoryAttributeBindingService bindingService,
        IAttributeValueRepository valueRepository,
        IFamilyDataImportRunRepository runRepository,
        IFamilyTypeRepository typeRepository,
        IAttributeDefinitionRepository attributeDefRepository)
    {
        _catalogItemId = catalogItemId;
        _writableProvider = writableProvider;
        _categoryRepository = categoryRepository;
        _assetService = assetService;
        _presetService = presetService;
        _dialogService = dialogService;
        _bindingService = bindingService;
        _valueRepository = valueRepository;
        _runRepository = runRepository;
        _typeRepository = typeRepository;
        _attributeDefRepository = attributeDefRepository;

        Name = name;
        Description = description;
        CategoryId = categoryId;
        CategoryPath = categoryPath ?? LanguageManager.GetString(StringLocalization.Keys.FM_NoCategory) ?? "No category";
        TagsText = tags is not null && tags.Count > 0 ? string.Join(", ", tags) : string.Empty;
        ContentStatus = contentStatus;
        Manufacturer = manufacturer;
        VersionLabel = versionLabel;
        FileSizeText = fileSizeText;
        CreatedAtText = createdAtText;
        UpdatedAtText = updatedAtText;
    }

    [RelayCommand]
    private async Task InitializeAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            await LoadAssetsAsync(ct);
            await LoadPresetsAsync(ct);
            await LoadAttributesDataAsync(ct);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadAssetsAsync(CancellationToken ct)
    {
        var assets = await _assetService.GetAssetsAsync(_catalogItemId, ct: ct);

        ImageAssets = new ObservableCollection<FamilyAsset>(assets.Where(a => a.AssetType == FamilyAssetType.Image));
        VideoAssets = new ObservableCollection<FamilyAsset>(assets.Where(a => a.AssetType == FamilyAssetType.Video));
        DocumentAssets = new ObservableCollection<FamilyAsset>(assets.Where(a => a.AssetType == FamilyAssetType.Document));
        LookupAssets = new ObservableCollection<FamilyAsset>(assets.Where(a => a.AssetType == FamilyAssetType.LookupTable));
        SpreadsheetAssets = new ObservableCollection<FamilyAsset>(assets.Where(a => a.AssetType == FamilyAssetType.Spreadsheet));
        Model3DAssets = new ObservableCollection<FamilyAsset>(assets.Where(a => a.AssetType == FamilyAssetType.Model3D));
        OtherAssets = new ObservableCollection<FamilyAsset>(assets.Where(a => a.AssetType == FamilyAssetType.Other));

        var primary = assets.FirstOrDefault(a => a.AssetType == FamilyAssetType.Image && a.IsPrimary);
        if (primary is null)
            primary = ImageAssets.FirstOrDefault();

        if (primary is not null)
        {
            var path = await _assetService.ResolveAssetPathAsync(primary.Id, ct);
            AvatarImagePath = path;
            HasAvatar = path is not null;
        }
        else
        {
            AvatarImagePath = null;
            HasAvatar = false;
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
            SmartConLogger.Warn($"LoadPresetsAsync failed: {ex.Message}");
            HasPresets = false;
            EffectiveParameters = [];
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

            var run = await _runRepository.GetLatestRunAsync(_catalogItemId, ct);
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

            var allValues = await _valueRepository.GetValuesForItemAsync(_catalogItemId, run.VersionId, ct);
            _allValues = allValues;

            var firstTypeId = AvailableTypes.Count > 0 ? AvailableTypes[0].TypeId : null;
            var typeValues = firstTypeId is not null
                ? allValues.Where(v => v.TypeId == firstTypeId).ToList()
                : allValues.ToList();
            var found = typeValues.Count(v => v.Status == AttributeValueStatus.Found);
            var missing = _effectiveAttributes.Count - found;
            if (missing < 0) missing = 0;
            AttributesFoundCount = found;
            AttributesMissingCount = missing;

            HasAttributeData = true;

            if (AvailableTypes.Count > 0)
                SelectedType = AvailableTypes[0];
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"LoadAttributesDataAsync failed: {ex.Message}");
            AttributesStatusMessage = ex.Message;
        }
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

        foreach (var attr in _effectiveAttributes.OrderBy(a => a.SortOrder))
        {
            var match = typeValues.FirstOrDefault(v => v.AttributeId == attr.AttributeId);
            rows.Add(new AttributeValueRow
            {
                AttributeName = attr.Name,
                Value = match?.ValueText,
                Status = match?.Status.ToString() ?? "MissingParameter",
                StatusDetail = match?.Message,
                IsFound = match is not null && match.Status == AttributeValueStatus.Found,
                IsInherited = attr.IsInherited,
                Group = attr.Group
            });
        }

        AttributeRows = new ObservableCollection<AttributeValueRow>(rows);
    }

    [RelayCommand]
    private async Task PickCategory()
    {
        var pickerVm = new CategoryPickerViewModel(_categoryRepository);
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

    [RelayCommand]
    private async Task Save()
    {
        var tags = TagsText
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        await _writableProvider.UpdateItemAsync(
            _catalogItemId,
            Name,
            Description,
            CategoryId,
            tags,
            ContentStatus,
            Manufacturer);

        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(null);

    [RelayCommand]
    private async Task ChangeAvatar()
    {
        var path = _dialogService.ShowAssetOpenFileDialog(
            LanguageManager.GetString(StringLocalization.Keys.FM_Props_SelectImage) ?? "Select image",
            FamilyAssetType.Image);
        if (path is null) return;

        IsBusy = true;
        try
        {
            var asset = await _assetService.AddAssetAsync(_catalogItemId, null, FamilyAssetType.Image, path, null);
            await _assetService.SetPrimaryAssetAsync(asset.Id);
            await LoadAssetsAsync(CancellationToken.None);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RemoveAvatar()
    {
        if (!HasAvatar || ImageAssets.Count == 0) return;

        var primary = ImageAssets.FirstOrDefault(a => a.IsPrimary) ?? ImageAssets.FirstOrDefault();
        if (primary is null) return;

        IsBusy = true;
        try
        {
            await _assetService.DeleteAssetAsync(primary.Id);
            await LoadAssetsAsync(CancellationToken.None);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task AddAsset(string assetTypeStr)
    {
        if (!Enum.TryParse<FamilyAssetType>(assetTypeStr, out var assetType)) return;

        var path = _dialogService.ShowAssetOpenFileDialog(
            LanguageManager.GetString(StringLocalization.Keys.FM_Props_AddFile) ?? "Select file",
            assetType);
        if (path is null) return;

        IsBusy = true;
        try
        {
            await _assetService.AddAssetAsync(_catalogItemId, null, assetType, path, null);
            await LoadAssetsAsync(CancellationToken.None);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteAsset(FamilyAsset? asset)
    {
        if (asset is null) return;

        IsBusy = true;
        try
        {
            await _assetService.DeleteAssetAsync(asset.Id);
            await LoadAssetsAsync(CancellationToken.None);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task OpenAsset(FamilyAsset? asset)
    {
        if (asset is null) return;

        var path = await _assetService.ResolveAssetPathAsync(asset.Id);
        if (path is null) return;

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"OpenAsset failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task SetAsPrimary(FamilyAsset? asset)
    {
        if (asset is null || asset.AssetType != FamilyAssetType.Image) return;

        IsBusy = true;
        try
        {
            await _assetService.SetPrimaryAssetAsync(asset.Id);
            await LoadAssetsAsync(CancellationToken.None);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public sealed class FamilyTypeSelectorItem
{
    public string TypeId { get; init; } = string.Empty;
    public string TypeName { get; init; } = string.Empty;
    public override string ToString() => TypeName;
}

public sealed class AttributeValueRow
{
    public string AttributeName { get; init; } = string.Empty;
    public string? Value { get; init; }
    public string Status { get; init; } = "OK";
    public string? StatusDetail { get; init; }
    public bool IsFound { get; init; }
    public bool IsInherited { get; init; }
    public string? Group { get; init; }
}
