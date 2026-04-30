namespace SmartCon.FamilyManager.ViewModels;

public sealed class FamilyTypeNodeViewModel : CatalogTreeNodeViewModel
{
    public override bool IsCategory => false;
    public override bool IsType => true;

    public string CatalogItemId { get; }
    public string TypeName { get; }

    public FamilyTypeNodeViewModel(string catalogItemId, string typeName)
    {
        CatalogItemId = catalogItemId;
        TypeName = typeName;
        DisplayName = typeName;
    }
}
