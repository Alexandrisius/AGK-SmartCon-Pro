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
    public int RevitMajorVersion { get; }
    public string FamilySource { get; }

    [ObservableProperty]
    private int? _typeCount;

    public string? RevitCategory { get; }

    /// <summary>
    /// VM-owned selection state. Bound to <c>DataGridRow.IsSelected</c> in
    /// XAML so that the selection survives clicks on inline editors
    /// (ComboBox dropdown, "…" Button) — those clicks collapse
    /// <c>DataGrid.SelectedItems</c> but the row stays visually selected
    /// because <c>IsSelected</c> is driven by this property.
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;

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
        RevitMajorVersion = item.RevitMajorVersion;
        FamilySource = item.FamilySource;
        _typeCount = item.TypeCount;
        RevitCategory = item.RevitCategory;
        Status = item.Status;
        ExistingCatalogItemId = item.ExistingCatalogItemId;
        ExistingVersionLabel = item.ExistingVersionLabel;
        _action = item.Action;
        _targetCategoryId = item.TargetCategoryId;
        // Display rule for the category cell:
        //   * TargetCategoryId is set   → a real category is assigned; show its
        //                                 path (or "Без категории" placeholder
        //                                 if the user explicitly picked it in
        //                                 the picker, which sets path to that
        //                                 string but keeps the real GUID).
        //   * TargetCategoryId is empty  → no category assigned; always show
        //                                 the "Без категории" placeholder.
        // This mirrors the legacy ShowBatchImportDialogAsync behaviour: that
        // method relied on the resolved CategoryId to decide whether the
        // dialog cell should display a real category name or the placeholder.
        // The previous logic that only checked TargetCategoryName
        // incorrectly showed "Без категории" as if it were a real category
        // whenever the DB row had CategoryPath="Без категории" (which
        // happens when the user previously selected the no-category option
        // in the picker and the picker wrote the placeholder literal to
        // category_name).
        _targetCategoryPath = !string.IsNullOrWhiteSpace(item.TargetCategoryId)
            ? (string.IsNullOrWhiteSpace(item.TargetCategoryName) ? "Без категории" : item.TargetCategoryName!)
            : "Без категории";
        _availableActions = BuildAvailableActions(item.Status);
    }

    private static IReadOnlyList<FamilyBatchImportAction> BuildAvailableActions(FamilyBatchImportStatus status) => status switch
    {
        FamilyBatchImportStatus.New => [FamilyBatchImportAction.IncrementVersion, FamilyBatchImportAction.Skip],
        FamilyBatchImportStatus.Existing => [FamilyBatchImportAction.IncrementVersion, FamilyBatchImportAction.OverwriteCurrent, FamilyBatchImportAction.Skip],
        _ => [FamilyBatchImportAction.Skip]
    };

    partial void OnActionChanged(FamilyBatchImportAction value)
    {
        ActionChanged?.Invoke(this, value);
    }

    partial void OnTargetCategoryPathChanged(string value)
    {
        // Fire only on path change so we always have a consistent (Id, Path)
        // pair. Picker flow sets Id first, then Path, so this fires after
        // both values are in place.
        CategoryChanged?.Invoke(this, (TargetCategoryId, value));
    }

    partial void OnIsSelectedChanged(bool value)
    {
        SelectionChanged?.Invoke(this, value);
    }

    [RelayCommand]
    private void PickCategory()
    {
        PickCategoryRequested?.Invoke(this);
    }

    public event Func<FamilyBatchImportRow, Task>? PickCategoryRequested;
    public event Action<FamilyBatchImportRow, FamilyBatchImportAction>? ActionChanged;
    public event Action<FamilyBatchImportRow, (string? Id, string Path)>? CategoryChanged;
    public event Action<FamilyBatchImportRow, bool>? SelectionChanged;
}
