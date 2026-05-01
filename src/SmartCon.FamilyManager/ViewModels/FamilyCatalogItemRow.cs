using CommunityToolkit.Mvvm.ComponentModel;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyCatalogItemRow : ObservableObject
{
    [ObservableProperty] private string _id = string.Empty;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string? _categoryId;
    [ObservableProperty] private string? _categoryName;
    [ObservableProperty] private string? _manufacturer;
    [ObservableProperty] private ContentStatus _contentStatus;
    [ObservableProperty] private string? _currentVersionLabel;
    [ObservableProperty] private string? _versionLabel;
    [ObservableProperty] private DateTimeOffset _updatedAtUtc;
    [ObservableProperty] private IReadOnlyList<string> _tags = [];
    [ObservableProperty] private string? _description;
    [ObservableProperty] private string _revitVersions = string.Empty;
}
