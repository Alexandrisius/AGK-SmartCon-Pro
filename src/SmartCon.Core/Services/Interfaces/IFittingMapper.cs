using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Search for suitable fittings by connector types.
/// Implementation: SmartCon.Core/Services/Implementation/FittingMapper.cs
/// </summary>
public interface IFittingMapper
{
    /// <summary>
    /// Priority-ordered list of matching mapping rules.
    /// </summary>
    IReadOnlyList<FittingMappingRule> GetMappings(
        ConnectionTypeCode from, ConnectionTypeCode to);

    /// <summary>
    /// Minimum chain of fittings through intermediate types (Dijkstra algorithm).
    /// Empty list = connection not possible.
    /// </summary>
    IReadOnlyList<FittingMappingRule> FindShortestFittingPath(
        ConnectionTypeCode from, ConnectionTypeCode to);

    /// <summary>Load mapping rules from a JSON file.</summary>
    void LoadFromFile(string jsonPath);
}
