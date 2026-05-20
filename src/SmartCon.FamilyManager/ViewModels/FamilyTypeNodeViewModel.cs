namespace SmartCon.FamilyManager.ViewModels;

public sealed class FamilyTypeNodeViewModel : CatalogTreeNodeViewModel
{
    public override bool IsCategory => false;
    public override bool IsType => true;

    public string CatalogItemId { get; }
    public string TypeName { get; }
    public string? UniqueId { get; }

    public FamilyTypeNodeViewModel(string catalogItemId, string typeName, string? uniqueId = null)
    {
        CatalogItemId = catalogItemId;
        TypeName = typeName;
        UniqueId = uniqueId;
        DisplayName = typeName;
    }
}
