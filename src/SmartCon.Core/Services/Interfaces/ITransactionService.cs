using Autodesk.Revit.DB;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Все изменения модели — только через этот интерфейс.
/// Создание new Transaction(doc) напрямую запрещено (I-03).
/// </summary>
public interface ITransactionService
{
    /// <summary>
    /// Запускает Transaction, выполняет action, делает Commit.
    /// При исключении — Rollback. Возвращает false при неудаче.
    /// </summary>
    bool RunInTransaction(string name, Action<Document> action);

    /// <summary>
    /// Запускает TransactionGroup. Используется для одноразовых операций (одна запись Undo).
    /// </summary>
    bool RunInTransactionGroup(string name, Action<Document> action);

    /// <summary>
    /// Открывает долгоживущую TransactionGroup и возвращает сессию.
    /// Сессия остаётся открытой пока не будет вызван Assimilate() или RollBack().
    /// Используется для Phase 8: PipeConnectEditor (модальное окно + серия операций).
    /// Вызывать только из ExternalEventHandler.Execute() (I-01).
    /// </summary>
    ITransactionGroupSession BeginGroupSession(string name);
}
