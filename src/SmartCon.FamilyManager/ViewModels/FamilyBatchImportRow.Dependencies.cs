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
}
