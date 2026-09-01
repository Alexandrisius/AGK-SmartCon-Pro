using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;

namespace SmartCon.UI.Converters;

/// <summary>
/// #210: single source of truth for the localized status-tooltip texts
/// (stale reason, stale count roll-up, type presence). The XAML converters
/// delegate here, and view-models reuse the same texts when building
/// <c>StatusNotice</c> explanations for the clickable-badge details
/// dialog — no duplicated key switches.
/// </summary>
public static class StatusTooltipText
{
    public static string? ForStaleReason(StaleReason reason)
    {
        var key = reason switch
        {
            StaleReason.NoEntityStorage => StringLocalization.Keys.FM_StaleTooltipNoES,
            StaleReason.VersionMismatch => StringLocalization.Keys.FM_StaleTooltipMismatch,
            StaleReason.RevitVersionMismatch => StringLocalization.Keys.FM_StaleTooltipRevitVer,
            StaleReason.NotInCatalog => StringLocalization.Keys.FM_StaleTooltipNotInCatalog,
            StaleReason.ContentDrift => StringLocalization.Keys.FM_StaleTooltipContentDrift,
            StaleReason.RoutingDrift => StringLocalization.Keys.FM_StaleTooltipRoutingDrift,
            _ => StringLocalization.Keys.FM_StaleTooltipNone,
        };
        return LanguageManager.GetString(key);
    }

    public static string? ForStaleCount(int count)
    {
        if (count <= 0) return null;
        return count == 1
            ? LanguageManager.GetString(StringLocalization.Keys.FM_StaleCategoryTooltipOne)
            : LocalizationService.Format(StringLocalization.Keys.FM_StaleCategoryTooltipMany, count);
    }

    public static string? ForPresenceState(TypePresenceState state)
    {
        var key = state switch
        {
            TypePresenceState.InProject => StringLocalization.Keys.FM_PresenceTooltipInProject,
            TypePresenceState.StaleInProject => StringLocalization.Keys.FM_PresenceTooltipStale,
            _ => StringLocalization.Keys.FM_PresenceTooltipNotInProject,
        };
        return LanguageManager.GetString(key);
    }
}
