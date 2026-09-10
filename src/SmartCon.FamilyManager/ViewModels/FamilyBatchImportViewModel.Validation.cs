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

public sealed partial class FamilyBatchImportViewModel
{
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
}
