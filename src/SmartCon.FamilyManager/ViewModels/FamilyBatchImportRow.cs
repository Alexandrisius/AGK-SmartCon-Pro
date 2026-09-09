using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services;

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
    /// ADR-066: parent links when this row is a dependency of another row
    /// (routing fitting of a system category in E1). <c>null</c> for
    /// top-level rows. Read-only — links are decided at Phase-1 prepare and
    /// ride through the dialog unchanged into Phase 3.
    /// </summary>
    public IReadOnlyList<FamilyDependencyLink>? DependencyLinks { get; }

    /// <summary>
    /// v2.0.0: precomputed canonical managed path the VM allocated up
    /// front in <c>MapPreparedItemsToBatchItemsAsync</c>. The staging
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
    /// Issue #249 (Phase 2): per-type content hashes computed at Prepare.
    /// Read-only — never changes during the dialog lifetime, so a plain
    /// get-only property is enough (no INPC needed). Written to
    /// <c>family_type_hashes</c> by the import transaction.
    /// </summary>
    public IReadOnlyList<FamilyTypeHashEntry>? PerTypeHashes { get; }

    /// <summary>
    /// Issue #249 (Phase 4): canonical content sections from Prepare.
    /// Read-only like <see cref="PerTypeHashes"/>. Written to
    /// <c>catalog_versions.section_hashes/section_strings</c> by the
    /// import transaction; also the INCOMING side of the "what changed"
    /// diff against the active version.
    /// </summary>
    public IReadOnlyList<ContentSectionHash>? Sections { get; }

    /// <summary>
    /// Phase 27: version label that the content hash matched (e.g. "v2").
    /// Displayed in the dialog as "Duplicate (v2)". Null when status is
    /// not Duplicate.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CrossNameDuplicateTooltip))]
    [NotifyPropertyChangedFor(nameof(IsOutdatedNested))]
    [NotifyPropertyChangedFor(nameof(OutdatedNestedTooltip))]
    [NotifyPropertyChangedFor(nameof(MarkerResolvedTooltip))]
    private string? _matchedVersionLabel;

    /// <summary>
    /// #180 (2026-08-12): <c>true</c> when <see cref="MatchedVersionLabel"/>
    /// came from the verified ES marker override, not from content-hash
    /// dedup (the embedded identity hash disagrees or has no match —
    /// expected after a nested update: a merge never propagates parameter
    /// groups, so the identity hash keeps matching the OLD version). The
    /// status column annotates the version as marker-resolved so
    /// "Duplicate (v2)" is not misread as "content-identical to v2".
    /// Display-only flag — never consumed by import logic.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MarkerResolvedTooltip))]
    private bool _isMarkerResolvedVersion;

    /// <summary>Localized explanation shown on a marker-resolved version label.</summary>
    public string? MarkerResolvedTooltip
    {
        get
        {
            if (!IsMarkerResolvedVersion)
            {
                return null;
            }
            var format = SmartCon.UI.LanguageManager.GetString(
                SmartCon.UI.StringLocalization.Keys.FM_BatchImport_MarkerResolved_Tooltip)
                ?? "Version {0} was resolved by the verification marker (content verified during update). The embedded copy's content hash matches a different version: parameter groups never propagate on merge — this is expected.";
            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                format,
                MatchedVersionLabel ?? string.Empty);
        }
    }

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

    /// <summary>ADR-072 World B (audit M11): the snapshot routing is the
    /// UNsubstituted slim mini state — the import must not seed it as
    /// item-level catalog truth.</summary>
    public bool UnsubstitutedMiniRouting { get; }

    /// <summary>
    /// Display-ready type names for the Types-column tooltip, resolved from
    /// the same Prepare payloads that feed <see cref="TypeCount"/>
    /// (<see cref="Services.SnapshotExtractionMapper.ResolveTypeNames"/>), so
    /// the tooltip always matches the number in the column. The synthetic
    /// "&lt;default&gt;" marker is shown under the current <see cref="FileName"/>
    /// — renaming the row re-resolves the substitution.
    /// Empty when Prepare failed or produced no snapshot.
    /// </summary>
    public IReadOnlyList<string> TypeNames =>
        Services.SnapshotExtractionMapper.ResolveTypeNames(
            LoadableSnapshot, SystemSnapshot, SourceTypes, FileName) ?? [];

    /// <summary><c>true</c> when <see cref="TypeNames"/> is non-empty —
    /// drives <c>ToolTipService.IsEnabled</c> on the Types cell.</summary>
    public bool HasTypeNames => TypeNames.Count > 0;

    [ObservableProperty]
    private IReadOnlyList<FamilyBatchImportAction> _availableActions;

    public FamilyBatchImportRow(FamilyBatchImportItem item)
    {
        FilePath = item.FilePath;
        FileName = item.FileName;
        RevitMajorVersion = item.RevitMajorVersion;
        FamilySource = item.FamilySource;
        Source = item.Source;
        DependencyLinks = item.DependencyLinks;
        SourceTypes = item.SourceTypes;
        LoadableSnapshot = item.LoadableSnapshot;
        SystemSnapshot = item.SystemSnapshot;
        UnsubstitutedMiniRouting = item.UnsubstitutedMiniRouting;
        HealthReport = item.HealthReport;
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
        PerTypeHashes = item.PerTypeHashes;
        Sections = item.Sections;
        _matchedVersionLabel = item.MatchedVersionLabel;
        _isMarkerResolvedVersion = item.IsMarkerResolvedVersion;
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
        _existingCategoryId = item.ExistingCategoryId;
        _existingCategoryPath = item.ExistingCategoryPath;
        _availableActions = BuildAvailableActions(item.Status);
        // Initial gate state from the Prepare health check: errors block
        // immediately; warnings surface as Warning; rule check runs later
        // (on category assignment) via the parent view-model.
        _gateStatus = HealthReport?.IsHealthy == false
            ? FamilyRowGateStatus.Failed
            : HealthReport is not null && HealthReport.Issues.Count > 0
                ? FamilyRowGateStatus.Warning
                : FamilyRowGateStatus.NotChecked;
        if (_gateStatus == FamilyRowGateStatus.Failed)
        {
            _availableActions = [FamilyBatchImportAction.Skip];
            _action = FamilyBatchImportAction.Skip;
        }

        RebuildNotices();
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

    [RelayCommand]
    private void OpenValidationReport()
    {
        OpenValidationReportRequested?.Invoke(this);
    }

    public event Action<FamilyBatchImportRow>? OpenValidationReportRequested;

    /// <summary>
    /// #210: opens the PROBLEM view for this row (warning/error notices +
    /// follow-up actions) — fired by the problem triangle.
    /// </summary>
    [RelayCommand]
    private void OpenStatusDetails()
    {
        OpenStatusDetailsRequested?.Invoke(this, false);
    }

    /// <summary>
    /// #210: opens the RELATED-ELEMENTS view for this row (info notices
    /// only — dependency parents, marker origin — NO warnings and NO
    /// actions). Fired by the paperclip and the muted info badge: these
    /// badges are about relationships, problems live behind the triangle.
    /// </summary>
    [RelayCommand]
    private void OpenInfoDetails()
    {
        OpenStatusDetailsRequested?.Invoke(this, true);
    }

    /// <summary>(row, infoOnly) — infoOnly: только info-заметки без действий.</summary>
    public event Action<FamilyBatchImportRow, bool>? OpenStatusDetailsRequested;

    /// <summary>
    /// #249 (Phase 4): shows the "what changed" diff badge — only for
    /// rows whose content differs from the active catalog version
    /// (Existing). New and Duplicate rows have no meaningful diff (New
    /// has no active version; Duplicate is content-identical).
    /// </summary>
    public bool ShowDiffBadge => Status == FamilyBatchImportStatus.Existing;

    /// <summary>
    /// #249 (Phase 4): opens the "what changed" diff against the active
    /// version (change class + changed sections + per-type lists) —
    /// fired by the diff badge.
    /// </summary>
    [RelayCommand]
    private async Task OpenDiffDetails()
    {
        var handler = OpenDiffDetailsRequested;
        if (handler is null) return;
        try
        {
            await handler(this);
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"OpenDiffDetails failed for '{FileName}': {ex.GetType().Name}: {ex.Message} [Action: закройте batch dialog и повторите, проверьте логи smartcon.log]");
        }
    }

    /// <summary>#249 (Phase 4): async diff request (DB analytics read).</summary>
    public event Func<FamilyBatchImportRow, Task>? OpenDiffDetailsRequested;

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
