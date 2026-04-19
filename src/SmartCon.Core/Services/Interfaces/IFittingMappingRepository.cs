using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// CRUD for fitting mapping rules (connector types + reducer/fitting rules).
/// Storage: per-project <c>DataStorage</c> in <c>ExtensibleStorage</c> of the active
/// Revit document (ADR-012). Import/Export to JSON is handled manually through the
/// Settings window.
/// Implementation: <c>SmartCon.Revit/Storage/RevitFittingMappingRepository.cs</c>.
/// </summary>
public interface IFittingMappingRepository
{
    IReadOnlyList<ConnectorTypeDefinition> GetConnectorTypes();
    void SaveConnectorTypes(IReadOnlyList<ConnectorTypeDefinition> types);

    IReadOnlyList<FittingMappingRule> GetMappingRules();
    void SaveMappingRules(IReadOnlyList<FittingMappingRule> rules);

    string GetStoragePath();
}
