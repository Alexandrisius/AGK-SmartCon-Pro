using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Revit implementation of the import health check. UC-1 pattern: one
/// TransactionGroup (rolled back at the end — the document is never
/// modified) containing one small transaction per family type; each
/// commit triggers the collecting <see cref="IFailuresPreprocessor"/>
/// which swallows warnings from the Revit UI and records every failure
/// against the exact type that caused it (pyRevit Family Quick Check
/// pattern). Error-severity failures roll back their transaction
/// SILENTLY via <c>SetClearAfterRollback(true)</c> — without it Revit
/// shows a cancel-only error dialog per broken type (Autodesk docs:
/// "ProceedWithRollBack — failures will be shown to the user" unless
/// clear-after-rollback is set).
/// <para>
/// Render-thread risk (net48): Regenerate + RollBack on a held-open
/// family document are two of the three triggers documented in
/// <c>af301dd</c>/ADR-042 (#92) for the WPF zombie state — the third,
/// get_Geometry, is deliberately NOT used here (unlike the removed
/// ExtractGeometryPerType). The type-catalog baker (Phase 27B) proves a
/// plain transaction + Regenerate before the dialog works; #95
/// BatchDialogRenderRecovery and #96 BalloonNudge remain the active
/// mitigations on this path. Manual net48 test must cover a 30+ type
/// family batch import (dialog paints, pane does not freeze).
/// </para>
/// </summary>
public sealed class RevitFamilyHealthChecker : IFamilyHealthChecker
{
    private const int MaxIssuesPerReport = 200;

    public FamilyHealthReport CheckFamilyDocument(Document familyDoc, CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope("HealthCheck",
            ("Method", nameof(CheckFamilyDocument)),
            ("Family", familyDoc.Title));

        var issues = new List<FamilyHealthIssue>();
        CollectDocumentWarnings(familyDoc, issues);

        var familyManager = familyDoc.FamilyManager;
        if (familyManager is null)
        {
            SmartConLogger.Warn(
                $"FamilyManager is null for '{familyDoc.Title}' — type-level health check skipped " +
                "[Action: verify the document is a valid family file]");
            return FamilyHealthReport.FromIssues(issues);
        }

        var types = familyManager.Types.Cast<FamilyType>().ToList();
        if (types.Count == 0)
        {
            SmartConLogger.Debug("Family has no types — document warnings only");
            return FamilyHealthReport.FromIssues(issues);
        }

        var collector = new HealthFailureCollector();
        using (var group = new TransactionGroup(familyDoc, "SmartCon Family Health Check"))
        {
            group.Start();
            try
            {
                foreach (var type in types)
                {
                    ct.ThrowIfCancellationRequested();
                    collector.CurrentTypeName = type.Name;
                    CheckSingleType(familyDoc, familyManager, type, collector, issues);
                }
            }
            finally
            {
                collector.CurrentTypeName = null;
                try
                {
                    if (group.HasStarted())
                    {
                        group.RollBack();
                    }
                }
                catch (Exception ex)
                {
                    SmartConLogger.Debug($"Health check TransactionGroup rollback failed: {ex.Message}");
                }
            }
        }

        issues.AddRange(collector.Issues);

        var report = FamilyHealthReport.FromIssues(Deduplicate(issues));
        SmartConLogger.Info(
            $"Health check done: types={types.Count}, issues={report.Issues.Count} " +
            $"(errors={report.Issues.Count(i => i.Severity == FamilyHealthIssueSeverity.Error)}, " +
            $"warnings={report.Issues.Count(i => i.Severity == FamilyHealthIssueSeverity.Warning)}), " +
            $"healthy={report.IsHealthy}");
        return report;
    }

    public FamilyHealthReport CheckActiveFamilyDocument(Document familyDoc)
    {
        using var _scope = SmartConLogger.BeginScope("HealthCheck",
            ("Method", nameof(CheckActiveFamilyDocument)),
            ("Family", familyDoc.Title));

        var issues = new List<FamilyHealthIssue>();
        CollectDocumentWarnings(familyDoc, issues);

        var report = FamilyHealthReport.FromIssues(Deduplicate(issues));
        SmartConLogger.Info(
            $"Active document health check: issues={report.Issues.Count}, healthy={report.IsHealthy}");
        return report;
    }

    private static void CheckSingleType(
        Document familyDoc,
        Autodesk.Revit.DB.FamilyManager familyManager,
        FamilyType type,
        HealthFailureCollector collector,
        List<FamilyHealthIssue> issues)
    {
        using var tx = new Transaction(familyDoc, "SmartCon Health Check Type");
        var options = tx.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(collector);
        options.SetClearAfterRollback(true);
        tx.SetFailureHandlingOptions(options);

        try
        {
            tx.Start();
            familyManager.CurrentType = type;
            familyDoc.Regenerate();
            tx.Commit();
        }
        catch (Exception ex)
        {
            if (tx.HasStarted() && !tx.HasEnded())
            {
                tx.RollBack();
            }

            issues.Add(new FamilyHealthIssue(
                type.Name,
                FamilyHealthIssueSeverity.Error,
                $"{ex.GetType().Name}: {ex.Message}"));

            SmartConLogger.Debug(
                $"Type '{type.Name}' health check threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void CollectDocumentWarnings(Document familyDoc, List<FamilyHealthIssue> issues)
    {
        try
        {
            foreach (var warning in familyDoc.GetWarnings())
            {
                issues.Add(new FamilyHealthIssue(
                    null,
                    FamilyHealthIssueSeverity.Warning,
                    warning.GetDescriptionText()));
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"GetWarnings failed for '{familyDoc.Title}': {ex.Message}");
        }
    }

    private static IReadOnlyList<FamilyHealthIssue> Deduplicate(List<FamilyHealthIssue> issues)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<FamilyHealthIssue>(issues.Count);
        foreach (var issue in issues)
        {
            var key = (issue.TypeName ?? string.Empty) + "|" + issue.Severity + "|" + issue.Description;
            if (seen.Add(key))
            {
                result.Add(issue);
                if (result.Count >= MaxIssuesPerReport)
                {
                    break;
                }
            }
        }

        return result;
    }

    private sealed class HealthFailureCollector : IFailuresPreprocessor
    {
        public string? CurrentTypeName { get; set; }
        public List<FamilyHealthIssue> Issues { get; } = new();

        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            var hasError = false;
            foreach (var failure in failuresAccessor.GetFailureMessages())
            {
                var severity = failure.GetSeverity();
                var description = failure.GetDescriptionText();

                if (severity == FailureSeverity.Warning)
                {
                    Issues.Add(new FamilyHealthIssue(
                        CurrentTypeName, FamilyHealthIssueSeverity.Warning, description));
                    failuresAccessor.DeleteWarning(failure);
                }
                else
                {
                    Issues.Add(new FamilyHealthIssue(
                        CurrentTypeName, FamilyHealthIssueSeverity.Error, description));
                    hasError = true;
                }
            }

            return hasError
                ? FailureProcessingResult.ProceedWithRollBack
                : FailureProcessingResult.Continue;
        }
    }
}
