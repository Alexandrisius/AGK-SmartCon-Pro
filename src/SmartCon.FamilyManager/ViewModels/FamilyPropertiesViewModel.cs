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
    private readonly IFamilyGeometryPipeline _geometryPipeline;
    private readonly IFamilyFileResolver _fileResolver;
    private readonly IAvatarCropService _avatarCropService;
    private readonly IDatabaseUpdateStateService _updateState;
    private readonly IFamilyFactRepository _factRepository;
    private readonly ICategoryChangeGateService _categoryChangeGate;

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
    [ObservableProperty] private string? _revitCategory;
    [ObservableProperty] private ObservableCollection<FamilyFactDisplayRow> _factRows = [];
    [ObservableProperty] private bool _hasFactRows;

    public string RevitCategoryDisplay =>
        string.IsNullOrWhiteSpace(RevitCategory) ? "—" : RevitCategory!;

    partial void OnVersionLabelChanged(string? value)
    {
        // A new version/family was selected: the next 3D preview load should
        // fit the camera to the new scene rather than keep the old camera.
        _isFirst3DLoad = true;
        OnVersionLabelChangedForAssets(value);
    }
    [ObservableProperty] private string? _createdAtText;
    [ObservableProperty] private string? _updatedAtText;

    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private bool _isBusy;

    /// <summary>
    /// Index of the Routing tab in the properties TabControl. Stable: the
    /// tab order is fixed (General, Content, Attributes, 3D, Versions,
    /// Routing) and the Routing tab is the ONLY visibility-gated one — and
    /// a deep-link targets it exactly when it is visible.
    /// </summary>
    public const int RoutingTabIndex = 5;

    /// <summary>
    /// #133 deep-link (routing-phantom badge): open the properties directly
    /// on the Routing tab, focused at the type whose rule holds a dead part
    /// reference (<see cref="RoutingTypeItem.KeyOf"/> format). Set BEFORE
    /// <c>InitializeCommand</c> is executed.
    /// </summary>
    public bool FocusRoutingTab { get; set; }

    /// <summary>#133 deep-link: RoutingTypeItem.KeyOf of the type to focus (null = first).</summary>
    public string? FocusRoutingTypeKey { get; set; }

    [ObservableProperty] private System.Windows.Media.Imaging.BitmapImage? _avatarImage;
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
    [ObservableProperty] private ObservableCollection<AttributeRow> _filteredAttributes = [];
    [ObservableProperty] private ObservableCollection<AttributeGroupRow> _attributeGroups = [];
    [ObservableProperty] private string? _selectedAttributeGroup;
    [ObservableProperty] private bool _hasFilteredAttributes;
    [ObservableProperty] private string _attributesStatusMessage = string.Empty;
    [ObservableProperty] private bool _hasAttributeData;
    [ObservableProperty] private bool _hasNoCategory;
    [ObservableProperty] private bool _hasNoBindings;
    [ObservableProperty] private bool _hasNotImported;
    [ObservableProperty] private string _importRunInfo = string.Empty;
    [ObservableProperty] private int _attributesFoundCount;
    [ObservableProperty] private int _attributesMissingCount;
    [ObservableProperty] private bool _hasTypes;
    [ObservableProperty] private bool _showTypeSelector;
    [ObservableProperty] private bool _isReadOnly;

    partial void OnSelectedAttributeGroupChanged(string? value)
    {
        RebuildFilteredAttributes();
    }

    partial void OnIsReadOnlyChanged(bool value)
    {
        OkCommand.NotifyCanExecuteChanged();
        PickCategoryCommand.NotifyCanExecuteChanged();
        ChangeAvatarCommand.NotifyCanExecuteChanged();
        RemoveAvatarCommand.NotifyCanExecuteChanged();
        AddAssetCommand.NotifyCanExecuteChanged();
        AddFileUnifiedCommand.NotifyCanExecuteChanged();
        DeleteAssetCommand.NotifyCanExecuteChanged();
        SetAsPrimaryCommand.NotifyCanExecuteChanged();
        ToggleAssetVersionBindingCommand.NotifyCanExecuteChanged();
        MakeActiveCommand.NotifyCanExecuteChanged();
        DeleteVersionCommand.NotifyCanExecuteChanged();
    }

    private IReadOnlyList<EffectiveCategoryAttribute> _effectiveAttributes = [];
    private IReadOnlyList<ExtractedAttributeValue> _allValues = [];
    private List<AttributeRow> _allAttributeRows = [];

    /// <summary>
    /// Raised after the family avatar was re-cropped or removed (ADR-047 rev 2) so
    /// long-lived consumers (the catalog tree tooltip) can invalidate their cache
    /// immediately instead of waiting for the next tree reload.
    /// </summary>
    public event Action? AvatarChanged;

    // Original values for dirty tracking (primary tab only)
    private string _originalName;
    private readonly string? _originalDescription;
    private readonly string? _originalCategoryId;
    private readonly List<string> _originalTags;
    private readonly ContentStatus _originalContentStatus;

    public bool HasUnsavedChanges => !IsReadOnly && (
        Name != _originalName
        || Description != _originalDescription
        || CategoryId != _originalCategoryId
        || !Tags.SequenceEqual(_originalTags)
        || ContentStatus != _originalContentStatus
        || HasRoutingChanges);

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
        string? revitCategory,
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
        IFamilyStorageRenameService renameService,
        IFamilyGeometryPipeline geometryPipeline,
        IFamilyFileResolver fileResolver,
        IAvatarCropService avatarCropService,
        IDatabaseUpdateStateService updateState,
        IFamilyFactRepository factRepository,
        ICategoryChangeGateService categoryChangeGate,
        string? familySource = null,
        int? revitCategoryId = null,
        IRoutingEditorService? routingEditorService = null)
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
        _geometryPipeline = geometryPipeline;
        _fileResolver = fileResolver;
        _avatarCropService = avatarCropService;
        _updateState = updateState;
        _factRepository = factRepository;
        _categoryChangeGate = categoryChangeGate;

        // ADR-072 Phase 3: the routing tab exists only for system MEPCurve
        // items (decided before Initialize so the tab never flashes).
        _routingEditorService = routingEditorService;
        InitializeRoutingTab(familySource, revitCategoryId);

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
        RevitCategory = revitCategory;

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
            await LoadFactsAsync(ct);
            await LoadRoutingAsync(ct);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Loads the category-driven fact rows of the header (ADR-055): the
    /// item's Revit category ordinal selects the rules from
    /// <see cref="FamilyFactRuleSet"/>; each rule with a non-empty stored
    /// fact becomes one "Label: Value" row. Evaluated-but-absent facts
    /// (empty <see cref="FamilyFact.ValueKey"/> sentinel) and pre-V22
    /// items (null category id) hide the block entirely.
    /// </summary>
    internal async Task LoadFactsAsync(CancellationToken ct)
    {
        try
        {
            var data = await _factRepository.GetForItemAsync(_catalogItemId, ct).ConfigureAwait(true);

            FactRows.Clear();
            if (data.RevitCategoryId is int categoryId)
            {
                foreach (var rule in FamilyFactRuleSet.GetRulesForCategory(categoryId))
                {
                    var fact = data.Facts.FirstOrDefault(f =>
                        string.Equals(f.FactKey, rule.FactKey, StringComparison.Ordinal));
                    if (fact is null || fact.ValueKey.Length == 0)
                        continue;

                    var label = LanguageManager.GetString(rule.LabelKey) ?? rule.FactKey;
                    var value = rule.FactKey switch
                    {
                        FamilyFactRuleSet.PartTypeFactKey =>
                            PartTypeLabelMap.TryGetLabel(fact.ValueKey) ?? fact.ValueDisplay,
                        // Connector shapes localize at display time too —
                        // the stored «Round+Rectangular» fallback must never
                        // reach the UI (owner stress test 2026-09-01). A
                        // genuinely connectorless family (evaluated mask 0)
                        // reads «Нет коннекторов» instead of an empty value.
                        FamilyFactRuleSet.ConnectorShapeFactKey =>
                            ConnectorShapeLabelMap.TryGetLabel(fact.ValueKey)
                            ?? (ConnectorShapeLabelMap.IsZeroMask(fact.ValueKey)
                                ? ConnectorShapeLabelMap.NoConnectorsLabel
                                : fact.ValueDisplay),
                        _ => fact.ValueDisplay,
                    };
                    FactRows.Add(new FamilyFactDisplayRow(label, value));
                }
            }
            HasFactRows = FactRows.Count > 0;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"LoadFactsAsync failed: {ex.Message} [Action: закройте и откройте properties снова; факты семейства будут скрыты]");
            FactRows = [];
            HasFactRows = false;
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

}

public sealed class FamilyTypeSelectorItem
{
    public string? TypeId { get; init; }
    public string TypeName { get; init; } = string.Empty;
    public override string ToString() => TypeName;
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

