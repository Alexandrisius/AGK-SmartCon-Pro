namespace SmartCon.Core.Models;

/// <summary>
/// Ограничение на значение столбца lookup-таблицы от другого коннектора фитинга.
/// Используется при поиске доступных DN для multi-query size_lookup:
///   size_lookup(Table, target, default, DN1, DN2)
/// Для коннектора DN1 constraints = [ LookupColumnConstraint(connIdx2, "DN2", 20.0) ]
/// → показывать только строки где DN2 ≈ 20 мм.
/// </summary>
public sealed record LookupColumnConstraint(
    int ConnectorIndex,
    string ParameterName,
    double ValueMm
);
