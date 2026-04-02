using Autodesk.Revit.DB;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Сессия долгоживущей TransactionGroup для модальных операций (Phase 8: PipeConnectEditor).
/// Группа остаётся открытой пока пользователь работает в окне редактора.
/// «Соединить» → Assimilate() — одна запись Undo. «Отмена» → RollBack() — полный откат (I-04).
/// Создаётся через ITransactionService.BeginGroupSession().
/// Вызывать только из ExternalEventHandler.Execute() (I-01).
/// </summary>
public interface ITransactionGroupSession : IDisposable
{
    /// <summary>
    /// True — TransactionGroup открыта и активна.
    /// </summary>
    bool IsActive { get; }

    /// <summary>
    /// Выполнить Transaction внутри открытой группы.
    /// </summary>
    void RunInTransaction(string name, Action<Document> action);

    /// <summary>
    /// Слить все вложенные транзакции в одну Undo-запись и закрыть группу.
    /// </summary>
    void Assimilate();

    /// <summary>
    /// Откатить все изменения группы и закрыть её.
    /// </summary>
    void RollBack();
}
