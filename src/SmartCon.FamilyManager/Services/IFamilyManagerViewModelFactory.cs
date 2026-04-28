using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.ViewModels;

namespace SmartCon.FamilyManager.Services;

public interface IFamilyManagerViewModelFactory
{
    FamilyMetadataEditViewModel CreateMetadataEditViewModel(
        string catalogItemId, string name, string? description,
        string? category, IReadOnlyList<string> tags, FamilyContentStatus status);
}
