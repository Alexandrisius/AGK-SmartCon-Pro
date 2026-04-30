using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.ViewModels;

namespace SmartCon.FamilyManager.Services;

public sealed class FamilyManagerViewModelFactory : IFamilyManagerViewModelFactory
{
    private readonly IWritableFamilyCatalogProvider _writableProvider;
    private readonly ICategoryRepository _categoryRepository;
    private readonly IFamilyManagerDialogService _dialogService;

    public FamilyManagerViewModelFactory(
        IWritableFamilyCatalogProvider writableProvider,
        ICategoryRepository categoryRepository,
        IFamilyManagerDialogService dialogService)
    {
        _writableProvider = writableProvider;
        _categoryRepository = categoryRepository;
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
}
