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
    /// <summary>All configured connector type definitions.</summary>
    IReadOnlyList<ConnectorTypeDefinition> GetConnectorTypes();

    /// <summary>Persist connector type definitions.</summary>
    void SaveConnectorTypes(IReadOnlyList<ConnectorTypeDefinition> types);

    /// <summary>All fitting mapping rules.</summary>
    IReadOnlyList<FittingMappingRule> GetMappingRules();

    /// <summary>Persist fitting mapping rules.</summary>
    void SaveMappingRules(IReadOnlyList<FittingMappingRule> rules);

    /// <summary>File path of the JSON storage (for import/export).</summary>
    string GetStoragePath();
}
