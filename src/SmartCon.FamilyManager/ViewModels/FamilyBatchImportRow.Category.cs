using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyBatchImportRow
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCategoryMoveWarning))]
    [NotifyPropertyChangedFor(nameof(CategoryMoveWarningTooltip))]
    [NotifyPropertyChangedFor(nameof(ShowRuleConflictIcon))]
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
    /// Issue #261: the user explicitly picked «Без категории» in the
    /// picker — the import will MOVE the existing item to no category
    /// (write NULL), not just "not assign one". Copied to
    /// <c>FamilyBatchImportItem.ClearCategoryOnImport</c> by GetResultItems.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCategoryMoveWarning))]
    [NotifyPropertyChangedFor(nameof(CategoryMoveWarningTooltip))]
    private bool _clearCategoryOnImport;

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
    /// existing family between categories. Issue #261: an explicit
    /// «Без категории» pick (move to no category) warns the same way.
    /// </summary>
    public bool ShowCategoryMoveWarning =>
        ((TargetCategoryIsManual
                && !string.IsNullOrEmpty(TargetCategoryId)
                && !string.IsNullOrEmpty(ExistingCategoryId)
                && TargetCategoryId != ExistingCategoryId)
            || (ClearCategoryOnImport
                && !string.IsNullOrEmpty(ExistingCategoryId)))
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
}
