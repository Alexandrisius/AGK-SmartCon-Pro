using System.ComponentModel;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// Navigation item for the Attributes tab sidebar: represents an attribute
/// group (or the special "All" / "No group" entries) with a display name
/// and the count of attributes belonging to that group.
/// </summary>
public sealed class AttributeGroupRow : INotifyPropertyChanged
{
    public AttributeGroupRow(string groupName, string displayName, int count)
    {
        GroupName = groupName;
        DisplayName = displayName;
        Count = count;
    }

    /// <summary>
    /// The raw group key. "__all__" means all attributes, "__nogroup__" means
    /// attributes with no group, otherwise it is the actual group name.
    /// </summary>
    public string GroupName { get; }

    public string DisplayName { get; }

    private int _count;
    public int Count
    {
        get => _count;
        set
        {
            if (_count == value) return;
            _count = value;
            OnPropertyChanged(nameof(Count));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
