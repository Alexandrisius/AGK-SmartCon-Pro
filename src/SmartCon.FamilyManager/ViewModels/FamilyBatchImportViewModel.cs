using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// ViewModel for the batch import dialog.
/// </summary>
public sealed partial class FamilyBatchImportViewModel : ObservableObject, IObservableRequestClose, ICloseAwareViewModel, IDisposable
{
    public event Action<bool?>? RequestClose;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    private bool _canImport;

    [ObservableProperty]
    private ObservableCollection<FamilyBatchImportRow> _items = new();

    /// <summary>
    /// VM-owned set of currently selected rows. Maintained through
    /// <see cref="OnRowSelectionChanged"/> wired to each row's
    /// <c>IsSelected</c> property. This is the source of truth for batch
    /// operations, NOT <c>DataGrid.SelectedItems</c> — that one collapses
    /// when the user clicks an inline editor (ComboBox / Button) and would
    /// defeat the multi-select batch-apply UX.
    /// </summary>
    private readonly HashSet<FamilyBatchImportRow> _selectedRows = new();

    private readonly IFamilyManagerDialogService _dialogService;
    private readonly IFamilyManagerViewModelFactory _viewModelFactory;
    private readonly IFamilyCatalogProvider? _catalogProvider;
    /// <summary>
    /// Import Validation Gate: resolves effective rules per category and
    /// evaluates row snapshots. Nullable for backward compatibility with
    /// older test fixtures — production always passes a real instance;
    /// when null, the gate stays inert (rows behave as pre-feature).
    /// </summary>
    private readonly IFamilyImportValidationService? _validationService;
    /// <summary>
    /// #241: auto-assignment rules evaluation. Nullable for backward
    /// compatibility with older test fixtures — production always passes a
    /// real instance; when null (or no rules configured), no row gets an
    /// automatic category.
    /// </summary>
    private readonly ICategoryAutoAssignService? _autoAssignService;
    /// <summary>
    /// #241: rules preloaded ONCE per dialog (the dialog is modal — rules
    /// cannot change mid-session; documented limitation). Sync
    /// <see cref="ICategoryAutoAssignService.Evaluate"/> uses this cache on
    /// every row evaluation.
    /// </summary>
    private CategoryAutoAssignPreloaded? _autoAssignRules;
    /// <summary>
    /// v2.0.0: optional precomputer that re-derives the
    /// (CatalogItemId, VersionLabel, ManagedPath) triple when the user
    /// renames a row in the dialog. Nullable for backward compatibility
    /// with older test fixtures that don't wire it up — production
    /// code always passes a real instance.
    /// </summary>
    private readonly IFamilyImportPrecomputer? _importPrecomputer;
    private readonly IContentHashDedupService? _dedupService;
    private readonly IFamilyBatchImportExecutor? _executor;
    /// <summary>
    /// #249 (Phase 4): read access to the stored content analytics of the
    /// ACTIVE version (section hashes + per-type hashes) for the "what
    /// changed" diff. Nullable for backward compatibility with older test
    /// fixtures — the diff then degrades to the "analytics pending"
    /// notice instead of failing.
    /// </summary>
    private readonly IContentHashAnalyticsRepository? _analyticsRepository;
    private readonly string? _categoryId;
    private readonly string? _publishedByUser;
    /// <summary>
    /// Dispatcher for marshalling background name-change recomputes back to
    /// the UI thread (ADR-031/ADR-036). Production passes the shared
    /// <see cref="IDispatcher"/> from FamilyManagerServices; the default
    /// inline fallback executes directly (unit tests, where no UI thread
    /// with a message pump exists).
    /// </summary>
    private readonly IDispatcher _dispatcher;
    private bool _disposed;
    private bool _batchApplying;

