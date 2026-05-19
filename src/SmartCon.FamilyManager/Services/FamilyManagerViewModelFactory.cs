using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.ViewModels;

namespace SmartCon.FamilyManager.Services;

public sealed class FamilyManagerViewModelFactory : IFamilyManagerViewModelFactory
{
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
    private readonly IFamilyMetadataPackageService _packageService;
    private readonly IDbUserRepository _userRepo;
    private readonly IDbAccessControlService _accessControl;
    private readonly IUserIdentityService _identityService;

    public FamilyManagerViewModelFactory(
        IWritableFamilyCatalogProvider writableProvider,
        ICategoryRepository categoryRepository,
        IFamilyAssetService assetService,
        IAttributePresetService presetService,
        IFamilyManagerDialogService dialogService,
        ICategoryAttributeBindingService bindingService,
        IAttributeValueRepository valueRepository,
        IFamilyDataImportRunRepository runRepository,
        IFamilyTypeRepository typeRepository,
        IAttributeDefinitionRepository attributeDefRepository,
        IFamilyMetadataPackageService packageService,
        IDbUserRepository userRepo,
        IDbAccessControlService accessControl,
        IUserIdentityService identityService)
    {
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
        _packageService = packageService;
        _userRepo = userRepo;
        _accessControl = accessControl;
        _identityService = identityService;
    }

    public FamilyMetadataEditViewModel CreateMetadataEditViewModel(
        string catalogItemId, string name, string? description,
        string? categoryId, string? categoryPath, IReadOnlyList<string> tags, ContentStatus contentStatus)
    {
        return new FamilyMetadataEditViewModel(
            catalogItemId, name, description,
            categoryId: categoryId,
            categoryPath: categoryPath,
            tags, contentStatus,
            _writableProvider, _categoryRepository, _dialogService, this);
    }

    public FamilyPropertiesViewModel CreatePropertiesViewModel(
        string catalogItemId, string name, string? description,
        string? categoryId, string? categoryPath, IReadOnlyList<string> tags,
        ContentStatus contentStatus, string? manufacturer, string? versionLabel,
        string? fileSizeText, string? createdAtText, string? updatedAtText,
        bool isReadOnly = false)
    {
        return new FamilyPropertiesViewModel(
            catalogItemId, name, description,
            categoryId, categoryPath, tags, contentStatus,
            manufacturer, versionLabel, fileSizeText, createdAtText, updatedAtText,
            _writableProvider, _categoryRepository, _assetService, _presetService, _dialogService,
            _bindingService, _valueRepository, _runRepository, _typeRepository, _attributeDefRepository, this)
        { IsReadOnly = isReadOnly };
    }

    public CategoryTreeEditorViewModel CreateCategoryTreeEditorViewModel()
    {
        return new CategoryTreeEditorViewModel(
            _categoryRepository, _dialogService, _attributeDefRepository, _bindingService, _packageService, this);
    }

    public AttributeLibraryViewModel CreateAttributeLibraryViewModel()
    {
        return new AttributeLibraryViewModel(
            _attributeDefRepository, _bindingService, _dialogService, _categoryRepository);
    }

    public CategoryPickerViewModel CreateCategoryPickerViewModel(bool allowClear = true)
    {
        return new CategoryPickerViewModel(_categoryRepository, allowClear);
    }

    public ProfileViewModel CreateProfileViewModel()
    {
        return new ProfileViewModel(_userRepo, _accessControl, _identityService, _dialogService);
    }
}
