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
        bool isReadOnly = false,
        string? familySource = null,
        int? revitCategoryId = null);

    /// <summary>
    /// Routing part picker (ADR-072, Phase 3): family + type selection for
    /// one routing rule, filtered by the group's fitting Revit category and
    /// part_type ordinals. <paramref name="currentPartName"/> preselects the
    /// currently assigned part.
    /// </summary>
    RoutingPartPickerViewModel CreateRoutingPartPickerViewModel(
        int fittingCategoryId, IReadOnlyCollection<int> partTypeOrdinals, string? currentPartName,
        string? contextLabel = null, int preferredJunctionType = -1, int connectorShapeBits = 0,
        int requiredShapeMask = 0, bool excludeMultiShape = false);

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
    AssignmentRulesEditorViewModel CreateAssignmentRulesEditorViewModel(
        string categoryId, string categoryPath, string? copyFromCategoryId = null, string? copyFromCategoryPath = null);
}
