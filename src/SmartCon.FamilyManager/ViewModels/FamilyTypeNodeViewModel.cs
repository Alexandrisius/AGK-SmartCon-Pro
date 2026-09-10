using CommunityToolkit.Mvvm.ComponentModel;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyTypeNodeViewModel : CatalogTreeNodeViewModel
{
    public override bool IsCategory => false;
    public override bool IsType => true;

    public string CatalogItemId { get; }
    public string TypeName { get; }
    public bool IsVirtual { get; }
    public string FamilySource { get; }
    public string? UniqueId { get; }
    public bool IsUnavailable { get; }

    /// <summary>
    /// Issue #183: Revit system family of the type (from
    /// <c>family_types.family_name</c>) — the sync identity is
    /// (family, name). null for legacy rows and loadable types.
    /// </summary>
    public string? FamilyName { get; }

    /// <summary>
    /// Issue #190 (ADR-064): locale-invariant family identity (from
    /// <c>family_types.family_key</c>) — preferred over
    /// <see cref="FamilyName"/> for presence/stale matching. null for
    /// legacy rows (pre-V27) and loadable types.
    /// </summary>
    public string? FamilyKey { get; }

    /// <summary>
    /// Issue #203: display badge distinguishing system family kinds (duct
    /// profile shape, fittings presence, wall kind, stairs construction
    /// method) — same-named types of different families are otherwise
    /// visually identical in the tree. None for loadable, single-family,
    /// unknown and legacy (pre-V27) rows.
    /// </summary>
    public SystemFamilyBadge Badge => SystemFamilyBadgeMap.Resolve(FamilyKey);

    /// <summary>
    /// Issue #203: whether the badge cell takes layout space at all — the
    /// tree template collapses the whole badge on badge-less rows
    /// (loadable/single-family/legacy), so the name keeps its normal
    /// offset next to the presence dot.
    /// </summary>
    public bool HasBadge => Badge != SystemFamilyBadge.None;

    /// <summary>
    /// Issue #203: hover text for the family badge — the localized kind
    /// caption plus the family name (the badge itself is glyph-only).
    /// null when <see cref="Badge"/> is None.
    /// </summary>
    public string? BadgeTooltip
    {
        get
        {
            var label = Badge switch
            {
                SystemFamilyBadge.ShapeRound => Text(SmartCon.UI.StringLocalization.Keys.FM_FamilyBadge_Round, "Круглое сечение"),
                SystemFamilyBadge.ShapeRectangular => Text(SmartCon.UI.StringLocalization.Keys.FM_FamilyBadge_Rectangular, "Прямоугольное сечение"),
                SystemFamilyBadge.ShapeOval => Text(SmartCon.UI.StringLocalization.Keys.FM_FamilyBadge_Oval, "Овальное сечение"),
                SystemFamilyBadge.Fittings => Text(SmartCon.UI.StringLocalization.Keys.FM_FamilyBadge_WithFittings, "С соединительными деталями"),
                SystemFamilyBadge.FittingsNone => Text(SmartCon.UI.StringLocalization.Keys.FM_FamilyBadge_WithoutFittings, "Без соединительных деталей"),
                SystemFamilyBadge.WallBasic => Text(SmartCon.UI.StringLocalization.Keys.FM_FamilyBadge_WallBasic, "Основная стена"),
                SystemFamilyBadge.WallStacked => Text(SmartCon.UI.StringLocalization.Keys.FM_FamilyBadge_WallStacked, "Составная стена"),
                SystemFamilyBadge.WallCurtain => Text(SmartCon.UI.StringLocalization.Keys.FM_FamilyBadge_WallCurtain, "Витраж"),
                SystemFamilyBadge.StairsAssembled => Text(SmartCon.UI.StringLocalization.Keys.FM_FamilyBadge_StairsAssembled, "Смонтированная лестница"),
                SystemFamilyBadge.StairsCastInPlace => Text(SmartCon.UI.StringLocalization.Keys.FM_FamilyBadge_StairsCastInPlace, "Монолитная лестница"),
                SystemFamilyBadge.StairsPrecast => Text(SmartCon.UI.StringLocalization.Keys.FM_FamilyBadge_StairsPrecast, "Сборная лестница"),
                _ => null,
            };
            if (label is null)
                return null;
            return string.IsNullOrEmpty(FamilyName) ? label : $"{label} · {FamilyName}";
        }
    }

    private static string Text(string key, string fallback)
        => SmartCon.UI.LanguageManager.GetString(key) ?? fallback;

    /// <summary>
    /// #187: the type is present in the ACTIVE project (same family + name
    /// found in the document). Computed on every tree load via one
    /// CollectTypes pass over the system categories of the catalog.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PresenceState))]
    private bool _isInProject;

    /// <summary>
    /// #187: the type is present in the ACTIVE project AND its ES marker
    /// does not match the catalog (outdated) — the orange dot. Set for
    /// system types from the per-type stale map and mirrored onto loadable
    /// type nodes from the leaf verdict (ADR-063 §3).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PresenceState))]
    private bool _isStaleInProject;

    /// <summary>#187: tri-state presence for the dot indicator.</summary>
    public TypePresenceState PresenceState =>
        !IsInProject
            ? TypePresenceState.NotInProject
            : IsStaleInProject
                ? TypePresenceState.StaleInProject
                : TypePresenceState.InProject;

    // ── Clickable presence dot (#210) ──────────────────────────────────
    // The dot is a placement shortcut: click = the same place-type flow as
    // DnD / context menu, with freshness semantics per color (grey = load
    // and place, blue = place, orange = update then place). The tooltip
    // states exactly that action — no status dialog for this tiny state.

    /// <summary>Action label of the dot click for the current <see cref="PresenceState"/>.</summary>
    public string PresenceBadgeTooltip =>
        PresenceState switch
        {
            TypePresenceState.NotInProject =>
                SmartCon.UI.LanguageManager.GetString(SmartCon.UI.StringLocalization.Keys.FM_PlaceType_DotLoadPlace)
                    ?? "Загрузить и разместить тип",
            TypePresenceState.StaleInProject =>
                SmartCon.UI.LanguageManager.GetString(SmartCon.UI.StringLocalization.Keys.FM_PlaceType_DotUpdatePlace)
                    ?? "Обновить и разместить",
            _ =>
                SmartCon.UI.LanguageManager.GetString(SmartCon.UI.StringLocalization.Keys.FM_PlaceType)
                    ?? "Разместить тип",
        };

    partial void OnIsInProjectChanged(bool value)
    {
        OnPropertyChanged(nameof(PresenceBadgeTooltip));
    }

    partial void OnIsStaleInProjectChanged(bool value)
    {
        OnPropertyChanged(nameof(PresenceBadgeTooltip));
    }

    public bool IsSystemType =>
        FamilySource == "system" || (UniqueId is not null && UniqueId.Length > 0);

    public FamilyTypeNodeViewModel(
        string catalogItemId,
        string typeName,
        bool isVirtual = false,
        string familySource = "loadable",
        string? uniqueId = null,
        string? displayName = null,
        bool isUnavailable = false,
        string? familyName = null,
        string? familyKey = null)
    {
        CatalogItemId = catalogItemId;
        TypeName = typeName;
        IsVirtual = isVirtual;
        FamilySource = familySource;
        UniqueId = uniqueId;
        IsUnavailable = isUnavailable;
        FamilyName = familyName;
        FamilyKey = familyKey;
        DisplayName = displayName ?? typeName;
    }
}
