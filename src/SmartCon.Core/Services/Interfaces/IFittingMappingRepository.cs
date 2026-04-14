using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// CRUD for fitting mapping rules. Storage in JSON (AppData).
/// Implementation: SmartCon.Core/Services/Implementation/JsonFittingMappingRepository.cs
/// </summary>
public interface IFittingMappingRepository
{
    IReadOnlyList<ConnectorTypeDefinition> GetConnectorTypes();
    void SaveConnectorTypes(IReadOnlyList<ConnectorTypeDefinition> types);

    IReadOnlyList<FittingMappingRule> GetMappingRules();
    void SaveMappingRules(IReadOnlyList<FittingMappingRule> rules);

    string GetStoragePath();
}
