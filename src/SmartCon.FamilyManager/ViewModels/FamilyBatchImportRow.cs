using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// ViewModel for a single row in the batch import dialog.
/// </summary>
public sealed partial class FamilyBatchImportRow : ObservableObject
{
    public string FilePath { get; }
    public string FileName { get; }
    public string Sha256 { get; }
    public int RevitMajorVersion { get; }
    public long FileSizeBytes { get; }
    public FamilyBatchImportStatus Status { get; }
    public string? ExistingCatalogItemId { get; }
    public string? ExistingVersionLabel { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    private FamilyBatchImportAction _action;

    [ObservableProperty]
    private string? _targetCategoryId;

    [ObservableProperty]
    private string _targetCategoryPath = string.Empty;

    public bool CanImport => Action != FamilyBatchImportAction.Skip;

    public IReadOnlyList<FamilyBatchImportAction> AvailableActions { get; }

    public FamilyBatchImportRow(FamilyBatchImportItem item)
    {
        FilePath = item.FilePath;
        FileName = item.FileName;
        Sha256 = item.Sha256;
        RevitMajorVersion = item.RevitMajorVersion;
        FileSizeBytes = item.FileSizeBytes;
        Status = item.Status;
        ExistingCatalogItemId = item.ExistingCatalogItemId;
        ExistingVersionLabel = item.ExistingVersionLabel;
        _action = item.Action;
        _targetCategoryId = item.TargetCategoryId;
        _targetCategoryPath = item.TargetCategoryName ?? item.TargetCategoryId ?? "Без категории";

        AvailableActions = Status switch
        {
            FamilyBatchImportStatus.Duplicate => [FamilyBatchImportAction.Skip],
            FamilyBatchImportStatus.New => [FamilyBatchImportAction.IncrementVersion, FamilyBatchImportAction.Skip],
            FamilyBatchImportStatus.Existing => [FamilyBatchImportAction.IncrementVersion, FamilyBatchImportAction.OverwriteCurrent, FamilyBatchImportAction.Skip],
            _ => [FamilyBatchImportAction.Skip]
        };
    }

    [RelayCommand]
    private void PickCategory()
    {
        PickCategoryRequested?.Invoke(this);
    }

    public event Action<FamilyBatchImportRow>? PickCategoryRequested;
}
