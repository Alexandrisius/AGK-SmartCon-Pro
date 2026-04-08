using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

public interface IDialogService
{
    ConnectorTypeDefinition? ShowMiniTypeSelector(
        IReadOnlyList<ConnectorTypeDefinition> availableTypes);

    void ShowWarning(string title, string message);

    IReadOnlyList<FittingMapping>? ShowFamilySelector(
        IReadOnlyList<string> availableFamilies,
        IReadOnlyList<FittingMapping> currentSelection);

    bool ShowFittingCtcSetup(
        string familyName,
        string symbolName,
        List<FittingCtcSetupItem> connectors,
        IReadOnlyList<ConnectorTypeDefinition> availableTypes);
}
