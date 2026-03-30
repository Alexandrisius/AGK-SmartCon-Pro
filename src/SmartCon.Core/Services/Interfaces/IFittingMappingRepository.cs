using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// CRUD для правил маппинга фитингов. Хранение в JSON (AppData).
/// Реализация: SmartCon.Core/Services/Implementation/JsonFittingMappingRepository.cs
/// </summary>
public interface IFittingMappingRepository
{
    IReadOnlyList<ConnectorTypeDefinition> GetConnectorTypes();
    void SaveConnectorTypes(IReadOnlyList<ConnectorTypeDefinition> types);

    IReadOnlyList<FittingMappingRule> GetMappingRules();
    void SaveMappingRules(IReadOnlyList<FittingMappingRule> rules);

    string GetStoragePath();
}
