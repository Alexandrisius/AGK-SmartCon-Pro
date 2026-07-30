using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Transactions;

/// <summary>
/// Реализация ITransactionService (I-03).
/// Каждая транзакция оборачивается в using, подключает SmartConFailurePreprocessor (I-07).
/// </summary>
/// <remarks>
/// Issue #178: <c>Transaction.Commit()</c> возвращает <see cref="TransactionStatus"/>
/// и МОЖЕТ молча откатить транзакцию (<see cref="TransactionStatus.RolledBack"/>)
/// без исключения — когда Revit постит error-failure (например, «нельзя удалить
/// последний тип системной семьи»). Статус проверяется всегда: молчаливый откат
/// превращается в <c>false</c> + Warn, иначе вызывающий код считал бы запись
/// успешной при фактически неизменённом документе.
/// </remarks>
public sealed class RevitTransactionService : ITransactionService
{
    private readonly IRevitContext _revitContext;

    public RevitTransactionService(IRevitContext revitContext)
    {
        _revitContext = revitContext;
    }

    public bool RunInTransaction(string name, Action<Document> action)
    {
        return RunInTransaction(_revitContext.GetDocument(), name, action);
    }

    public bool RunInTransaction(Document document, string name, Action<Document> action)
    {
        using var transaction = new Transaction(document, name);

        var options = transaction.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(new SmartConFailurePreprocessor());
        transaction.SetFailureHandlingOptions(options);

        try
        {
            transaction.Start();
            action(document);
            var status = transaction.Commit();
            if (status != TransactionStatus.Committed)
            {
                SmartConLogger.Warn(
                    $"Transaction '{name}' was silently rolled back by Revit " +
                    $"(Commit status: {status}) — the document was NOT modified. " +
                    "[Action: check the operation's preconditions (e.g. deleting " +
                    "the last type of a system family is forbidden) and retry]");
                return false;
            }
            return true;
        }
        catch
        {
            if (transaction.HasStarted())
            {
                transaction.RollBack();
            }

            throw;
        }
    }

    public bool RunInTransactionGroup(string name, Action<Document> action)
    {
        var doc = _revitContext.GetDocument();

        using var group = new TransactionGroup(doc, name);

        try
        {
            group.Start();
            action(doc);
            group.Assimilate();
            return true;
        }
        catch
        {
            if (group.HasStarted())
            {
                group.RollBack();
            }

            throw;
        }
    }

    public ITransactionGroupSession BeginGroupSession(string name)
    {
        return new RevitTransactionGroupSession(_revitContext, name);
    }

    public T? RunAndRollback<T>(string name, Func<Document, T> action) where T : struct
    {
        var doc = _revitContext.GetDocument();

        using var transaction = new Transaction(doc, name);
        var options = transaction.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(new SmartConFailurePreprocessor());
        transaction.SetFailureHandlingOptions(options);

        try
        {
            transaction.Start();
            var result = action(doc);
            transaction.RollBack();
            return result;
        }
        catch
        {
            if (transaction.HasStarted())
                transaction.RollBack();
            return default;
        }
    }

    public bool RunAndRollback(string name, Action<Document> action)
    {
        var result = RunAndRollback<bool>(name, doc =>
        {
            action(doc);
            return true;
        });
        return result.HasValue && result.Value;
    }
}
