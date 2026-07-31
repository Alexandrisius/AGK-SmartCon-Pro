namespace SmartCon.FamilyManager.ViewModels;

public sealed class FamilyTypeNodeViewModel : CatalogTreeNodeViewModel
{
    public override bool IsCategory => false;
    public override bool IsType => true;

    public string CatalogItemId { get; }
    public string TypeName { get; }
    public bool IsVirtual { get; }
    public string FamilySource { get; }
    public string? UniqueId { get; }
    public bool IsUnavailable { get; }

    /// <summary>
    /// Issue #183: Revit system family of the type (from
    /// <c>family_types.family_name</c>) — the sync identity is
    /// (family, name). null for legacy rows and loadable types.
    /// </summary>
    public string? FamilyName { get; }

    public bool IsSystemType =>
        FamilySource == "system" || (UniqueId is not null && UniqueId.Length > 0);

    public FamilyTypeNodeViewModel(
        string catalogItemId,
        string typeName,
        bool isVirtual = false,
        string familySource = "loadable",
        string? uniqueId = null,
        string? displayName = null,
        bool isUnavailable = false,
        string? familyName = null)
    {
        CatalogItemId = catalogItemId;
        TypeName = typeName;
        IsVirtual = isVirtual;
        FamilySource = familySource;
        UniqueId = uniqueId;
        IsUnavailable = isUnavailable;
        FamilyName = familyName;
        DisplayName = displayName ?? typeName;
    }
}
