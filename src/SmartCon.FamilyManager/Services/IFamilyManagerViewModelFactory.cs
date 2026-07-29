using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.ViewModels;
using SmartCon.FamilyManager.ViewModels.ProjectBase;

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
        string? revitCategory = null,
        bool isReadOnly = false);

    CategoryTreeEditorViewModel CreateCategoryTreeEditorViewModel();
    AttributeLibraryViewModel CreateAttributeLibraryViewModel();
    SharedParameterPickerViewModel CreateSharedParameterPickerViewModel(IEnumerable<string> existingNames);
    CategoryPickerViewModel CreateCategoryPickerViewModel(bool allowClear = true);
    ProfileViewModel CreateProfileViewModel();
    ProjectBaseRulesEditorViewModel CreateProjectBaseRulesEditorViewModel(ProjectBaseBinding? existingBinding = null, string currentDocumentPath = "");
    ValidationReportViewModel CreateValidationReportViewModel(
        string familyName,
        string categoryPath,
        FamilyHealthReport? healthReport,
        FamilyValidationReport? validationReport,
        int validationRulesCount);
    ValidationRulesEditorViewModel CreateValidationRulesEditorViewModel(
        string bindingId, string attributeName, string categoryPath);
}
