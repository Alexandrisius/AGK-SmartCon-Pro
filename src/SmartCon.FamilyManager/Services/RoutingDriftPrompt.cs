using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.UI;

namespace SmartCon.FamilyManager.Services;

/// <summary>
/// ADR-072 World B: confirmation dialog shown when placing a system type
/// whose live routing differs from the catalog links — the catalog import
/// changes the project's routing settings, and the user must know it.
/// </summary>
internal sealed class RoutingDriftPrompt : IRoutingDriftPrompt
{
    private readonly IFamilyManagerDialogService _dialogs;

    public RoutingDriftPrompt(IFamilyManagerDialogService dialogs)
    {
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
    }

    public bool ConfirmRoutingOverwrite(string typeName)
        => _dialogs.ShowConfirmation(
            LanguageManager.GetString(StringLocalization.Keys.FM_RoutingDriftTitle) ?? "Routing",
            string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_RoutingDriftBody) ?? "{0}",
                typeName));
}
