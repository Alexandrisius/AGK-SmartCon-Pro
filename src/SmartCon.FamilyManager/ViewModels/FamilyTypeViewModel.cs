using CommunityToolkit.Mvvm.ComponentModel;

namespace SmartCon.FamilyManager.ViewModels;

[ObservableObject]
public sealed partial class FamilyTypeViewModel
{
    private readonly CatalogCategory _category;
    private readonly FamilyType _type;

    public FamilyTypeViewModel(CatalogCategory category, FamilyType type)
    {
        _category = category;
        _type = type;
    }

    public string DisplayName => _category.Families.Count > 1
        ? $"{_type.Name} — {_type.FamilyName}"
        : _type.Name;

    public FamilyType Model => _type;
}
