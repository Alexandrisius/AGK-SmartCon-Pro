using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SmartCon.FamilyManager.ViewModels;

public abstract partial class CatalogTreeNodeViewModel : ObservableObject
{
    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private ObservableCollection<CatalogTreeNodeViewModel> _children = [];
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    public abstract bool IsCategory { get; }
    public virtual bool IsType => false;
}
