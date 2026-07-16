using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.ViewModels;
using SmartCon.FamilyManager.ViewModels.ProjectBase;

namespace SmartCon.FamilyManager.Services;

public sealed class FamilyManagerViewModelFactory : IFamilyManagerViewModelFactory
{
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
    private readonly IDbUserRepository _userRepo;
    private readonly IDbAccessControlService _accessControl;
    private readonly IUserIdentityService _identityService;
    private readonly IFamilyStorageRenameService _renameService;
    private readonly IFamilyManagerMetadataMediator _metadataMediator;
    private readonly IRevitContext _revitContext;
    private readonly IFileNameParser _fileNameParser;
    private readonly IFamilyGeometryPipeline _geometryPipeline;
    private readonly IFamilyFileResolver _fileResolver;
    private readonly IAvatarCropService _avatarCropService;

    public FamilyManagerViewModelFactory(
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
        IDbUserRepository userRepo,
        IDbAccessControlService accessControl,
        IUserIdentityService identityService,
        IFamilyStorageRenameService renameService,
        IFamilyManagerMetadataMediator metadataMediator,
        IRevitContext revitContext,
        IFileNameParser fileNameParser,
        IFamilyGeometryPipeline geometryPipeline,
        IFamilyFileResolver fileResolver,
        IAvatarCropService avatarCropService)
    {
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
        _userRepo = userRepo;
        _accessControl = accessControl;
        _identityService = identityService;
        _renameService = renameService;
        _metadataMediator = metadataMediator;
        _revitContext = revitContext;
        _fileNameParser = fileNameParser;
        _geometryPipeline = geometryPipeline;
        _fileResolver = fileResolver;
        _avatarCropService = avatarCropService;
    }

    public FamilyPropertiesViewModel CreatePropertiesViewModel(
        string catalogItemId, string name, string? description,
        string? categoryId, string? categoryPath, IReadOnlyList<string> tags,
        ContentStatus contentStatus, string? versionLabel,
        string? createdAtText, string? updatedAtText,
        bool isReadOnly = false)
    {
        return new FamilyPropertiesViewModel(
            catalogItemId, name, description,
            categoryId, categoryPath, tags, contentStatus,
            versionLabel, createdAtText, updatedAtText,
            _writableProvider, _catalogProvider, _categoryRepository, _assetService, _presetService, _dialogService,
            _bindingService, _valueRepository, _runRepository, _typeRepository, _attributeDefRepository, this, _renameService,
            _geometryPipeline, _fileResolver, _avatarCropService)
        { IsReadOnly = isReadOnly };
    }

    public CategoryTreeEditorViewModel CreateCategoryTreeEditorViewModel()
    {
        return new CategoryTreeEditorViewModel(
            _categoryRepository, _dialogService, _attributeDefRepository, _bindingService, _metadataMediator, this);
    }

    public AttributeLibraryViewModel CreateAttributeLibraryViewModel()
    {
        return new AttributeLibraryViewModel(
            _attributeDefRepository, _bindingService, _dialogService, _categoryRepository, _metadataMediator);
    }

    public CategoryPickerViewModel CreateCategoryPickerViewModel(bool allowClear = true)
    {
        return new CategoryPickerViewModel(_categoryRepository, allowClear);
    }

    public ProfileViewModel CreateProfileViewModel()
    {
        return new ProfileViewModel(_userRepo, _accessControl, _identityService, _dialogService);
    }

    public ProjectBaseRulesEditorViewModel CreateProjectBaseRulesEditorViewModel(ProjectBaseBinding? existingBinding = null, string currentDocumentPath = "")
    {
        return new ProjectBaseRulesEditorViewModel(
            existingBinding ?? ProjectBaseBinding.Empty,
            currentDocumentPath,
            _fileNameParser,
            _dialogService);
    }
}
