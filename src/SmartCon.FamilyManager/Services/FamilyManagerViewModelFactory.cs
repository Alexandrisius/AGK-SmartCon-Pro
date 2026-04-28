using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.ViewModels;

namespace SmartCon.FamilyManager.Services;

public sealed class FamilyManagerViewModelFactory : IFamilyManagerViewModelFactory
{
    private readonly IWritableFamilyCatalogProvider _writableProvider;

    public FamilyManagerViewModelFactory(IWritableFamilyCatalogProvider writableProvider)
    {
        _writableProvider = writableProvider;
    }

    public FamilyMetadataEditViewModel CreateMetadataEditViewModel(
        string catalogItemId, string name, string? description,
        string? category, IReadOnlyList<string> tags, FamilyContentStatus status)
    {
        return new FamilyMetadataEditViewModel(
            catalogItemId, name, description, category, tags, status, _writableProvider);
    }
}
