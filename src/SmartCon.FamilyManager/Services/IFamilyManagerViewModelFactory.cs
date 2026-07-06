using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.ViewModels;

namespace SmartCon.FamilyManager.Services;

/// <summary>
/// Factory for creating FamilyManager ViewModels with resolved dependencies.
/// </summary>
public interface IFamilyManagerViewModelFactory
{
    FamilyPropertiesViewModel CreatePropertiesViewModel(
        string catalogItemId, string name, string? description,
        string? categoryId, string? categoryPath, IReadOnlyList<string> tags,
        ContentStatus contentStatus, string? versionLabel,
        string? createdAtText, string? updatedAtText,
        bool isReadOnly = false);

    CategoryTreeEditorViewModel CreateCategoryTreeEditorViewModel();
    AttributeLibraryViewModel CreateAttributeLibraryViewModel();
    CategoryPickerViewModel CreateCategoryPickerViewModel(bool allowClear = true);
    ProfileViewModel CreateProfileViewModel();
}
