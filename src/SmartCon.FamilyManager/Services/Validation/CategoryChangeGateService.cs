using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.ViewModels;
using Keys = SmartCon.UI.StringLocalization.Keys;

namespace SmartCon.FamilyManager.Services.Validation;

/// <summary>
/// Local implementation of the category-change gate. Block UX: the
/// validation report dialog doubles as the block dialog (banner explains
/// the move was cancelled); the "no extraction data" case shows an info
/// dialog pointing at the database update command.
/// <para>
/// The report VM is created DIRECTLY (not via the view-model factory):
/// it is a data-only VM, and depending on the factory would close a
/// circular dependency FamilyManagerViewModelFactory →
/// ICategoryChangeGateService → IFamilyManagerViewModelFactory that
/// crashed the plugin on startup.
/// </para>
/// </summary>
internal sealed class CategoryChangeGateService : ICategoryChangeGateService
{
    private readonly IFamilyImportValidationService _validationService;
    private readonly IFamilyManagerDialogService _dialogService;

    public CategoryChangeGateService(
        IFamilyImportValidationService validationService,
        IFamilyManagerDialogService dialogService)
    {
        _validationService = validationService;
        _dialogService = dialogService;
    }

    public async Task<bool> EnsureFamilyPassesAsync(
        string catalogItemId,
        string familyName,
        string? targetCategoryId,
        string targetCategoryPath,
        CancellationToken ct = default)
    {
        if (targetCategoryId is null)
        {
            return true;
        }

        var rules = await _validationService.GetEffectiveRulesAsync(targetCategoryId, ct);
        if (rules.Count == 0)
        {
            return true;
        }

        var report = await _validationService.ValidateCatalogItemAsync(catalogItemId, rules, ct);
        if (report is null)
        {
            SmartConLogger.Info(
                $"CategoryChangeGate: '{familyName}' → '{targetCategoryPath}' blocked — no extraction data");
            _dialogService.ShowInfo(
                Loc(Keys.FM_Gate_CategoryBlockedNoData_Title, "Категория не может быть применена"),
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    Loc(Keys.FM_Gate_CategoryBlockedNoData_Body,
                        "Семейство «{0}» нельзя переместить в «{1}»: атрибуты не извлечены, проверить правила невозможно.\n\nВыполните «Обновить базу» и повторите."),
                    familyName, targetCategoryPath));
            return false;
        }

        if (report.IsValid)
        {
            return true;
        }

        SmartConLogger.Info(
            $"CategoryChangeGate: '{familyName}' → '{targetCategoryPath}' blocked — " +
            $"{report.Violations.Count} violation(s) of {rules.Count} rule(s)");

        var blockedBanner = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            Loc(Keys.FM_Gate_CategoryBlocked_Banner,
                "Категория «{0}» не может быть применена — семейство не проходит её правила. Перемещение отменено."),
            targetCategoryPath);

        var reportVm = new ValidationReportViewModel(
            familyName, targetCategoryPath, null, report, rules.Count);
        reportVm.BlockedBannerText = blockedBanner;
        _dialogService.ShowValidationReport(reportVm);
        return false;
    }

    private static string Loc(string key, string fallback) =>
        SmartCon.UI.LanguageManager.GetString(key) ?? fallback;
}
