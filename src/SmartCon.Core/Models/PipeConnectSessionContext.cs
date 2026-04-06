using SmartCon.Core.Math;

namespace SmartCon.Core.Models;

/// <summary>
/// Контекст сессии соединения, передаваемый из PipeConnectCommand в PipeConnectEditorViewModel.
/// Содержит результаты S1-S5 анализа, выполненного ДО открытия TransactionGroup.
/// Иммутабельный — создаётся один раз в PipeConnectCommand.Execute().
/// </summary>
public sealed class PipeConnectSessionContext
{
    public required ConnectorProxy StaticConnector { get; init; }
    public required ConnectorProxy DynamicConnector { get; init; }

    /// <summary>Результат вычисления выравнивания (S3, чистая математика).</summary>
    public required AlignmentResult AlignResult { get; init; }

    /// <summary>Целевой радиус для динамического элемента (S4). null = размеры совпадают.</summary>
    public double? ParamTargetRadius { get; init; }

    /// <summary>S4: ожидается, что потребуется переходник (размер подобран приближённо).</summary>
    public bool ParamExpectNeedsAdapter { get; init; }

    /// <summary>
    /// Подобранные фитинги из маппинга (S5, упорядоченные по Priority).
    /// Пустой список = прямое соединение.
    /// </summary>
    public IReadOnlyList<FittingMappingRule> ProposedFittings { get; init; } = [];

    /// <summary>
    /// Граф цепочки dynamic-элемента, построенный ДО disconnect. null = нет цепочки.
    /// </summary>
    public ConnectionGraph? ChainGraph { get; init; }
}
