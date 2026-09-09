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
}
