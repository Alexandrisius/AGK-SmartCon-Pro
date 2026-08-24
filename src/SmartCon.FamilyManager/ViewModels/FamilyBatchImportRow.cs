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
    [NotifyPropertyChangedFor(nameof(ShowCategoryMoveWarning))]
    [NotifyPropertyChangedFor(nameof(CategoryMoveWarningTooltip))]
    private string? _targetCategoryId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CategoryMoveWarningTooltip))]
    private string _targetCategoryPath = string.Empty;

    /// <summary>
    /// Issue #135: where the target category came from. Single source of
    /// truth for the lock semantics: <see cref="CategoryProvenance.Command"/>
    /// and <see cref="CategoryProvenance.Manual"/> are locked (rename never
    /// resets them); the automatic provenances are re-derived by the rename
    /// handler. Replaces the v2.0.1 <c>TargetCategoryIsManual</c> bool.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetCategoryIsManual))]
    [NotifyPropertyChangedFor(nameof(CategoryProvenanceDescription))]
    [NotifyPropertyChangedFor(nameof(ShowCategoryMoveWarning))]
    [NotifyPropertyChangedFor(nameof(CategoryMoveWarningTooltip))]
    private CategoryProvenance _categoryProvenance;

    /// <summary>
    /// <c>true</c> when the category is locked by an explicit user/command
    /// choice and the rename handler must NOT re-derive it. Derived from
    /// <see cref="CategoryProvenance"/>.
    /// </summary>
    public bool TargetCategoryIsManual =>
        CategoryProvenance is CategoryProvenance.Command or CategoryProvenance.Manual;

    /// <summary>
    /// Issue #135: real category of the existing catalog item this row
    /// resolves to — may differ from <see cref="TargetCategoryId"/> when
    /// the target is locked to a different category (move-on-import).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCategoryMoveWarning))]
    [NotifyPropertyChangedFor(nameof(CategoryMoveWarningTooltip))]
    private string? _existingCategoryId;

    /// <summary>
    /// Issue #135: human-readable path of <see cref="ExistingCategoryId"/>
    /// for the move-warning tooltip.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CategoryMoveWarningTooltip))]
    private string? _existingCategoryPath;

    /// <summary>
    /// Issue #135 (P4): incremented by the rename handler when it changes
    /// the category automatically. The view flashes the category cell on
    /// every increment so the silent auto-change becomes visible.
    /// </summary>
    [ObservableProperty]
    private int _categoryFlashToken;

    /// <summary>
    /// Issue #135 (P2): <c>true</c> when the locked target category differs
    /// from the existing item's real category — the import will MOVE the
    /// existing family between categories.
    /// </summary>
    public bool ShowCategoryMoveWarning =>
        TargetCategoryIsManual
        && !string.IsNullOrEmpty(TargetCategoryId)
        && !string.IsNullOrEmpty(ExistingCategoryId)
        && TargetCategoryId != ExistingCategoryId
        && (Status == FamilyBatchImportStatus.Existing || Status == FamilyBatchImportStatus.Duplicate);

    /// <summary>
    /// Issue #135 (P1): localized explanation of where the category came
    /// from, shown as the category cell tooltip.
    /// </summary>
    public string CategoryProvenanceDescription => CategoryProvenance switch
    {
        CategoryProvenance.Command => SmartCon.UI.LanguageManager.GetString(
            SmartCon.UI.StringLocalization.Keys.FM_CategoryProvenance_Command)
            ?? "Set by the 'Import to Category' command",
        CategoryProvenance.Manual => SmartCon.UI.LanguageManager.GetString(
            SmartCon.UI.StringLocalization.Keys.FM_CategoryProvenance_Manual)
            ?? "Picked by you",
        CategoryProvenance.AutoName => SmartCon.UI.LanguageManager.GetString(
            SmartCon.UI.StringLocalization.Keys.FM_CategoryProvenance_AutoName)
            ?? "From catalog (name match)",
        CategoryProvenance.AutoHash => SmartCon.UI.LanguageManager.GetString(
            SmartCon.UI.StringLocalization.Keys.FM_CategoryProvenance_AutoHash)
            ?? "From duplicate (content match)",
        CategoryProvenance.AutoRule => SmartCon.UI.LanguageManager.GetString(
            SmartCon.UI.StringLocalization.Keys.FM_CategoryProvenance_AutoRule)
            ?? "By auto-assignment rule",
        _ => SmartCon.UI.LanguageManager.GetString(
            SmartCon.UI.StringLocalization.Keys.FM_CategoryProvenance_None)
            ?? "No category assigned",
    };

    /// <summary>
    /// Issue #135 (P2): localized warning that the import will move the
    /// existing family between categories. Empty when not applicable.
    /// </summary>
    public string CategoryMoveWarningTooltip
    {
        get
        {
            if (!ShowCategoryMoveWarning)
                return string.Empty;
            var format = SmartCon.UI.LanguageManager.GetString(
                SmartCon.UI.StringLocalization.Keys.FM_BatchImport_CategoryMoveWarning_Tooltip)
                ?? "\"{0}\" will be moved from \"{1}\" to \"{2}\" on import.";
            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                format,
                FileName,
                ExistingCategoryPath ?? "?",
                TargetCategoryPath);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableActions))]
    [NotifyPropertyChangedFor(nameof(TypeNames))]
    [NotifyPropertyChangedFor(nameof(HasTypeNames))]
    private string _fileName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCategoryMoveWarning))]
    [NotifyPropertyChangedFor(nameof(CategoryMoveWarningTooltip))]
    [NotifyPropertyChangedFor(nameof(IsOutdatedNested))]
    [NotifyPropertyChangedFor(nameof(OutdatedNestedTooltip))]
    private FamilyBatchImportStatus _status;

    [ObservableProperty]
    private string? _existingCatalogItemId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOutdatedNested))]
    [NotifyPropertyChangedFor(nameof(OutdatedNestedTooltip))]
    private string? _existingVersionLabel;

    public bool CanImport => Action != FamilyBatchImportAction.Skip;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GateTooltip))]
    private FamilyBatchImportRowState _importRowState = FamilyBatchImportRowState.Pending;

    [ObservableProperty]
    private string? _importErrorMessage;

    /// <summary>
    /// Import Validation Gate: system health report from Phase 1 Prepare
    /// (null for system families and when the check did not run).
    /// </summary>
    public FamilyHealthReport? HealthReport { get; }

    /// <summary>
    /// Import Validation Gate: latest rule-check report for the currently
    /// assigned category (null until a category with rules is assigned).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GateTooltip))]
    private FamilyValidationReport? _validationReport;

    /// <summary>
    /// Import Validation Gate: number of enabled rules of the currently
    /// assigned category — lets the report dialog distinguish "no rules"
    /// from "rules passed".
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GateTooltip))]
    private int _validationRulesCount;

    /// <summary>
    /// Import Validation Gate: combined gate status shown in the status
    /// column while the row has not been imported yet.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGateBlocked))]
    [NotifyPropertyChangedFor(nameof(GateTooltip))]
    private FamilyRowGateStatus _gateStatus = FamilyRowGateStatus.NotChecked;

    /// <summary><c>true</c> when the gate failed (health errors or rule
    /// violations) — the row is forced to Skip and cannot be imported.</summary>
    public bool IsGateBlocked => GateStatus == FamilyRowGateStatus.Failed;

    /// <summary>
    /// ADR-066 (E1): names of THIS row's dependency children whose gate
    /// failed (they are forced to Skip — the parent will be imported without
    /// them). Computed by the parent view-model after every revalidation;
    /// <c>null</c>/empty when all dependencies passed or the row has none.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailedDependencies))]
    [NotifyPropertyChangedFor(nameof(FailedDependenciesTooltip))]
    private IReadOnlyList<string>? _failedDependencyNames;

    /// <summary>
    /// #241: display paths of the categories whose auto-assignment rules
    /// ALL matched this row (ambiguous outcome). The row stays «Без
    /// категории» and the status column shows a warning notice listing the
    /// candidates — the user picks one via the category picker. Cleared by
    /// the view-model when the rules are re-evaluated or the user assigns
    /// a category.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAssignmentConflict))]
    private IReadOnlyList<string>? _assignmentConflictCandidates;

    /// <summary><c>true</c> when 2+ auto-assignment rule sets matched and
    /// the user must pick the category.</summary>
    public bool HasAssignmentConflict => AssignmentConflictCandidates is { Count: > 0 };

    /// <summary><c>true</c> when at least one dependency child failed the gate.</summary>
    public bool HasFailedDependencies => FailedDependencyNames is { Count: > 0 };

    /// <summary>Localized tooltip listing the failed dependency children.</summary>
    public string FailedDependenciesTooltip
    {
        get
        {
            var format = SmartCon.UI.LanguageManager.GetString(
                SmartCon.UI.StringLocalization.Keys.FM_Gate_Tooltip_DependencyFailed)
                ?? "Зависимости не прошли проверку и будут пропущены: {0}. Элемент будет импортирован без них.";
            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                format,
                string.Join(", ", FailedDependencyNames ?? Array.Empty<string>()));
        }
    }

    /// <summary>
    /// ADR-066 (E1): display names of the parent rows this row is a
    /// dependency of (routing fitting of a system category). <c>null</c>
    /// for top-level rows. Computed by the parent view-model.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDependency))]
    [NotifyPropertyChangedFor(nameof(DependencyOfTooltip))]
    [NotifyPropertyChangedFor(nameof(ShowInfoBadge))]
    private IReadOnlyList<string>? _dependencyParentNames;

    /// <summary><c>true</c> when this row is a dependency of another row.</summary>
    public bool IsDependency => DependencyParentNames is { Count: > 0 };

    /// <summary>
    /// E2 (#209): this dependency row embeds an OUTDATED copy of a catalog
    /// item — the content hash matched a non-active version
    /// (<see cref="MatchedVersionLabel"/> ≠ <see cref="ExistingVersionLabel"/>).
    /// The parents' import is blocked until the user either fixes the nested
    /// family inside the parent or picks MakeActive on this row (switching
    /// the catalog back to the embedded version).
    /// </summary>
    public bool IsOutdatedNested =>
        DependencyLinks is not null
        && Status == FamilyBatchImportStatus.Duplicate
        && !string.IsNullOrEmpty(MatchedVersionLabel)
        && !string.IsNullOrEmpty(ExistingVersionLabel)
        && MatchedVersionLabel != ExistingVersionLabel;

    /// <summary>Localized explanation shown on the amber badge of an outdated-nested row.</summary>
    public string OutdatedNestedTooltip
    {
        get
        {
            var format = SmartCon.UI.LanguageManager.GetString(
                SmartCon.UI.StringLocalization.Keys.FM_BatchImport_OutdatedNested_Tooltip)
                ?? "Nested \"{0}\" embeds an outdated version (embedded {1}, active {2}). The parent's import is blocked: update the nested family inside the parent and re-import — or pick \"Make Active\" on the nested row.";
            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                format,
                FileName,
                MatchedVersionLabel ?? string.Empty,
                ExistingVersionLabel ?? string.Empty);
        }
    }

    /// <summary>
    /// E2 (#209): display lines «Фланец (зашита v1, активна v2)» of THIS
    /// row's dependency children that embed an outdated nested version —
    /// the row's import is blocked (forced Skip) until the conflict is
    /// resolved. Computed by the parent view-model; <c>null</c> = not blocked.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutdatedDependencyBlock))]
    [NotifyPropertyChangedFor(nameof(OutdatedDependencyBlockTooltip))]
    private IReadOnlyList<string>? _outdatedDependencyBlockNames;

    /// <summary><c>true</c> when the row's import is blocked by outdated nested dependencies.</summary>
    public bool HasOutdatedDependencyBlock => OutdatedDependencyBlockNames is { Count: > 0 };

    /// <summary>Localized tooltip of the red import-block badge.</summary>
    public string OutdatedDependencyBlockTooltip
    {
        get
        {
            var format = SmartCon.UI.LanguageManager.GetString(
                SmartCon.UI.StringLocalization.Keys.FM_BatchImport_OutdatedNestedBlock_Tooltip)
                ?? "Импорт заблокирован — устаревшие вложенные:\n{0}\nОбновите вложенные семейства внутри родителя и повторите импорт, или выберите «Сделать активной» на строке вложенного.";
            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                format,
                DependencyGuardText.FormatMultilineList(OutdatedDependencyBlockNames ?? Array.Empty<string>()));
        }
    }

    /// <summary>Localized tooltip naming the parent rows of this dependency.</summary>
    public string DependencyOfTooltip
    {
        get
        {
            var format = SmartCon.UI.LanguageManager.GetString(
                SmartCon.UI.StringLocalization.Keys.FM_BatchImport_DependencyOf_Tooltip)
                ?? "Зависимость элемента: {0}";
            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                format,
                string.Join(", ", DependencyParentNames ?? Array.Empty<string>()));
        }
    }

    /// <summary>Localized short summary of the gate result for the status
    /// icon tooltip.</summary>
    public string GateTooltip
    {
        get
        {
            static string? Loc(string key) => SmartCon.UI.LanguageManager.GetString(key);
            if (ImportRowState == FamilyBatchImportRowState.Success)
            {
                return Loc(SmartCon.UI.StringLocalization.Keys.FM_Gate_Tooltip_Imported)
                    ?? "Импорт выполнен — открыть отчёт о проверке";
            }

            return GateStatus switch
            {
                FamilyRowGateStatus.Failed when HealthReport?.IsHealthy == false && ValidationReport?.IsValid == false =>
                    string.Format(System.Globalization.CultureInfo.CurrentCulture,
                        Loc(SmartCon.UI.StringLocalization.Keys.FM_Gate_Tooltip_FailedBoth) ?? "System errors: {0}, rule violations: {1}",
                        HealthReport.Issues.Count(i => i.Severity == FamilyHealthIssueSeverity.Error),
                        ValidationReport.Violations.Count),
                FamilyRowGateStatus.Failed when HealthReport?.IsHealthy == false =>
                    string.Format(System.Globalization.CultureInfo.CurrentCulture,
                        Loc(SmartCon.UI.StringLocalization.Keys.FM_Gate_Tooltip_FailedHealth) ?? "System errors in the family: {0}",
                        HealthReport.Issues.Count(i => i.Severity == FamilyHealthIssueSeverity.Error)),
                FamilyRowGateStatus.Failed when ValidationReport is not null =>
                    string.Format(System.Globalization.CultureInfo.CurrentCulture,
                        Loc(SmartCon.UI.StringLocalization.Keys.FM_Gate_Tooltip_FailedRules) ?? "Rule violations: {0}",
                        ValidationReport.Violations.Count),
                FamilyRowGateStatus.Warning =>
                    string.Format(System.Globalization.CultureInfo.CurrentCulture,
                        Loc(SmartCon.UI.StringLocalization.Keys.FM_Gate_Tooltip_Warning) ?? "Warnings: {0}",
                        HealthReport?.Issues.Count ?? 0),
                FamilyRowGateStatus.Passed when ValidationRulesCount > 0 =>
                    string.Format(System.Globalization.CultureInfo.CurrentCulture,
                        Loc(SmartCon.UI.StringLocalization.Keys.FM_Gate_Tooltip_PassedRules) ?? "Passed {0} rules",
                        ValidationRulesCount),
                FamilyRowGateStatus.Passed =>
                    Loc(SmartCon.UI.StringLocalization.Keys.FM_Gate_Tooltip_Passed) ?? "Check passed",
                FamilyRowGateStatus.Checking =>
                    Loc(SmartCon.UI.StringLocalization.Keys.FM_Gate_Tooltip_Checking) ?? "Checking…",
                _ => Loc(SmartCon.UI.StringLocalization.Keys.FM_Gate_Tooltip_NotChecked) ?? "Category not assigned — rules not checked",
            };
        }
    }

    // ── Clickable status badges (#210) ─────────────────────────────────
    // The status column shows at most two clickable badges (StatusBadgeButton
    // style): the dependency paperclip (IsDependency) and one problem
    // triangle coloured by the worst notice severity. Both open the status
    // details dialog listing ALL of the row's notices with the full
    // explanations that used to be long tooltips.

    /// <summary>
    /// #210: the row's active status notices (errors first, then warnings,
    /// then info). Rebuilt by <see cref="RebuildNotices"/> whenever one of
    /// the underlying badge inputs changes.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblemNotices))]
    [NotifyPropertyChangedFor(nameof(ShowInfoBadge))]
    [NotifyPropertyChangedFor(nameof(ProblemBadgeSeverity))]
    [NotifyPropertyChangedFor(nameof(ProblemBadgeTooltip))]
    private IReadOnlyList<StatusNotice> _notices = Array.Empty<StatusNotice>();

    /// <summary><c>true</c> when at least one notice is Warning or Error —
    /// drives the problem triangle's visibility.</summary>
    public bool HasProblemNotices => Notices.Any(n => n.Severity >= StatusNoticeSeverity.Warning);

    /// <summary>
    /// <c>true</c> when the row carries info-only notices (e.g. a
    /// marker-resolved version) that neither the paperclip nor the problem
    /// triangle make reachable — drives the muted info badge.
    /// </summary>
    public bool ShowInfoBadge =>
        Notices.Count > 0 && !IsDependency && !HasProblemNotices;

    /// <summary>One-line hint for the info badge (details live in the dialog).</summary>
    public string InfoBadgeTooltip =>
        SmartCon.UI.LanguageManager.GetString(SmartCon.UI.StringLocalization.Keys.FM_Badge_Info_Tooltip)
            ?? "Информация — нажмите для подробностей";

    /// <summary>Worst severity among Warning/Error notices (triangle colour).</summary>
    public StatusNoticeSeverity ProblemBadgeSeverity =>
        Notices.Where(n => n.Severity >= StatusNoticeSeverity.Warning)
               .Select(n => (StatusNoticeSeverity?)n.Severity)
               .Max() ?? StatusNoticeSeverity.Warning;

    /// <summary>One-line hint for the problem triangle (details live in the dialog).</summary>
    public string ProblemBadgeTooltip =>
        ProblemBadgeSeverity == StatusNoticeSeverity.Error
            ? SmartCon.UI.LanguageManager.GetString(SmartCon.UI.StringLocalization.Keys.FM_Badge_Error_Tooltip)
                ?? "Ошибка — нажмите для подробностей"
            : SmartCon.UI.LanguageManager.GetString(SmartCon.UI.StringLocalization.Keys.FM_Badge_Warning_Tooltip)
                ?? "Предупреждение — нажмите для подробностей";

    /// <summary>One-line hint for the dependency paperclip (details live in the dialog).</summary>
    public string DependencyBadgeTooltip =>
        SmartCon.UI.LanguageManager.GetString(SmartCon.UI.StringLocalization.Keys.FM_Badge_Dependency_Tooltip)
            ?? "Зависимость — нажмите для подробностей";

    /// <summary>
    /// Rebuilds <see cref="Notices"/> from the current badge inputs:
    /// title + bullet list of the concrete family names + a short guidance
    /// (no duplicated names in the text — the dialog layout keeps it clean).
    /// </summary>
    private void RebuildNotices()
    {
        static string Loc(string key, string fallback) =>
            SmartCon.UI.LanguageManager.GetString(key) ?? fallback;
        static string Fmt(string key, string fallback, params object[] args) =>
            string.Format(System.Globalization.CultureInfo.CurrentCulture, Loc(key, fallback), args);

        var list = new List<StatusNotice>();
        if (HasOutdatedDependencyBlock)
        {
            list.Add(new StatusNotice(
                StatusNoticeSeverity.Error,
                Loc(SmartCon.UI.StringLocalization.Keys.FM_Notice_DependencyBlock_Title,
                    "Импорт заблокирован устаревшими вложенными"),
                Loc(SmartCon.UI.StringLocalization.Keys.FM_Notice_DependencyBlock_Guidance,
                    "Обновите вложенные семейства внутри родителя и повторите импорт, или выберите «Сделать активной» на строке вложенного. Пока конфликт не разрешён, доступно только «Пропустить»."),
                OutdatedDependencyBlockNames));
        }
        if (HasFailedDependencies)
        {
            list.Add(new StatusNotice(
                StatusNoticeSeverity.Error,
                Loc(SmartCon.UI.StringLocalization.Keys.FM_Notice_FailedDependencies_Title,
                    "Зависимости не прошли проверку"),
                Loc(SmartCon.UI.StringLocalization.Keys.FM_Notice_FailedDeps_Guidance,
                    "Эти зависимости не прошли проверку и будут пропущены — элемент будет импортирован без них."),
                FailedDependencyNames));
        }
        if (IsOutdatedNested)
        {
            list.Add(new StatusNotice(
                StatusNoticeSeverity.Warning,
                Loc(SmartCon.UI.StringLocalization.Keys.FM_Notice_OutdatedNested_Title,
                    "Зашита устаревшая версия"),
                Fmt(SmartCon.UI.StringLocalization.Keys.FM_Notice_OutdatedNested_Body,
                    "Зашита {0}, активна {1}. Импорт родителя заблокирован: обновите вложенное семейство внутри родителя и повторите импорт — или выберите «Сделать активной», чтобы каталог вернулся на зашитую версию.",
                    MatchedVersionLabel ?? string.Empty, ExistingVersionLabel ?? string.Empty)));
        }
        if (IsCrossNameDuplicate)
        {
            list.Add(new StatusNotice(
                StatusNoticeSeverity.Warning,
                Loc(SmartCon.UI.StringLocalization.Keys.FM_Notice_CrossNameDuplicate_Title,
                    "Содержимое совпадает под другим именем"),
                Loc(SmartCon.UI.StringLocalization.Keys.FM_Notice_CrossName_Body,
                    "Содержимое совпадает, хотя имя файла другое. «Сделать активной» — файл не импортируется, активируется найденная версия. «Новая версия» — семейство будет переименовано в имя этого файла."),
                [$"{MatchedItemName} ({MatchedVersionLabel})"]));
        }
        if (HasAssignmentConflict)
        {
            list.Add(new StatusNotice(
                StatusNoticeSeverity.Warning,
                Loc(SmartCon.UI.StringLocalization.Keys.FM_Notice_AssignmentConflict_Title,
                    "Автоназначение: подходят несколько категорий"),
                Loc(SmartCon.UI.StringLocalization.Keys.FM_Notice_AssignmentConflict_Guidance,
                    "Под условия подходят все перечисленные категории. Выберите одну вручную через кнопку «…» в колонке «Категория» — до этого семейство остаётся в «Без категории»."),
                AssignmentConflictCandidates));
        }
        if (IsDependency)
        {
            list.Add(new StatusNotice(
                StatusNoticeSeverity.Info,
                Loc(SmartCon.UI.StringLocalization.Keys.FM_Notice_DependencyOf_Title,
                    "Зависимость другого элемента"),
                null,
                DependencyParentNames));
        }
        if (IsMarkerResolvedVersion)
        {
            list.Add(new StatusNotice(
                StatusNoticeSeverity.Info,
                Loc(SmartCon.UI.StringLocalization.Keys.FM_Notice_MarkerResolved_Title,
                    "Версия определена по маркеру"),
                Fmt(SmartCon.UI.StringLocalization.Keys.FM_BatchImport_MarkerResolved_Tooltip,
                    "Версия {0} определена по маркеру верификации (содержимое проверено при обновлении). Контентный хэш зашитой копии соответствует другой версии: группы параметров не переносятся при обновлении — это ожидаемо.",
                    MatchedVersionLabel ?? string.Empty)));
        }
        Notices = list;
    }

    partial void OnMatchedVersionLabelChanged(string? value) => RebuildNotices();
    partial void OnExistingVersionLabelChanged(string? value) => RebuildNotices();
    partial void OnIsMarkerResolvedVersionChanged(bool value) => RebuildNotices();
    partial void OnIsCrossNameDuplicateChanged(bool value) => RebuildNotices();
    partial void OnMatchedItemNameChanged(string? value) => RebuildNotices();
    partial void OnDependencyParentNamesChanged(IReadOnlyList<string>? value) => RebuildNotices();
    partial void OnFailedDependencyNamesChanged(IReadOnlyList<string>? value) => RebuildNotices();
    partial void OnAssignmentConflictCandidatesChanged(IReadOnlyList<string>? value) => RebuildNotices();

    partial void OnTargetCategoryIdChanged(string? value)
    {
        // #241: an explicit category assignment (user picker or auto-match)
        // resolves the ambiguous-conflict state.
        if (!string.IsNullOrEmpty(value) && HasAssignmentConflict)
        {
            AssignmentConflictCandidates = null;
        }
    }

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

    partial void OnActionChanged(FamilyBatchImportAction value)
    {
        if (_suppressActionBroadcast) return;
        ActionChanged?.Invoke(this, value);
    }

    partial void OnStatusChanged(FamilyBatchImportStatus value)
    {
        RebuildNotices();

        // Gate block wins over the status-driven action set: a blocked row
        // stays forced to Skip no matter how the dedup status flips.
        // E2 (#209): the outdated-nested import block wins too — a rename
        // of a blocked parent must not restore the full action set.
        if (IsGateBlocked || HasOutdatedDependencyBlock)
        {
            AvailableActions = [FamilyBatchImportAction.Skip];
            SetActionSilently(FamilyBatchImportAction.Skip);
            return;
        }

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

    partial void OnGateStatusChanged(FamilyRowGateStatus value)
    {
        if (value == FamilyRowGateStatus.Failed)
        {
            AvailableActions = [FamilyBatchImportAction.Skip];
            SetActionSilently(FamilyBatchImportAction.Skip);
            return;
        }

        // E2 (#209): a dependency-blocked row stays forced to Skip even when
        // the gate unblocks — the two block sources are independent.
        if (HasOutdatedDependencyBlock)
        {
            AvailableActions = [FamilyBatchImportAction.Skip];
            SetActionSilently(FamilyBatchImportAction.Skip);
            return;
        }

        // Unblock: restore the status-driven action set and reset the
        // forced Skip so a row that passes after a category change
        // becomes importable again (the user can re-pick Skip manually).
        var wasBlocked = !AvailableActions.Contains(FamilyBatchImportAction.IncrementVersion)
            && Action == FamilyBatchImportAction.Skip;
        AvailableActions = BuildAvailableActions(Status);
        if (value != FamilyRowGateStatus.Checking
            && (wasBlocked || !AvailableActions.Contains(Action)))
        {
            SetActionSilently(
                AvailableActions.Contains(FamilyBatchImportAction.IncrementVersion)
                    ? FamilyBatchImportAction.IncrementVersion
                    : FamilyBatchImportAction.Skip);
        }
    }

    /// <summary>
    /// E2 (#209): the outdated-nested import block forces Skip exactly like
    /// the validation gate does; clearing the block restores the
    /// status-driven action set (mirrors <see cref="OnGateStatusChanged"/>).
    /// Block-driven changes never broadcast to the multi-selection.
    /// </summary>
    partial void OnOutdatedDependencyBlockNamesChanged(IReadOnlyList<string>? value)
    {
        RebuildNotices();

        if (HasOutdatedDependencyBlock)
        {
            AvailableActions = [FamilyBatchImportAction.Skip];
            SetActionSilently(FamilyBatchImportAction.Skip);
            return;
        }

        if (IsGateBlocked) return;

        var wasBlocked = !AvailableActions.Contains(FamilyBatchImportAction.IncrementVersion)
            && Action == FamilyBatchImportAction.Skip;
        AvailableActions = BuildAvailableActions(Status);
        if (wasBlocked || !AvailableActions.Contains(Action))
        {
            SetActionSilently(
                AvailableActions.Contains(FamilyBatchImportAction.IncrementVersion)
                    ? FamilyBatchImportAction.IncrementVersion
                    : FamilyBatchImportAction.Skip);
        }
    }

    /// <summary>
    /// Gate-driven action changes must NOT batch-propagate to the other
    /// selected rows: a forced Skip (or its reset) is this row's own
    /// verdict, not a user instruction for the whole selection. User
    /// picks in the combo box keep broadcasting via
    /// <see cref="ActionChanged"/>.
    /// </summary>
    private bool _suppressActionBroadcast;

    private void SetActionSilently(FamilyBatchImportAction action)
    {
        _suppressActionBroadcast = true;
        try
        {
            Action = action;
        }
        finally
        {
            _suppressActionBroadcast = false;
        }
    }

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
