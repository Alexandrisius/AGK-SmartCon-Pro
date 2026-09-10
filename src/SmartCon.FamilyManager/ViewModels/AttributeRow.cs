using System.ComponentModel;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// UI row for a single effective attribute value. Mutable so that future
/// in-place updates (e.g. after editing a value) do not force a full reload
/// of the attribute list.
/// </summary>
public sealed class AttributeRow : INotifyPropertyChanged
{
    private string _attributeName = string.Empty;
    private string? _value;
    private string _status = string.Empty;
    private string? _statusDetail;
    private bool _isFound;
    private bool _isInherited;
    private string? _group;

    public string AttributeName
    {
        get => _attributeName;
        set { if (_attributeName == value) return; _attributeName = value; OnPropertyChanged(nameof(AttributeName)); }
    }

    public string? Value
    {
        get => _value;
        set { if (_value == value) return; _value = value; OnPropertyChanged(nameof(Value)); }
    }

    public string Status
    {
        get => _status;
        set { if (_status == value) return; _status = value; OnPropertyChanged(nameof(Status)); }
    }

    public string? StatusDetail
    {
        get => _statusDetail;
        set { if (_statusDetail == value) return; _statusDetail = value; OnPropertyChanged(nameof(StatusDetail)); }
    }

    public bool IsFound
    {
        get => _isFound;
        set { if (_isFound == value) return; _isFound = value; OnPropertyChanged(nameof(IsFound)); }
    }

    public bool IsInherited
    {
        get => _isInherited;
        set { if (_isInherited == value) return; _isInherited = value; OnPropertyChanged(nameof(IsInherited)); }
    }

    public string? Group
    {
        get => _group;
        set { if (_group == value) return; _group = value; OnPropertyChanged(nameof(Group)); }
    }

    public AttributeValueStatus OriginalStatus { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
