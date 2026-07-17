using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
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

    /// <summary>
    /// v2.0.0: source payload for post-dialog staging. <c>null</c> for
    /// UC-1/UC-2 (file already on disk). <see cref="FamilyImportSource"/>
    /// for UC-3/UC-4 (the dialog receives placeholder <c>FilePath</c>
    /// like <c>"system://..."</c> or <c>"loadable://..."</c>; this
    /// <c>Source</c> is the staged pipeline's input for creating the
    /// real managed file after the user confirms the dialog).
    ///
    /// Read-only because the user never edits it directly — only the
    /// row VM's <see cref="GetResultItems"/> re-emits it on
    /// <see cref="FamilyBatchImportItem.Source"/>.
    /// </summary>
    public FamilyImportSource? Source { get; }

    /// <summary>
    /// v2.0.0: precomputed canonical managed path the VM allocated up
    /// front in <c>BuildSystemFamilyBatchRowVirtualAsync</c> /
    /// <c>BuildLoadableFamilyBatchRowVirtualAsync</c>. The staging
    /// helper writes the staged file at this exact path (so the
    /// managed-path invariant
    /// <c>family_files.relative_path = "{dbRoot}/files/&lt;catalogItemId&gt;/&lt;versionLabel&gt;/&lt;name&gt;"</c>
    /// holds). <c>GetResultItems</c> must re-emit it on
    /// <see cref="FamilyBatchImportItem.PrecomputedManagedPath"/> so the
    /// post-dialog flow still has it — losing this value is what
    /// broke the v2.0.0 import and forced staging to fall back to
    /// <c>ComputeSystemFamilyManagedPath</c> with a fresh GUID, which
    /// then made <c>ImportFileAsync</c> look for the file at a path
    /// nothing wrote to.
    /// <para>
    /// v2.0.0 hotfix: this and the two <c>Precomputed*</c> siblings are
    /// <c>[ObservableProperty]</c>-backed (not <c>get;</c>-only) because
    /// the dialog's <c>OnRowNameChanged</c> handler has to re-derive the
    /// triple when the user renames a row — leaving the values stuck on
    /// the original name produces the
    /// <c>UNIQUE constraint failed: catalog_items.id</c> failure mode
    /// where <c>ImportFileAsync</c> tries to insert a new row with the
    /// pre-existing id of the family the row used to be named after.
    /// </para>
    /// </summary>
    [ObservableProperty]
    private string? _precomputedManagedPath;

    /// <summary>
    /// v2.0.0: precomputed catalog item id. See
    /// <see cref="PrecomputedManagedPath"/> for why this must survive
    /// the dialog round-trip.
    /// </summary>
    [ObservableProperty]
    private string? _precomputedCatalogItemId;

    /// <summary>
    /// v2.0.0: precomputed version label. See
    /// <see cref="PrecomputedManagedPath"/> for why this must survive
    /// the dialog round-trip.
    /// </summary>
    [ObservableProperty]
    private string? _precomputedVersionLabel;

    /// <summary>
    /// Phase 27: content hash (hex string) computed during Phase 1 Prepare.
    /// Survives the dialog round-trip so Phase 3 Commit can store it in
    /// the catalog. Null if hash was not computed (error or legacy).
    /// </summary>
    [ObservableProperty]
    private string? _precomputedContentHash;

    /// <summary>
    /// Phase 27: hash format version (1 for the current algorithm).
    /// Survives the dialog round-trip alongside <see cref="PrecomputedContentHash"/>.
    /// </summary>
    [ObservableProperty]
    private int? _hashFormatVersion;

    /// <summary>
    /// Phase 27: version label that the content hash matched (e.g. "v2").
    /// Displayed in the dialog as "Duplicate (v2)". Null when status is
    /// not Duplicate.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CrossNameDuplicateTooltip))]
    private string? _matchedVersionLabel;

    /// <summary>
    /// Issue #126: <c>true</c> when the content hash matched a catalog
    /// item whose name differs from this row's file name (the file was
    /// renamed). The status column renders a warning icon with
    /// <see cref="CrossNameDuplicateTooltip"/> for such rows.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CrossNameDuplicateTooltip))]
    private bool _isCrossNameDuplicate;

    /// <summary>
    /// Issue #126: display name of the catalog item whose version
    /// matched the content hash. Used by <see cref="CrossNameDuplicateTooltip"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CrossNameDuplicateTooltip))]
    private string? _matchedItemName;

    /// <summary>
    /// Issue #126: localized explanation shown as the warning icon's
    /// tooltip for cross-name duplicates. Empty when not applicable.
    /// </summary>
    public string CrossNameDuplicateTooltip
    {
        get
        {
            if (!IsCrossNameDuplicate)
                return string.Empty;
            var format = SmartCon.UI.LanguageManager.GetString(
                SmartCon.UI.StringLocalization.Keys.FM_BatchImport_CrossNameDuplicate_Tooltip)
                ?? "Content matches family \"{0}\" ({1}) although the file name differs.";
            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                format,
                MatchedItemName ?? string.Empty,
                MatchedVersionLabel ?? string.Empty);
        }
    }

    [ObservableProperty]
    private int? _typeCount;

    public string? RevitCategory { get; }

    /// <summary>
    /// Phase 27: source type descriptors for system families. Survives the
    /// dialog round-trip so <see cref="SystemFamilyImportOrchestrator"/> can
    /// persist <see cref="FamilyTypeDescriptor"/> rows without re-extracting
    /// from the staged .rvt. <c>null</c> for loadable families.
    /// </summary>
    public IReadOnlyList<FamilySourceTypeInfo>? SourceTypes { get; }

    /// <summary>
    /// Phase 27: in-memory loadable snapshot from Prepare. Survives the dialog
    /// round-trip so Commit can write types + values WITHOUT re-opening the
    /// managed .rfa. <c>null</c> for system families.
    /// </summary>
    public FamilySnapshot? LoadableSnapshot { get; }

    /// <summary>
    /// Phase 27: in-memory system snapshot from Prepare. Survives the dialog
    /// round-trip so Commit can write types + values WITHOUT re-opening the
    /// staged .rvt. <c>null</c> for loadable families.
    /// </summary>
    public SystemFamilySnapshot? SystemSnapshot { get; }

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

    /// <summary>
    /// v2.0.1: tracks whether the user has manually picked a category
    /// in the picker. <c>false</c> when the category was inherited from
    /// the dialog-build time lookup (ExistingCatalogItemId.CategoryId),
    /// <c>true</c> after the picker assigns a value. Used by the rename
    /// handler to decide whether to clobber the category when the row
    /// status flips Existing ↔ New.
    /// </summary>
    [ObservableProperty]
    private bool _targetCategoryIsManual;

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
    private FamilyBatchImportRowState _importRowState = FamilyBatchImportRowState.Pending;

    [ObservableProperty]
    private string? _importErrorMessage;

    [ObservableProperty]
    private IReadOnlyList<FamilyBatchImportAction> _availableActions;

    public FamilyBatchImportRow(FamilyBatchImportItem item)
    {
        FilePath = item.FilePath;
        FileName = item.FileName;
        RevitMajorVersion = item.RevitMajorVersion;
        FamilySource = item.FamilySource;
        Source = item.Source;
        SourceTypes = item.SourceTypes;
        LoadableSnapshot = item.LoadableSnapshot;
        SystemSnapshot = item.SystemSnapshot;
        _typeCount = item.TypeCount;
        RevitCategory = item.RevitCategory;
        Status = item.Status;
        ExistingCatalogItemId = item.ExistingCatalogItemId;
        ExistingVersionLabel = item.ExistingVersionLabel;
        // Backing-field assignment is intentional: the row is being
        // constructed, so the [ObservableProperty]-generated INPC
        // notifications would be wasted work (no listener yet).
        _precomputedCatalogItemId = item.PrecomputedCatalogItemId;
        _precomputedVersionLabel = item.PrecomputedVersionLabel;
        _precomputedManagedPath = item.PrecomputedManagedPath;
        _precomputedContentHash = item.ContentHash;
        _hashFormatVersion = item.HashFormatVersion;
        _matchedVersionLabel = item.MatchedVersionLabel;
        _isCrossNameDuplicate = item.IsCrossNameDuplicate;
        _matchedItemName = item.MatchedItemName;
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
        // ADR-041: MakeActive only for Duplicate — the incoming file's content
        // is already in the catalog as one of the existing versions. The user
        // signals "I'm importing this duplicate because I want that version to
        // become active." No file is saved — only current_version_label is
        // switched (and content_hash is synchronized on the item).
        FamilyBatchImportStatus.Duplicate => [FamilyBatchImportAction.Skip, FamilyBatchImportAction.IncrementVersion, FamilyBatchImportAction.MakeActive],
        _ => [FamilyBatchImportAction.Skip]
    };

    partial void OnActionChanged(FamilyBatchImportAction value)
    {
        ActionChanged?.Invoke(this, value);
    }

    partial void OnStatusChanged(FamilyBatchImportStatus value)
    {
        // v2.0.1 hotfix: recompute AvailableActions when Status flips so
        // OverwriteCurrent appears for Existing and disappears for New.
        // Previously the list was built once in the constructor, so a
        // rename Existing → New kept OverwriteCurrent (or vice versa).
        AvailableActions = BuildAvailableActions(value);

        // Phase 27: Duplicate defaults to Skip (no point creating a new
        // version with identical content). User can manually switch to
        // IncrementVersion if they want to force a new version.
        if (value == FamilyBatchImportStatus.Duplicate)
        {
            Action = FamilyBatchImportAction.Skip;
            return;
        }

        // Validate current Action against the new available set; if the
        // user previously selected OverwriteCurrent and the row became
        // New (or the row became Existing but Action was set during New
        // phase), reset to IncrementVersion. This keeps the combo box
        // bound to Action from ever holding an invalid value.
        if (!AvailableActions.Contains(Action))
        {
            Action = AvailableActions.Contains(FamilyBatchImportAction.IncrementVersion)
                ? FamilyBatchImportAction.IncrementVersion
                : FamilyBatchImportAction.Skip;
        }
    }

    partial void OnFileNameChanged(string value)
    {
        // v2.0.0 hotfix: notify the parent view-model so it can re-resolve
        // the catalog status (New/Existing) when the user renames the row.
        // Without this, the Status column would stay "Existing" even after
        // the user typed a unique name, leaving the dialog visually
        // inconsistent with what would actually happen on import.
        NameChanged?.Invoke(this, value);
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
    private async Task PickCategory()
    {
        var handler = PickCategoryRequested;
        if (handler is null) return;
        try
        {
            await handler(this);
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"PickCategory failed for '{FileName}': {ex.GetType().Name}: {ex.Message} [Action: закройте batch dialog и повторите, проверьте логи smartcon.log]");
        }
    }

    public event Func<FamilyBatchImportRow, Task>? PickCategoryRequested;
    public event Action<FamilyBatchImportRow, FamilyBatchImportAction>? ActionChanged;
    public event Action<FamilyBatchImportRow, (string? Id, string Path)>? CategoryChanged;
    public event Action<FamilyBatchImportRow, bool>? SelectionChanged;

    /// <summary>
    /// v2.0.0 hotfix: fires whenever the user edits
    /// <see cref="FileName"/> in the batch dialog. The parent view-model
    /// re-resolves the catalog status (New/Existing) on a debounced timer
    /// and updates the row accordingly.
    /// </summary>
    public event Action<FamilyBatchImportRow, string>? NameChanged;
}
