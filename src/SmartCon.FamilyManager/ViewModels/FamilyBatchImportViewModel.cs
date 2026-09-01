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

    private void OnRowSelectionChanged(FamilyBatchImportRow row, bool isSelected)
    {
        if (isSelected)
        {
            _selectedRows.Add(row);
        }
        else
        {
            _selectedRows.Remove(row);
        }
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

    /// <summary>
    /// Import Validation Gate: runs the rule check for rows against their
    /// assigned categories. Rules are fetched once per category (async
    /// SQLite I/O on the thread pool); ALL row mutations are marshalled
    /// back through <see cref="_dispatcher"/> (ADR-031/036) — the
    /// continuation after <c>ConfigureAwait(false)</c> runs off the UI
    /// thread, and touching <c>_selectedRows</c> / row properties there
    /// would race the UI. Rows blocked by the health check keep their
    /// Failed status (the rule report is still recorded for the report
    /// dialog).
    /// </summary>
    private async Task RevalidateRowsSafeAsync(IReadOnlyList<FamilyBatchImportRow> rows)
    {
        if (_validationService is null || rows.Count == 0) return;

        var byCategory = rows
            .Where(r => !string.IsNullOrEmpty(r.TargetCategoryId))
            .GroupBy(r => r.TargetCategoryId!)
            .ToList();
        var noCategoryRows = rows
            .Where(r => string.IsNullOrEmpty(r.TargetCategoryId))
            .ToList();

        // Checking state first (we are on the UI thread at entry — all
        // callers are UI event handlers / the constructor).
        foreach (var row in byCategory.SelectMany(g => g))
        {
            if (!row.IsGateBlocked)
            {
                row.GateStatus = FamilyRowGateStatus.Checking;
            }
        }

        // Fetch phase (thread pool): per-category rules with per-group
        // error isolation — one failing category must not strand the
        // others in Checking forever.
        var fetched = new List<(string CategoryId, List<FamilyBatchImportRow> Rows, IReadOnlyList<EffectiveValidationRule>? Rules)>();
        foreach (var group in byCategory)
        {
            try
            {
                var rules = await _validationService.GetEffectiveRulesAsync(group.Key).ConfigureAwait(false);
                fetched.Add((group.Key, group.ToList(), rules));
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"BatchImport.Gate: rules fetch failed for category '{group.Key}': {ex.Message} " +
                    "[Action: проверьте, что БД каталога доступна; строки этой категории остались непроверенными]");
                fetched.Add((group.Key, group.ToList(), null));
            }
        }

        // Mutation phase (UI thread via dispatcher). Skipped entirely when
        // the dialog is already closing: mutating rows of a torn-down view
        // produces a visible flicker and serves nobody.
        if (_isClosing)
        {
            SmartConLogger.Debug("BatchImport.Gate: revalidation result discarded — dialog is closing");
            return;
        }

        _dispatcher.Invoke(() =>
        {
            if (_isClosing)
            {
                SmartConLogger.Debug("BatchImport.Gate: revalidation mutation skipped — dialog is closing");
                return;
            }

            try
            {
                // Rows with no category revert to the health-only state.
                // The guard keys on the HEALTH block specifically: a
                // rule-blocked row becomes unblocked when its category is
                // cleared, while a health-blocked row stays Failed.
                foreach (var row in noCategoryRows)
                {
                    row.ValidationReport = null;
                    row.ValidationRulesCount = 0;
                    if (row.HealthReport?.IsHealthy == false)
                    {
                        row.GateStatus = FamilyRowGateStatus.Failed;
                    }
                    else
                    {
                        row.GateStatus = row.HealthReport is not null && row.HealthReport.Issues.Count > 0
                            ? FamilyRowGateStatus.Warning
                            : FamilyRowGateStatus.NotChecked;
                    }
                }

                foreach (var (categoryId, groupRows, rules) in fetched)
                {
                    if (rules is null)
                    {
                        // Fetch failed: revert to the health-only state so
                        // the rows are not stuck in Checking.
                        foreach (var row in groupRows)
                        {
                            row.ValidationReport = null;
                            row.ValidationRulesCount = 0;
                            if (row.HealthReport?.IsHealthy == false) continue;
                            row.GateStatus = row.HealthReport is not null && row.HealthReport.Issues.Count > 0
                                ? FamilyRowGateStatus.Warning
                                : FamilyRowGateStatus.NotChecked;
                        }
                        continue;
                    }

                    foreach (var row in groupRows)
                    {
                        // The user may have re-picked the category while
                        // the rules query was running — discard the stale
                        // result.
                        if (!string.Equals(row.TargetCategoryId, categoryId, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        var report = _validationService.ValidateFromSnapshots(
                            row.LoadableSnapshot, row.SystemSnapshot, rules);

                        row.ValidationReport = report;
                        row.ValidationRulesCount = rules.Count;

                        if (row.HealthReport?.IsHealthy == false)
                        {
                            row.GateStatus = FamilyRowGateStatus.Failed;
                        }
                        else if (!report.IsValid)
                        {
                            row.GateStatus = FamilyRowGateStatus.Failed;
                            SmartConLogger.Debug(
                                $"BatchImport.Gate: '{row.FileName}' failed validation for category '{categoryId}': " +
                                $"{report.Violations.Count} violation(s) of {rules.Count} rule(s)");
                        }
                        else if (row.HealthReport is not null && row.HealthReport.Issues.Count > 0)
                        {
                            row.GateStatus = FamilyRowGateStatus.Warning;
                        }
                        else
                        {
                            row.GateStatus = FamilyRowGateStatus.Passed;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SmartConLogger.Error(
                    $"BatchImport.Gate: revalidation mutation failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                RefreshDependencyIndicators();
                UpdateCanImport();
            }
        });
    }

    /// <summary>
    /// #241: the combined dialog-open pipeline — initial gate revalidation
    /// for rows that already have a category, then auto-assignment for the
    /// rows without one, then a follow-up revalidation of the newly
    /// assigned rows (their rule check must run against the ASSIGNED
    /// category). The whole task is what <c>RunImportAsync</c> awaits via
    /// <see cref="_pendingValidation"/>.
    /// </summary>
    private async Task RunInitialGateAsync()
    {
        var rowsWithCategory = Items
            .Where(r => !string.IsNullOrEmpty(r.TargetCategoryId))
            .ToList();
        var initialGate = rowsWithCategory.Count > 0
            ? RevalidateRowsSafeAsync(rowsWithCategory)
            : Task.CompletedTask;

        List<FamilyBatchImportRow>? assigned = null;
        try
        {
            _autoAssignRules = _autoAssignService is not null
                ? await _autoAssignService.PreloadAsync().ConfigureAwait(false)
                : null;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"BatchImport.AutoAssign: rules preload failed: {ex.Message} " +
                "[Action: проверьте БД каталога; строки импортируются без автоназначения категорий]");
        }

        if (_autoAssignRules is { HasRules: true })
        {
            // Row mutations are marshalled to the UI thread (ADR-031): the
            // continuation after ConfigureAwait(false) runs off the UI
            // thread.
            await _dispatcher.InvokeAsync(() => assigned = ApplyAutoAssignment(Items))
                .ConfigureAwait(false);
        }

        await initialGate.ConfigureAwait(false);

        if (assigned is { Count: > 0 })
        {
            // RevalidateRowsSafeAsync mutates rows at entry — it must START
            // on the UI thread; the task continues in the background.
            Task? followUp = null;
            await _dispatcher.InvokeAsync(() => followUp = RevalidateRowsSafeAsync(assigned))
                .ConfigureAwait(false);
            if (followUp is not null)
            {
                await followUp.ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// #241: evaluates the assignment rules for EVERY row and applies the
    /// matched category only to the eligible ones (Status == New,
    /// provenance == None, not gate-blocked — Existing/Duplicate and
    /// Manual/Command rows are NEVER re-categorized automatically). Every
    /// row gets the recommendation state (ids + display paths) that drives
    /// the category-column warning icon — including Existing/Duplicate
    /// rows whose current category differs from the rules' recommendation.
    /// MUST run on the UI thread.
    /// </summary>
    private List<FamilyBatchImportRow> ApplyAutoAssignment(IEnumerable<FamilyBatchImportRow> candidates)
    {
        using var _scope = SmartConLogger.BeginScope("AutoAssign",
            ("Method", nameof(ApplyAutoAssignment)));
        var assigned = new List<FamilyBatchImportRow>();
        var recommended = 0;
        foreach (var row in candidates)
        {
            if (_isClosing)
            {
                SmartConLogger.Debug("BatchImport.AutoAssign: application skipped — dialog is closing");
                break;
            }

            CategoryAutoAssignResult result;
            try
            {
                result = _autoAssignService!.Evaluate(
                    _autoAssignRules!, row.LoadableSnapshot, row.SystemSnapshot, row.FileName);
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"BatchImport.AutoAssign: evaluation failed for '{row.FileName}': {ex.Message} " +
                    "[Action: проверьте логи; строка остаётся в «Без категории»]");
                continue;
            }

            ApplyRecommendation(row, result);

            if (result.Outcome == CategoryAutoAssignOutcome.Matched
                && row.Status == FamilyBatchImportStatus.New
                && row.CategoryProvenance == CategoryProvenance.None
                && !row.IsGateBlocked)
            {
                // Re-check the icon state after the assignment: the current
                // category now equals the recommendation → icon hidden.
                AssignAutoRuleCategory(row, result.CategoryId!);
                assigned.Add(row);
            }
            else if (result.Outcome != CategoryAutoAssignOutcome.NoMatch)
            {
                recommended++;
            }
        }

        if (assigned.Count > 0 || recommended > 0)
        {
            SmartConLogger.Info(
                $"Auto-assign: {assigned.Count} row(s) assigned, {recommended} row(s) recommended (not auto-applied) of {Items.Count}");
            UpdateCanImport();
        }

        return assigned;
    }

    /// <summary>
    /// #241: stores the rules' recommendation on the row (drives the
    /// category-column warning icon regardless of the row's eligibility
    /// for automatic assignment).
    /// </summary>
    private void ApplyRecommendation(FamilyBatchImportRow row, CategoryAutoAssignResult result)
    {
        switch (result.Outcome)
        {
            case CategoryAutoAssignOutcome.Matched when result.CategoryId is not null:
                row.RecommendedCategoryIds = [result.CategoryId];
                row.RecommendedCategoryPaths = [ResolveCategoryPath(result.CategoryId)];
                break;
            case CategoryAutoAssignOutcome.Ambiguous:
                row.RecommendedCategoryIds = result.CandidateCategoryIds;
                row.RecommendedCategoryPaths = ResolveCategoryPaths(result.CandidateCategoryIds);
                SmartConLogger.Debug(
                    $"BatchImport.AutoAssign: '{row.FileName}' recommendation — {result.CandidateCategoryIds.Count} categories match");
                break;
            default:
                row.RecommendedCategoryIds = null;
                row.RecommendedCategoryPaths = null;
                break;
        }
    }

    /// <summary>
    /// #241: re-evaluates ONE row's assignment rules (rename path). Runs on
    /// the UI thread with the cached rules; a matched category fires the
    /// row's CategoryChanged chain (provenance BEFORE path — same contract
    /// as <c>ApplyNameChangeResult</c>).
    /// </summary>
    private void TryAutoAssignRow(FamilyBatchImportRow row)
    {
        if (_autoAssignRules is not { HasRules: true })
        {
            return;
        }

        CategoryAutoAssignResult result;
        try
        {
            result = _autoAssignService!.Evaluate(
                _autoAssignRules, row.LoadableSnapshot, row.SystemSnapshot, row.FileName);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"BatchImport.AutoAssign: re-evaluation failed for '{row.FileName}': {ex.Message} " +
                "[Action: проверьте логи; рекомендация строки может быть неактуальной]");
            return;
        }

        ApplyRecommendation(row, result);

        if (result.Outcome == CategoryAutoAssignOutcome.Matched
            && result.CategoryId is not null
            && row.Status == FamilyBatchImportStatus.New
            && row.CategoryProvenance == CategoryProvenance.None
            && !row.IsGateBlocked)
        {
            AssignAutoRuleCategory(row, result.CategoryId);
        }
    }

    private void AssignAutoRuleCategory(FamilyBatchImportRow row, string categoryId)
    {
        // Provenance BEFORE the path: the CategoryChanged batch-apply
        // (fired by the path setter) must observe the row's new provenance.
        row.CategoryProvenance = CategoryProvenance.AutoRule;
        row.TargetCategoryId = categoryId;
        row.TargetCategoryPath = ResolveCategoryPath(categoryId);
        row.CategoryFlashToken++;
        SmartConLogger.Debug(
            $"BatchImport.AutoAssign: '{row.FileName}' -> '{row.TargetCategoryPath}' (provenance=AutoRule)");
    }

    private string ResolveCategoryPath(string categoryId) =>
        _autoAssignRules?.CategoryPathsById.TryGetValue(categoryId, out var path) == true
            ? path
            : categoryId;

    private List<string> ResolveCategoryPaths(IReadOnlyList<string> categoryIds)
    {
        var paths = new List<string>(categoryIds.Count);
        foreach (var id in categoryIds)
        {
            paths.Add(ResolveCategoryPath(id));
        }

        return paths;
    }

    /// <summary>
    /// ADR-066 (E1): recomputes the dependency indicators in both directions:
    /// child rows get their parent display names (<see cref="FamilyBatchImportRow.DependencyParentNames"/>),
    /// parent rows get the names of gate-failed children
    /// (<see cref="FamilyBatchImportRow.FailedDependencyNames"/>). Called
    /// after row construction and after every gate revalidation — a
    /// gate-failed child is already forced to Skip by the standard gate, so
    /// the parent stays importable and the indicator is informational
    /// ("will be imported without these dependencies").
    /// </summary>
    internal void RefreshDependencyIndicators()
    {
        var fileNameByPath = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in Items)
        {
            fileNameByPath[row.FilePath] = row.FileName;
        }

        var failedByParent = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var row in Items)
        {
            if (row.DependencyLinks is null)
            {
                row.DependencyParentNames = null;
                continue;
            }

            row.DependencyParentNames = row.DependencyLinks
                .Select(l => fileNameByPath.TryGetValue(l.ParentSourcePath, out var name) ? name : l.ParentSourcePath)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (!row.IsGateBlocked) continue;
            foreach (var link in row.DependencyLinks)
            {
                if (!failedByParent.TryGetValue(link.ParentSourcePath, out var list))
                {
                    list = new List<string>();
                    failedByParent.Add(link.ParentSourcePath, list);
                }

                if (!list.Contains(row.FileName))
                {
                    list.Add(row.FileName);
                }
            }
        }

        foreach (var row in Items)
        {
            row.FailedDependencyNames = failedByParent.TryGetValue(row.FilePath, out var failed)
                ? failed
                : null;
        }

        // E2 (#209): a dependency row embedding an OUTDATED nested version
        // (Duplicate matched to a non-active version) blocks its parents'
        // import — forced Skip with an explanatory badge. Escape hatch:
        // MakeActive on the child row switches the catalog back to the
        // embedded version, making it consistent — the block lifts.
        var blockedByParent = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var row in Items)
        {
            if (!row.IsOutdatedNested
                || row.Action == FamilyBatchImportAction.MakeActive
                || row.DependencyLinks is null)
            {
                continue;
            }

            var line = $"{row.FileName} ({row.MatchedVersionLabel} → {row.ExistingVersionLabel})";
            foreach (var link in row.DependencyLinks)
            {
                if (!blockedByParent.TryGetValue(link.ParentSourcePath, out var list))
                {
                    list = new List<string>();
                    blockedByParent.Add(link.ParentSourcePath, list);
                }

                if (!list.Contains(line))
                {
                    list.Add(line);
                }
            }
        }

        foreach (var row in Items)
        {
            row.OutdatedDependencyBlockNames = blockedByParent.TryGetValue(row.FilePath, out var lines)
                ? lines
                : null;
        }
    }

    /// <summary>
    /// #210: opens the status details dialog for a batch row. Two separate
    /// views: the problem triangle shows warning/error notices + follow-up
    /// actions (validation report); the paperclip / info badge shows only
    /// the related-elements info (dependency parents, marker origin) with
    /// no actions — warnings never leak into it.
    /// </summary>
    private void OnRowOpenStatusDetails(FamilyBatchImportRow row, bool infoOnly)
    {
        try
        {
            var notices = infoOnly
                ? row.Notices.Where(n => n.Severity == StatusNoticeSeverity.Info).ToList()
                : row.Notices.Where(n => n.Severity >= StatusNoticeSeverity.Warning).ToList();
            if (notices.Count == 0) return;

            var actions = new List<StatusDetailsAction>();
            if (!infoOnly && (row.HealthReport is not null || row.ValidationReport is not null))
            {
                actions.Add(new StatusDetailsAction(
                    SmartCon.UI.LanguageManager.GetString(SmartCon.UI.StringLocalization.Keys.FM_StatusDetails_OpenValidationReport)
                        ?? "Открыть отчёт о проверке",
                    () => OnRowOpenValidationReport(row)));
            }

            var detailsVm = new StatusDetailsViewModel(
                row.FileName,
                row.TargetCategoryPath,
                notices,
                actions);
            _dialogService.ShowStatusDetails(detailsVm);
        }
        catch (Exception ex)
        {
            SmartConLogger.Error(
                $"BatchImport.Badges: failed to open status details for '{row.FileName}': {ex.Message}");
        }
    }

    /// <summary>
    /// #249 (Phase 4): the "what changed" diff of an Existing row against
    /// the ACTIVE catalog version — change class (trivial/minor/major),
    /// changed sections, per-type lists, and for a trivial class the
    /// «Перезаписать текущую» follow-up action (the storage saver:
    /// fewer cosmetic new versions, fewer duplicate files).
    /// </summary>
    private async Task OnRowOpenDiffDetailsAsync(FamilyBatchImportRow row)
    {
        try
        {
            IReadOnlyDictionary<string, string>? activeHashes = null;
            IReadOnlyList<FamilyTypeHashEntry>? activeTypes = null;
            if (_analyticsRepository is not null
                && !string.IsNullOrEmpty(row.ExistingCatalogItemId)
                && !string.IsNullOrEmpty(row.ExistingVersionLabel))
            {
                activeHashes = await _analyticsRepository.GetSectionHashesAsync(
                    row.ExistingCatalogItemId!, row.ExistingVersionLabel!, CancellationToken.None)
                    .ConfigureAwait(true);
                activeTypes = await _analyticsRepository.GetTypeHashesAsync(
                    row.ExistingCatalogItemId!, row.ExistingVersionLabel!, CancellationToken.None)
                    .ConfigureAwait(true);
            }

            var sectionsPending = activeHashes is null;
            var typesPending = activeTypes is null;

            // When the ACTIVE side's analytics are pending, never show a
            // FAKE classification (every incoming section would read as
            // changed → a misleading red "Major" for every legacy row).
            // Sections/classification appear only on real data; per-type
            // lists are shown when the type analytics are ready
            // (independent of the section side).
            var diff = ContentVersionDiffComputer.Compute(
                sectionsPending ? (IReadOnlyList<ContentSectionHash>)[] : (row.Sections ?? (IReadOnlyList<ContentSectionHash>)[]),
                row.PerTypeHashes,
                activeHashes ?? new Dictionary<string, string>(),
                activeTypes ?? (IReadOnlyList<FamilyTypeHashEntry>)[]);

            var notices = new List<StatusNotice>();
            if (!sectionsPending)
            {
                var (classSeverity, classTitle, classExplanation) = DescribeClass(diff.Class);
                notices.Add(new StatusNotice(classSeverity, classTitle, classExplanation));
            }
            else
            {
                notices.Add(new StatusNotice(
                    StatusNoticeSeverity.Warning,
                    Loc(StringLocalization.Keys.FM_Diff_PendingAnalytics,
                        "Аналитика активной версии ещё не вычислена"),
                    Loc(StringLocalization.Keys.FM_Diff_PendingAnalyticsHint,
                        "Выполните «Обновить базу» — сравнение секций и класс изменений станут точными (per-type списки показаны по готовым данным).")));
            }

            if (!sectionsPending && diff.ChangedSections.Count > 0)
            {
                notices.Add(new StatusNotice(
                    StatusNoticeSeverity.Info,
                    Fmt(StringLocalization.Keys.FM_Diff_Sections, "Изменённые секции ({0})", diff.ChangedSections.Count),
                    null,
                    diff.ChangedSections.Select(DescribeSection).ToList()));
            }
            if (!typesPending && diff.ChangedTypes.Count > 0)
            {
                notices.Add(new StatusNotice(
                    StatusNoticeSeverity.Info,
                    Fmt(StringLocalization.Keys.FM_Diff_ChangedTypes, "Изменённые типы ({0})", diff.ChangedTypes.Count),
                    null,
                    diff.ChangedTypes));
            }
            if (!typesPending && diff.AddedTypes.Count > 0)
            {
                notices.Add(new StatusNotice(
                    StatusNoticeSeverity.Info,
                    Fmt(StringLocalization.Keys.FM_Diff_AddedTypes, "Новые типы ({0})", diff.AddedTypes.Count),
                    null,
                    diff.AddedTypes));
            }
            if (!typesPending && diff.RemovedTypes.Count > 0)
            {
                notices.Add(new StatusNotice(
                    StatusNoticeSeverity.Info,
                    Fmt(StringLocalization.Keys.FM_Diff_RemovedTypes, "Удалённые типы ({0})", diff.RemovedTypes.Count),
                    null,
                    diff.RemovedTypes));
            }

            var actions = new List<StatusDetailsAction>();
            if (!sectionsPending
                && diff.Class == ContentChangeClass.Trivial
                && row.AvailableActions.Contains(FamilyBatchImportAction.OverwriteCurrent))
            {
                actions.Add(new StatusDetailsAction(
                    Loc(StringLocalization.Keys.FM_Diff_OverwriteAction, "Перезаписать текущую версию"),
                    () => row.Action = FamilyBatchImportAction.OverwriteCurrent));
            }

            var detailsVm = new StatusDetailsViewModel(
                row.FileName,
                Fmt(StringLocalization.Keys.FM_Diff_Subtitle,
                    "сравнение с активной версией {0}", row.ExistingVersionLabel ?? "?"),
                notices,
                actions);
            _dialogService.ShowStatusDetails(detailsVm);
        }
        catch (Exception ex)
        {
            SmartConLogger.Error(
                $"BatchImport.Diff: failed to open the diff details for '{row.FileName}': {ex.Message}");
        }
    }

    private static (StatusNoticeSeverity Severity, string Title, string Explanation) DescribeClass(
        ContentChangeClass changeClass)
    {
        return changeClass switch
        {
            ContentChangeClass.Major => (
                StatusNoticeSeverity.Error,
                Loc(StringLocalization.Keys.FM_Diff_ClassMajor, "Существенные изменения"),
                Loc(StringLocalization.Keys.FM_Diff_ClassMajorHint,
                    "Изменены геометрия, привязки или коннекторы — рекомендуется «Новая версия», чтобы не потерять предыдущее содержимое.")),
            ContentChangeClass.Minor => (
                StatusNoticeSeverity.Warning,
                Loc(StringLocalization.Keys.FM_Diff_ClassMinor, "Умеренные изменения"),
                Loc(StringLocalization.Keys.FM_Diff_ClassMinorHint,
                    "Изменены значения типов, параметры или вложения — проверьте секции ниже перед выбором действия.")),
            ContentChangeClass.Trivial => (
                StatusNoticeSeverity.Info,
                Loc(StringLocalization.Keys.FM_Diff_ClassTrivial, "Косметические изменения"),
                Loc(StringLocalization.Keys.FM_Diff_ClassTrivialHint,
                    "Геометрия и параметры не затронуты — можно «Перезаписать текущую» вместо создания новой версии (экономия места).")),
            _ => (
                StatusNoticeSeverity.Info,
                Loc(StringLocalization.Keys.FM_Diff_ClassNone, "Содержимое идентично"),
                Loc(StringLocalization.Keys.FM_Diff_ClassNoneHint,
                    "Секции совпадают — различий с активной версией не найдено.")),
        };
    }

    /// <summary>
    /// Maps a content-section key (DEF, GEOM, …) to the localized
    /// user-facing name shown in the diff window — raw keys are storage
    /// identifiers, meaningless to the user. Unknown/future keys fall
    /// back to the raw key so nothing is ever hidden.
    /// </summary>
    private static string DescribeSection(string sectionKey)
    {
        var (locKey, fallback) = sectionKey switch
        {
            FamilyContentSectionNames.Meta => (StringLocalization.Keys.FM_Diff_Section_META, "Метаданные (формат, категория)"),
            FamilyContentSectionNames.Params => (StringLocalization.Keys.FM_Diff_Section_PARAMS, "Структура параметров"),
            FamilyContentSectionNames.Types => (StringLocalization.Keys.FM_Diff_Section_TYPES, "Типоразмеры и их значения"),
            FamilyContentSectionNames.Phantom => (StringLocalization.Keys.FM_Diff_Section_PHANTOM, "Фантомные типы"),
            FamilyContentSectionNames.Def => (StringLocalization.Keys.FM_Diff_Section_DEF, "Привязки параметров (видимость, материал, размеры)"),
            FamilyContentSectionNames.Geom => (StringLocalization.Keys.FM_Diff_Section_GEOM, "3D-геометрия"),
            FamilyContentSectionNames.Geom2d => (StringLocalization.Keys.FM_Diff_Section_GEOM2D, "2D-графика (условные обозначения)"),
            FamilyContentSectionNames.Nested => (StringLocalization.Keys.FM_Diff_Section_NESTED, "Вложенные семейства"),
            FamilyContentSectionNames.NonShared => (StringLocalization.Keys.FM_Diff_Section_NONSHARED, "Необщие вложенные семейства"),
            FamilyContentSectionNames.NestedHash => (StringLocalization.Keys.FM_Diff_Section_NESTEDHASH, "Содержимое вложенных семейств"),
            FamilyContentSectionNames.Facts => (StringLocalization.Keys.FM_Diff_Section_FACTS, "Факты семейства (Part Type)"),
            FamilyContentSectionNames.Flags => (StringLocalization.Keys.FM_Diff_Section_FLAGS, "Флаги семейства"),
            FamilyContentSectionNames.Conn => (StringLocalization.Keys.FM_Diff_Section_CONN, "Коннекторы"),
            FamilyContentSectionNames.Lookup => (StringLocalization.Keys.FM_Diff_Section_LOOKUP, "Lookup-таблицы"),
            FamilyContentSectionNames.FamKey => (StringLocalization.Keys.FM_Diff_Section_FAMKEY, "Ключ семейства"),
            FamilyContentSectionNames.Struct => (StringLocalization.Keys.FM_Diff_Section_STRUCT, "Структура (слои)"),
            FamilyContentSectionNames.Routing => (StringLocalization.Keys.FM_Diff_Section_ROUTING, "Маршрутизация (правила)"),
            FamilyContentSectionNames.Segments => (StringLocalization.Keys.FM_Diff_Section_SEGMENTS, "Сегменты и материалы"),
            FamilyContentSectionNames.Subtypes => (StringLocalization.Keys.FM_Diff_Section_SUBTYPES, "Подтипы"),
            FamilyContentSectionNames.Railing => (StringLocalization.Keys.FM_Diff_Section_RAILING, "Ограждение (структура)"),
            FamilyContentSectionNames.Wire => (StringLocalization.Keys.FM_Diff_Section_WIRE, "Провод (параметры)"),
            FamilyContentSectionNames.Values => (StringLocalization.Keys.FM_Diff_Section_VALUES, "Значения параметров"),
            _ => (string.Empty, sectionKey),
        };
        return string.IsNullOrEmpty(locKey) ? fallback : Loc(locKey, fallback);
    }

    private static string Loc(string key, string fallback)
        => SmartCon.UI.LanguageManager.GetString(key) ?? fallback;

    private static string Fmt(string key, string fallback, params object[] args)
        => string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            SmartCon.UI.LanguageManager.GetString(key) ?? fallback,
            args);

    private void OnRowOpenValidationReport(FamilyBatchImportRow row)
    {
        try
        {
            var reportVm = _viewModelFactory.CreateValidationReportViewModel(
                row.FileName,
                row.TargetCategoryPath,
                row.HealthReport,
                row.ValidationReport,
                row.ValidationRulesCount);
            _dialogService.ShowValidationReport(reportVm);
        }
        catch (Exception ex)
        {
            SmartConLogger.Error(
                $"BatchImport.Gate: failed to open validation report for '{row.FileName}': {ex.Message}");
        }
    }


    /// <summary>
    /// v2.0.0 hotfix: re-resolve catalog status when the user renames a
    /// row. Debounced by <see cref="NameChangeDebounceMs"/> so we don't
    /// fire one DB query per keystroke. The lookup uses
    /// <see cref="FamilyNameNormalizer"/> to match the same canonical
    /// form the pre-build flow uses, so the row's Status flips
    /// New ↔ Existing as soon as the user types a name that no longer
    /// (or now) matches an existing catalog item.
    /// <para>
    /// v2.0.0: when an <see cref="IFamilyImportPrecomputer"/> is wired
    /// in, we also re-derive the precomputed
    /// (CatalogItemId, VersionLabel, ManagedPath) triple — without this
    /// re-derivation, the dialog would carry a stale precomputed id
    /// (the one from the row's original name) into the post-dialog
    /// import, and <c>ImportFileAsync</c> would try to
    /// <c>INSERT</c> a new <c>catalog_items</c> row with that id,
    /// tripping the <c>UNIQUE constraint failed: catalog_items.id</c>
    /// failure mode observed in the v2.0.0 manual run (the
    /// "Трубы → Трубы новые" rename in the active-project flow).
    /// </para>
    /// </summary>
    private void OnRowNameChanged(FamilyBatchImportRow row, string newName)
    {
        if (_batchApplying) return;
        if (_catalogProvider is null && _importPrecomputer is null) return;
        if (string.IsNullOrWhiteSpace(newName)) return;

        lock (_pendingNameChangesLock)
        {
            if (_pendingNameChanges.TryGetValue(row, out var previous))
            {
                previous.Cts.Cancel();
                previous.Cts.Dispose();
                _pendingNameChanges.Remove(row);
            }
        }

        var cts = new CancellationTokenSource();
        var token = cts.Token;

        var extension = ResolveExtensionForRow(row);

        var rowContentHash = row.PrecomputedContentHash;
        var rowHashFormatVersion = row.HashFormatVersion;
        var rowFamilySource = row.FamilySource;
        // Issue #192: system-row identity is the BuiltInCategory ordinal —
        // the dedup needs it for the category-based fallback lookup.
        var rowRevitCategoryId = row.Source is SmartCon.Core.Models.FamilyManager.FamilyImportSource.SystemSource sysSource
            ? sysSource.CategoryId
            : (int?)null;

        var renameTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(NameChangeDebounceMs, token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;

                using var _scope = SmartConLogger.BeginScope("FMImport",
                    ("Method", nameof(OnRowNameChanged)),
                    ("Row", newName));

                var normalized = FamilyNameNormalizer.Normalize(newName);

                FamilyContentHash? contentHash = null;
                if (rowContentHash is not null && rowHashFormatVersion is not null)
                {
                    contentHash = new FamilyContentHash(
                        rowContentHash, rowHashFormatVersion.Value, rowFamilySource);
                }

                ContentHashDedupResult? dedupResult = null;
                if (_dedupService is not null)
                {
                    dedupResult = await _dedupService
                        .CheckAsync(normalized, contentHash, rowFamilySource, rowRevitCategoryId, token)
                        .ConfigureAwait(false);
                    if (token.IsCancellationRequested) return;
                }

                var existing = _catalogProvider is null
                    ? null
                    : await _catalogProvider
                        .FindByNormalizedNameAsync(normalized, rowFamilySource, token)
                        .ConfigureAwait(false);
                if (token.IsCancellationRequested) return;

                var newStatus = dedupResult?.Status
                    ?? (existing is null
                        ? FamilyBatchImportStatus.New
                        : FamilyBatchImportStatus.Existing);
                var newExistingId = dedupResult?.ExistingCatalogItemId ?? existing?.Id;
                var newExistingVersionLabel = dedupResult?.ExistingVersionLabel ?? existing?.CurrentVersionLabel;
                var newMatchedVersionLabel = dedupResult?.HashMatch?.MatchedVersionLabel;
                var newIsCrossNameDuplicate = dedupResult?.IsCrossNameDuplicate ?? false;
                var newMatchedItemName = dedupResult?.HashMatch?.MatchedItemName;
                var isHashMatch = dedupResult?.HashMatch is not null;
                var newExistingCategoryId = existing?.CategoryId;
                var newExistingCategoryPath = existing?.CategoryPath;

                // Issue #135 defect 2: when dedup matched an item by
                // content hash (ADR-049, possibly under a different name),
                // the category must come from the HASH-MATCHED item — the
                // name lookup above misses for a unique new name and would
                // reset the category to «Без категории» even though the
                // duplicate's category should be preserved.
                if (isHashMatch && dedupResult!.ExistingCatalogItemId is not null && _catalogProvider is not null)
                {
                    try
                    {
                        var hashMatchedItem = await _catalogProvider
                            .GetItemAsync(dedupResult.ExistingCatalogItemId, token)
                            .ConfigureAwait(false);
                        if (hashMatchedItem is not null)
                        {
                            newExistingCategoryId = hashMatchedItem.CategoryId;
                            newExistingCategoryPath = hashMatchedItem.CategoryPath;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        SmartConLogger.Warn(
                            $"BatchImport.NameChange hash-match category lookup failed for '{dedupResult.ExistingCatalogItemId}': {ex.Message} " +
                            $"[Action: проверьте, что БД каталога доступна; категория строки может быть неактуальной]");
                    }
                    if (token.IsCancellationRequested) return;
                }

                // Issue #135 (P1): provenance the rename handler will
                // assign when the row is NOT locked. Locked rows keep
                // their Command/Manual provenance untouched.
                var newAutoProvenance = newStatus switch
                {
                    FamilyBatchImportStatus.Duplicate when isHashMatch => CategoryProvenance.AutoHash,
                    FamilyBatchImportStatus.Existing or FamilyBatchImportStatus.Duplicate => CategoryProvenance.AutoName,
                    _ => CategoryProvenance.None,
                };

                PrecomputedImportTriple? precomputed = null;
                if (_importPrecomputer is not null)
                {
                    // Issue #126: when dedup matched an item by content
                    // hash (possibly under a different name), the triple
                    // must target that item, not the name lookup.
                    precomputed = await _importPrecomputer
                        .BuildPrecomputedTripleAsync(newName, extension, rowFamilySource, dedupResult?.ExistingCatalogItemId, token)
                        .ConfigureAwait(false);
                    if (token.IsCancellationRequested) return;
                }

                _dispatcher.Invoke(() => ApplyNameChangeResult(row, newStatus, newExistingId, newExistingVersionLabel, newExistingCategoryId, newExistingCategoryPath, precomputed, newMatchedVersionLabel, newIsCrossNameDuplicate, newMatchedItemName, newAutoProvenance));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"BatchImport.NameChange lookup failed: {ex.Message} [Action: проверьте, что БД каталога доступна; статус строки может быть неактуальным до Refresh]");
            }
        }, token);

        lock (_pendingNameChangesLock)
        {
            _pendingNameChanges[row] = (cts, renameTask);
        }
        _ = renameTask.ContinueWith(
            _ =>
            {
                lock (_pendingNameChangesLock)
                {
                    if (_pendingNameChanges.TryGetValue(row, out var current)
                        && ReferenceEquals(current.Task, renameTask))
                    {
                        _pendingNameChanges.Remove(row);
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static string ResolveExtensionForRow(FamilyBatchImportRow row)
    {
        // v2.0.0: extension is the file-type component of the
        // precomputed managed path. System-family rows stage .rvt
        // snapshots, loadable rows stage .rfa. We read it from the row
        // (not the catalog) because the row already carries the
        // resolved FamilySource — see FamilyBatchImportRow constructor.
        return row.FamilySource switch
        {
            "system" => ".rvt",
            _ => ".rfa"
        };
    }

    private void ApplyNameChangeResult(
        FamilyBatchImportRow row,
        FamilyBatchImportStatus newStatus,
        string? newExistingId,
        string? newExistingVersionLabel,
        string? newExistingCategoryId,
        string? newExistingCategoryPath,
        PrecomputedImportTriple? precomputed,
        string? matchedVersionLabel = null,
        bool isCrossNameDuplicate = false,
        string? matchedItemName = null,
        CategoryProvenance newAutoProvenance = CategoryProvenance.None)
    {
        if (row.Status != newStatus)
        {
            row.Status = newStatus;
        }
        row.ExistingCatalogItemId = newExistingId;
        row.ExistingVersionLabel = newExistingVersionLabel;
        row.MatchedVersionLabel = matchedVersionLabel;
        // #180: a rename re-dedup resolves the version by content hash only
        // (the ES marker is not consulted at the dialog level) — the shown
        // label is hash-originated again, so the marker-origin annotation
        // must not survive.
        row.IsMarkerResolvedVersion = false;
        row.IsCrossNameDuplicate = isCrossNameDuplicate;
        row.MatchedItemName = matchedItemName;

        // Issue #135 (P2): the existing item's REAL category always
        // follows the lookup — even for locked rows — so the move-warning
        // can compare it against the locked target category.
        row.ExistingCategoryId = newExistingCategoryId;
        row.ExistingCategoryPath = newExistingCategoryPath;

        if (!row.TargetCategoryIsManual)
        {
            var previousCategoryId = row.TargetCategoryId;
            if ((newStatus == FamilyBatchImportStatus.Existing || newStatus == FamilyBatchImportStatus.Duplicate) && newExistingId is not null)
            {
                // Provenance BEFORE the path: the CategoryChanged
                // batch-apply (fired by the path setter) must observe the
                // row's new provenance to decide about locked targets.
                row.CategoryProvenance = newAutoProvenance;
                row.TargetCategoryId = newExistingCategoryId;
                row.TargetCategoryPath = !string.IsNullOrWhiteSpace(newExistingCategoryPath)
                    ? newExistingCategoryPath!
                    : (LanguageManager.GetString(StringLocalization.Keys.FM_NoCategory) ?? "Без категории");
            }
            else
            {
                row.CategoryProvenance = CategoryProvenance.None;
                row.TargetCategoryId = null;
                row.TargetCategoryPath = LanguageManager.GetString(StringLocalization.Keys.FM_NoCategory) ?? "Без категории";
            }

            // Issue #135 (P4): make the silent auto-change visible — the
            // view flashes the category cell on every token increment.
            if (!string.Equals(previousCategoryId, row.TargetCategoryId, StringComparison.Ordinal))
            {
                row.CategoryFlashToken++;
                SmartConLogger.Debug(
                    $"BatchImport.Category: '{row.FileName}' auto-category " +
                    $"'{previousCategoryId ?? "<none>"}' -> '{row.TargetCategoryId ?? "<none>"}' " +
                    $"(status={newStatus}, provenance={row.CategoryProvenance})");
            }

            // #241: re-evaluate the assignment rules with the new name —
            // an automatic-provenance row may get its category back (or
            // lose it, or become ambiguous). A matched category fires the
            // row's CategoryChanged chain (gate revalidation included).
            // Runs on the UI thread: ApplyNameChangeResult is invoked via
            // _dispatcher.
            TryAutoAssignRow(row);
        }
        else
        {
            SmartConLogger.Debug(
                $"BatchImport.Category: '{row.FileName}' rename kept locked category " +
                $"'{row.TargetCategoryId ?? "<none>"}' (status={newStatus}, provenance={row.CategoryProvenance})");

            // #241: a locked row keeps its category, but the RULES'
            // recommendation still follows the new name — the category
            // column's warning icon must reflect the renamed family.
            TryAutoAssignRow(row);
        }

        if (row.ShowCategoryMoveWarning)
        {
            SmartConLogger.Debug(
                $"BatchImport.Category: '{row.FileName}' will MOVE existing item from " +
                $"'{row.ExistingCategoryPath ?? row.ExistingCategoryId}' to '{row.TargetCategoryPath}' on import");
        }

        if (precomputed is not null)
        {
            row.PrecomputedCatalogItemId = precomputed.CatalogItemId;
            row.PrecomputedVersionLabel = precomputed.VersionLabel;
            row.PrecomputedManagedPath = precomputed.ManagedPath;
        }
        else
        {
            row.PrecomputedCatalogItemId = null;
            row.PrecomputedVersionLabel = null;
            row.PrecomputedManagedPath = null;
        }

        // E2 (#209): a rename may flip a dependency row into/out of the
        // outdated-nested state — recompute the parents' import blocks.
        if (row.DependencyLinks is not null)
        {
            RefreshDependencyIndicators();
        }
    }

    private void ApplyActionToSelection(FamilyBatchImportRow source, FamilyBatchImportAction newValue)
    {
        if (_batchApplying) return;
        _batchApplying = true;
        try
        {
            foreach (var target in GetOtherSelectedRows(source))
            {
                if (target.AvailableActions.Contains(newValue))
                {
                    target.Action = newValue;
                }
                else
                {
                    SmartCon.Core.Logging.SmartConLogger.Debug(
                        $"BatchImport.Action: skip apply {newValue} to '{target.FileName}' — not in AvailableActions");
                }
            }
        }
        finally
        {
            _batchApplying = false;
        }
    }

    private void ApplyCategoryToSelection(FamilyBatchImportRow source, string? id, string path)
    {
        if (_batchApplying) return;
        _batchApplying = true;
        var applied = 0;
        var skippedLocked = 0;
        try
        {
            foreach (var target in GetOtherSelectedRows(source))
            {
                // Issue #135 defect 3: an AUTOMATIC change (rename
                // re-derivation) must not clobber a locked category on
                // other selected rows; an explicit user choice (picker,
                // provenance Manual) applies to everyone, locks included.
                if (target.TargetCategoryIsManual && !source.TargetCategoryIsManual)
                {
                    skippedLocked++;
                    continue;
                }
                target.CategoryProvenance = source.CategoryProvenance;
                target.TargetCategoryId = id;
                target.TargetCategoryPath = path;
                applied++;
            }
        }
        finally
        {
            _batchApplying = false;
        }
        if (applied > 0 || skippedLocked > 0)
        {
            SmartConLogger.Debug(
                $"BatchImport.Category: batch-applied '{path}' to {applied} row(s) from '{source.FileName}' " +
                $"(provenance={source.CategoryProvenance}, skippedLocked={skippedLocked})");
        }
    }

    private List<FamilyBatchImportRow> GetOtherSelectedRows(FamilyBatchImportRow source)
    {
        // Exclude the source so the setter isn't fired twice (it would
        // still be idempotent but would emit an extra PropertyChanged and
        // a redundant UpdateCanImport cycle). The Count <= 1 fast-path
        // also covers the single-row selection case — when the user
        // changes Action on a single selected row there is nothing to
        // batch-apply.
        if (_selectedRows.Count <= 1) return new List<FamilyBatchImportRow>();
        return _selectedRows.Where(r => !ReferenceEquals(r, source)).ToList();
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
