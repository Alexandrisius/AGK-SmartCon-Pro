using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.ViewModels;

namespace SmartCon.FamilyManager.Services;

public interface IFamilyManagerViewModelFactory
{
    FamilyMetadataEditViewModel CreateMetadataEditViewModel(
        string catalogItemId, string name, string? description,
        string? categoryId, string? categoryPath, IReadOnlyList<string> tags, ContentStatus contentStatus);

    FamilyPropertiesViewModel CreatePropertiesViewModel(
        string catalogItemId, string name, string? description,
        string? categoryId, string? categoryPath, IReadOnlyList<string> tags,
        ContentStatus contentStatus, string? manufacturer, string? versionLabel,
        string? fileSizeText, string? createdAtText, string? updatedAtText);
}