    private sealed class InlineDispatcher : IDispatcher
    {
        public bool CheckAccess() => true;
        public void Invoke(Action action) => action();
        public Task InvokeAsync(Action action, CancellationToken ct = default)
        {
            action();
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// v2.0.0 hotfix: debouncer for the per-row name change handler. The
    /// user can type freely in the FileName cell, and we wait
    /// <see cref="NameChangeDebounceMs"/> ms of quiet before asking the
    /// catalog whether the new name already exists. The previous
    /// implementation never re-checked, so renaming a row in the dialog
    /// did not update its Status (it would stay "Existing" even after
    /// the user typed a unique name).
    /// </summary>
    private const int NameChangeDebounceMs = 250;

    /// <summary>
    /// Per-row pending name-change recomputation. A single shared CTS
    /// cancelled the PREVIOUS row's recompute when two rows were renamed
    /// in quick succession, leaving the first row with a stale
    /// precomputed triple; <see cref="RunImportAsync"/> awaits every
    /// pending task before snapshotting rows so a rename typed right
    /// before pressing Import cannot race the import.
    /// </summary>
    private readonly Dictionary<FamilyBatchImportRow, (CancellationTokenSource Cts, Task Task)> _pendingNameChanges = new();
    private readonly object _pendingNameChangesLock = new();

    /// <summary>
    /// Import Validation Gate: the most recent in-flight
    /// <see cref="RevalidateRowsSafeAsync"/> task. Awaited by
    /// <c>RunImportAsync</c> so a category change typed right before
    /// pressing Import cannot race the row snapshot.
    /// </summary>
    private Task? _pendingValidation;

    /// <summary>
    /// Set when the dialog starts closing — in-flight gate revalidations
    /// discard their results instead of mutating rows of a torn-down view.
    /// </summary>
    private volatile bool _isClosing;

    public FamilyBatchImportViewModel(
        IReadOnlyList<FamilyBatchImportItem> items,
        IFamilyManagerDialogService dialogService,
        IFamilyManagerViewModelFactory viewModelFactory,
        string? defaultCategoryId = null,
        string? defaultCategoryName = null,
        IFamilyCatalogProvider? catalogProvider = null,
        IFamilyImportPrecomputer? importPrecomputer = null,
        IContentHashDedupService? dedupService = null,
        IFamilyBatchImportExecutor? executor = null,
        string? publishedByUser = null,
        IDispatcher? dispatcher = null,
        IFamilyImportValidationService? validationService = null,
        ICategoryAutoAssignService? autoAssignService = null,
        IContentHashAnalyticsRepository? analyticsRepository = null)
    {
        _dialogService = dialogService;
        _viewModelFactory = viewModelFactory;
        _catalogProvider = catalogProvider;
        _importPrecomputer = importPrecomputer;
        _dedupService = dedupService;
        _executor = executor;
        _analyticsRepository = analyticsRepository;
        _categoryId = defaultCategoryId;
        _publishedByUser = publishedByUser;
        _dispatcher = dispatcher ?? new InlineDispatcher();
        _validationService = validationService;
        _autoAssignService = autoAssignService;
        InitializeExecutionState();

        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.TargetCategoryId) && !string.IsNullOrEmpty(defaultCategoryId))
            {
                item.TargetCategoryId = defaultCategoryId;
                item.TargetCategoryName ??= defaultCategoryName;
            }
            var row = new FamilyBatchImportRow(item);
            // Issue #135 defect 1: when the dialog was opened via
            // «Импорт в категорию» (defaultCategoryId != null), the
            // preselected category is an explicit user instruction —
            // lock it (Command) so a rename never resets it to
            // «Без категории». A category inherited from an existing
            // catalog item stays automatic (AutoName) and continues to
            // follow renames.
            if (!string.IsNullOrEmpty(defaultCategoryId)
                && string.Equals(row.TargetCategoryId, defaultCategoryId, StringComparison.Ordinal))
            {
                row.CategoryProvenance = CategoryProvenance.Command;
            }
            else if (!string.IsNullOrEmpty(row.TargetCategoryId))
            {
                row.CategoryProvenance = CategoryProvenance.AutoName;
            }
            else
            {
                row.CategoryProvenance = CategoryProvenance.None;
            }
            row.PropertyChanged += OnRowPropertyChanged;
            row.PickCategoryRequested += OnRowPickCategoryRequestedAsync;
            row.PickRecommendedCategoryRequested += OnRowPickRecommendedCategoryRequestedAsync;
            row.ActionChanged += OnRowActionChanged;
            row.CategoryChanged += OnRowCategoryChanged;
            row.SelectionChanged += OnRowSelectionChanged;
            row.NameChanged += OnRowNameChanged;
            row.OpenValidationReportRequested += OnRowOpenValidationReport;
            row.OpenStatusDetailsRequested += OnRowOpenStatusDetails;
            row.OpenDiffDetailsRequested += OnRowOpenDiffDetailsAsync;
            Items.Add(row);
        }
        var commandLockedCount = Items.Count(r => r.CategoryProvenance == CategoryProvenance.Command);
        if (commandLockedCount > 0)
        {
            SmartConLogger.Debug(
                $"BatchImport.Category: locked {commandLockedCount}/{Items.Count} rows to command category " +
                $"'{defaultCategoryName ?? defaultCategoryId}' (provenance=Command)");
        }
        UpdateCanImport();

