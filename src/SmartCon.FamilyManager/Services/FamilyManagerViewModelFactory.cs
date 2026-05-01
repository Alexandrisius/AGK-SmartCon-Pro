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

    public FamilyManagerViewModelFactory(
        IWritableFamilyCatalogProvider writableProvider,
        ICategoryRepository categoryRepository,
        IFamilyAssetService assetService,
        IAttributePresetService presetService,
        IFamilyManagerDialogService dialogService)
    {
        _writableProvider = writableProvider;
        _categoryRepository = categoryRepository;
        _assetService = assetService;
        _presetService = presetService;
        _dialogService = dialogService;
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
            _writableProvider, _categoryRepository, _dialogService);
    }

    public FamilyPropertiesViewModel CreatePropertiesViewModel(
        string catalogItemId, string name, string? description,
        string? categoryId, string? categoryPath, IReadOnlyList<string> tags,
        ContentStatus contentStatus, string? manufacturer, string? versionLabel,
        string? fileSizeText, string? createdAtText, string? updatedAtText)
    {
        return new FamilyPropertiesViewModel(
            catalogItemId, name, description,
            categoryId, categoryPath, tags, contentStatus,
            manufacturer, versionLabel, fileSizeText, createdAtText, updatedAtText,
            _writableProvider, _categoryRepository, _assetService, _presetService, _dialogService);
    }
}
