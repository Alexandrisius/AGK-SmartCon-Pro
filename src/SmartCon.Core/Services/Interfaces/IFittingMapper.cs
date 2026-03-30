using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Поиск подходящих фитингов по типам коннекторов.
/// Реализация: SmartCon.Core/Services/Implementation/FittingMapper.cs
/// </summary>
public interface IFittingMapper
{
    /// <summary>
    /// Упорядоченный по Priority список подходящих маппингов.
    /// </summary>
    IReadOnlyList<FittingMappingRule> GetMappings(
        ConnectionTypeCode from, ConnectionTypeCode to);

    /// <summary>
    /// Минимальная цепочка фитингов через промежуточные типы (алгоритм Дейкстры).
    /// Пустой список = соединение невозможно.
    /// </summary>
    IReadOnlyList<FittingMappingRule> FindShortestFittingPath(
        ConnectionTypeCode from, ConnectionTypeCode to);

    void LoadFromFile(string jsonPath);
}