        // Import Validation Gate + auto-assignment (#241): rows that
        // arrive with a category assigned (Import-to-Category command,
        // AutoName from existing items) get their rule check immediately;
        // rows without a category get one from the assignment rules FIRST
        // so the gate revalidation covers the assigned category too.
        // Health-blocked rows are included in the gate: they stay Failed
        // but still collect the rule report for the detail dialog.
        _pendingValidation = RunInitialGateAsync();

        // ADR-066: initial dependency indicators (health-based gate states
        // are already known from row construction; rule-based states refresh
        // the indicators again when the async revalidation lands).
        RefreshDependencyIndicators();
    }

    private async Task OnRowPickCategoryRequestedAsync(FamilyBatchImportRow row)
    {
        try
        {
            var pickerVm = _viewModelFactory.CreateCategoryPickerViewModel();
            await pickerVm.InitializeAsync();
            var result = _dialogService.ShowCategoryPicker(pickerVm);
            if (result is not null)
            {
                if (string.IsNullOrEmpty(result))
                {
                    // v2.0.1 hotfix: picking "Без категории" (null/empty)
                    // is the user's way of saying "reset, let the
                    // catalog decide by name". Unlike picking a real
                    // category, it must NOT lock the category away from
                    // the dynamic ExistingCatalogItemId lookup — otherwise
                    // renaming to another existing family would keep the
                    // row at "Без категории" instead of pulling the
                    // target family's category. Clear the lock so
                    // ApplyNameChangeResult picks the category up again.
                    // Issue #135: provenance is set BEFORE the path so the
                    // CategoryChanged batch-apply observes the new source
                    // provenance.
                    row.CategoryProvenance = CategoryProvenance.None;
                    row.TargetCategoryId = null;
                    row.TargetCategoryPath = LanguageManager.GetString(StringLocalization.Keys.FM_NoCategory) ?? "Без категории";
                    // #241: the rules' recommendation survives the reset —
                    // «Без категории» is not a recommended category, so the
                    // warning icon stays visible and keeps pointing at the
                    // recommended categories.
                }
                else
                {
                    // v2.0.1: a real category choice is a deliberate
                    // "move to this category" instruction. Lock the
                    // category so a subsequent rename does not silently
                    // re-categorize the row.
                    row.CategoryProvenance = CategoryProvenance.Manual;
                    row.TargetCategoryId = result;
                    row.TargetCategoryPath = pickerVm.SelectedPath;
                }
                // OnTargetCategoryPathChanged partial-method on Row fires
                // ApplyCategoryToSelection, so the multi-select batch effect
                // is delivered without an explicit call here.
            }
        }
        catch (Exception ex)
        {
            SmartCon.Core.Logging.SmartConLogger.Error($"BatchImport.CategoryPicker: failed: {ex.Message}");
        }
    }

    /// <summary>
    /// #241: the category-column warning icon opens the picker
    /// pre-filtered to the categories the rules recommend, with a subtitle
    /// explaining why. An explicit pick from ANY picker is a deliberate
    /// user instruction → provenance Manual (locked).
    /// </summary>
    private async Task OnRowPickRecommendedCategoryRequestedAsync(FamilyBatchImportRow row)
    {
        try
        {
            if (!row.HasRuleRecommendation) return;

            var pickerVm = _viewModelFactory.CreateCategoryPickerViewModel(allowClear: false);
            var subtitle = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_RulePicker_Subtitle)
                    ?? "Под правила автоназначения подходят {0} категорий — выберите одну:",
                row.RecommendedCategoryIds!.Count);
            await pickerVm.InitializeRecommendedAsync(row.RecommendedCategoryIds!, subtitle);
            var result = _dialogService.ShowCategoryPicker(pickerVm);
            if (!string.IsNullOrEmpty(result))
            {
                row.CategoryProvenance = CategoryProvenance.Manual;
                row.TargetCategoryId = result;
                row.TargetCategoryPath = pickerVm.SelectedPath;
            }
        }
        catch (Exception ex)
        {
            SmartCon.Core.Logging.SmartConLogger.Error($"BatchImport.RecommendedPicker: failed: {ex.Message}");
        }
    }

    private void OnRowActionChanged(FamilyBatchImportRow row, FamilyBatchImportAction newValue)
    {
        // Re-entrancy guard: when ApplyActionToSelection sets
        // target.Action = newValue below, that fires OnActionChanged on
        // the target, which would re-enter this method. The flag is
        // also checked inside ApplyActionToSelection itself for the
        // same reason.
        if (_batchApplying) return;
        ApplyActionToSelection(row, newValue);

        // E2 (#209): MakeActive on an outdated-nested row lifts its
        // parents' import block (and switching back re-arms it).
        RefreshDependencyIndicators();
    }

    private void OnRowCategoryChanged(FamilyBatchImportRow row, (string? Id, string Path) payload)
    {
        if (_batchApplying) return;
        ApplyCategoryToSelection(row, payload.Id, payload.Path);

        // Import Validation Gate: re-check the affected rows against the
        // new category's rules (source + batch-applied selection).
        var affected = new List<FamilyBatchImportRow> { row };
        affected.AddRange(GetOtherSelectedRows(row).Where(r => r.CategoryProvenance == row.CategoryProvenance));
        _pendingValidation = RevalidateRowsSafeAsync(affected);
    }

    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FamilyBatchImportRow.CanImport))
        {
            UpdateCanImport();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_pendingNameChangesLock)
        {
            foreach (var pending in _pendingNameChanges.Values)
            {
                pending.Cts.Cancel();
                pending.Cts.Dispose();
            }
            _pendingNameChanges.Clear();
        }
        DisposeExecution();

        foreach (var row in Items)
        {
            row.PropertyChanged -= OnRowPropertyChanged;
            row.PickCategoryRequested -= OnRowPickCategoryRequestedAsync;
            row.ActionChanged -= OnRowActionChanged;
            row.CategoryChanged -= OnRowCategoryChanged;
            row.SelectionChanged -= OnRowSelectionChanged;
            row.NameChanged -= OnRowNameChanged;
            row.OpenValidationReportRequested -= OnRowOpenValidationReport;
            row.OpenStatusDetailsRequested -= OnRowOpenStatusDetails;
        }
    }

    private void UpdateCanImport()
    {
        CanImport = Items.Any(r => r.CanImport);
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// Returns items with user-selected actions for the caller.
    /// v2.0.0: also re-emits the <see cref="FamilyBatchImportItem.Source"/>
    /// payload AND the precomputed
    /// (CatalogItemId, VersionLabel, ManagedPath) triple.
    ///
    /// Without <c>Source</c>, the post-dialog staging flow in
    /// <c>ProcessProjectImportAsync</c> sees <c>Source = null</c> on
    /// every row and skips all staging, leaving the placeholder
    /// <c>FilePath</c> ("system://..." / "loadable://...") in place.
    /// The orchestrator then calls <c>ImportFileAsync</c> with a
    /// non-existent path and reports <c>Success = false</c> for every
    /// item. This was the root cause of the v2.0.0 batch-import
    /// regression (UC-3/UC-4 imported zero families).
    ///
    /// Without the precomputed triple, staging falls back to
    /// <c>ComputeSystemFamilyManagedPath</c> with a fresh GUID, the
    /// staged file lands at a path that no longer matches the
    /// <c>family_files.relative_path</c> row that
    /// <see cref="IFamilyImportService.ImportFileAsync"/> would later
    /// allocate, and every row returns <c>Success = false</c>. This
    /// regression bit hard in the most recent Revit run, so the row VM
    /// now propagates all three precomputed values back to the caller.
    /// </summary>
    public IReadOnlyList<FamilyBatchImportItem> GetResultItems()
    {
        return Items.Select(r => new FamilyBatchImportItem(
            r.FilePath,
            r.FileName,
            r.RevitMajorVersion,
            r.Status,
            r.ExistingCatalogItemId,
            r.ExistingVersionLabel,
            r.TargetCategoryId,
            r.TargetCategoryPath,
            r.FamilySource,
            r.TypeCount,
            r.RevitCategory,
            OriginalSourcePath: null,
            SourceTypes: r.SourceTypes,
            Source: r.Source,
            PrecomputedCatalogItemId: r.PrecomputedCatalogItemId,
            PrecomputedVersionLabel: r.PrecomputedVersionLabel,
            PrecomputedManagedPath: r.PrecomputedManagedPath,
            ContentHash: r.PrecomputedContentHash,
            HashFormatVersion: r.HashFormatVersion,
            MatchedVersionLabel: r.MatchedVersionLabel,
            LoadableSnapshot: r.LoadableSnapshot,
            SystemSnapshot: r.SystemSnapshot,
            IsCrossNameDuplicate: r.IsCrossNameDuplicate,
            MatchedItemName: r.MatchedItemName,
            ExistingCategoryId: r.ExistingCategoryId,
            ExistingCategoryPath: r.ExistingCategoryPath,
            DependencyLinks: r.DependencyLinks,
            PerTypeHashes: r.PerTypeHashes,
            Sections: r.Sections,
            UnsubstitutedMiniRouting: r.UnsubstitutedMiniRouting)
        {
            Action = r.Action
        }).ToList();
    }
}
