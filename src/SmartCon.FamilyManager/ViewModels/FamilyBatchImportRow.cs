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
    public string Sha256 { get; }
    public int RevitMajorVersion { get; }
    public long FileSizeBytes { get; }
    public string FamilySource { get; }
    public int TypeCount { get; }
    public string? RevitCategory { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    [NotifyPropertyChangedFor(nameof(AvailableActions))]
    private FamilyBatchImportAction _action;

    [ObservableProperty]
    private string? _targetCategoryId;

    [ObservableProperty]
    private string _targetCategoryPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableActions))]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private FamilyBatchImportStatus _status;

    [ObservableProperty]
    private string? _existingCatalogItemId;

    [ObservableProperty]
    private string? _existingVersionLabel;

    public bool CanImport => Action != FamilyBatchImportAction.Skip;

    [ObservableProperty]
    private IReadOnlyList<FamilyBatchImportAction> _availableActions;

    public FamilyBatchImportRow(FamilyBatchImportItem item)
    {
        FilePath = item.FilePath;
        FileName = item.FileName;
        Sha256 = item.Sha256;
        RevitMajorVersion = item.RevitMajorVersion;
        FileSizeBytes = item.FileSizeBytes;
        FamilySource = item.FamilySource;
        TypeCount = item.TypeCount;
        RevitCategory = item.RevitCategory;
        Status = item.Status;
        ExistingCatalogItemId = item.ExistingCatalogItemId;
        ExistingVersionLabel = item.ExistingVersionLabel;
        _action = item.Action;
        _targetCategoryId = item.TargetCategoryId;
        _targetCategoryPath = item.TargetCategoryName ?? item.TargetCategoryId ?? "Без категории";
        _availableActions = BuildAvailableActions(item.Status);
    }

    private static IReadOnlyList<FamilyBatchImportAction> BuildAvailableActions(FamilyBatchImportStatus status) => status switch
    {
        FamilyBatchImportStatus.Duplicate => [FamilyBatchImportAction.Skip],
        FamilyBatchImportStatus.New => [FamilyBatchImportAction.IncrementVersion, FamilyBatchImportAction.Skip],
        FamilyBatchImportStatus.Existing => [FamilyBatchImportAction.IncrementVersion, FamilyBatchImportAction.OverwriteCurrent, FamilyBatchImportAction.Skip],
        _ => [FamilyBatchImportAction.Skip]
    };

    public void SetStatusSilent(FamilyBatchImportStatus status, string? existingItemId, string? existingVersionLabel)
    {
        Status = status;
        ExistingCatalogItemId = existingItemId;
        ExistingVersionLabel = existingVersionLabel;
        AvailableActions = BuildAvailableActions(status);

        if (status == FamilyBatchImportStatus.Duplicate)
            Action = FamilyBatchImportAction.Skip;
    }

    partial void OnFileNameChanged(string value)
    {
        NameChanged?.Invoke(this);
    }

    [RelayCommand]
    private void PickCategory()
    {
        PickCategoryRequested?.Invoke(this);
    }

    public event Action<FamilyBatchImportRow>? PickCategoryRequested;
    public event Action<FamilyBatchImportRow>? NameChanged;
}
