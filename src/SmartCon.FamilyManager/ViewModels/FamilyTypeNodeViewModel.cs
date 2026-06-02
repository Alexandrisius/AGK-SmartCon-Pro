namespace SmartCon.FamilyManager.ViewModels;

public sealed class FamilyTypeNodeViewModel : CatalogTreeNodeViewModel
{
    public override bool IsCategory => false;
    public override bool IsType => true;

    public string CatalogItemId { get; }
    public string TypeName { get; }
    public bool IsVirtual { get; }

    public FamilyTypeNodeViewModel(string catalogItemId, string typeName, bool isVirtual = false)
    {
        CatalogItemId = catalogItemId;
        TypeName = typeName;
        IsVirtual = isVirtual;
        DisplayName = typeName;
    }
}
