using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyBatchImportRow
{
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
}
