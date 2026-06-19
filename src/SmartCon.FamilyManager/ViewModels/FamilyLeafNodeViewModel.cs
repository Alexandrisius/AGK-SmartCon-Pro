using CommunityToolkit.Mvvm.ComponentModel;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyLeafNodeViewModel : CatalogTreeNodeViewModel
{
    public override bool IsCategory => false;

    public string CatalogItemId { get; }
    public string? CategoryId { get; }
    public string? CategoryPath { get; }
    public string? Manufacturer { get; }
    public ContentStatus ContentStatus { get; }
    public string? VersionLabel { get; }
    public DateTimeOffset UpdatedAtUtc { get; }
    public IReadOnlyList<string> Tags { get; }
    public string? Description { get; }
    public string FamilySource { get; }
    [ObservableProperty]
    private bool _isStale;

    [ObservableProperty]
    private StaleReason _staleReason;

    public FamilyLeafNodeViewModel(FamilyCatalogItemRow row, bool isStale = false, StaleReason staleReason = StaleReason.None)
    {
        CatalogItemId = row.Id;
        CategoryId = row.CategoryId;
        CategoryPath = row.CategoryName;
        DisplayName = row.Name;
        Manufacturer = row.Manufacturer;
        ContentStatus = row.ContentStatus;
        VersionLabel = row.VersionLabel;
        UpdatedAtUtc = row.UpdatedAtUtc;
        Tags = row.Tags;
        Description = row.Description;
        FamilySource = row.FamilySource;
        _isStale = isStale;
        _staleReason = staleReason;
    }
}
