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
            var rollbackStatus = transaction.RollBack();
            if (rollbackStatus != TransactionStatus.RolledBack)
            {
                // Revit documents that callers must check the status: failure
                // handling can defer or refuse the rollback, and a dry run that
                // silently keeps changes violates "nothing was modified".
                SmartConLogger.Warn(
                    "Dry-run transaction '" + name + "' did not roll back (" + rollbackStatus +
                    ") [Action: re-open the document before applying; a change may have leaked]");
                return default;
            }

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

    public ITransactionScope BeginTransaction(Document document, string name)
    {
        if (document.IsModifiable)
        {
            throw new InvalidOperationException(
                "Cannot begin transaction '" + name + "': the document already has an open " +
                "transaction or transaction group. " +
                "[Action: close the outer transaction before starting a batch slice]");
        }

        var transaction = new Transaction(document, name);
        var options = transaction.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(new SmartConFailurePreprocessor());
        transaction.SetFailureHandlingOptions(options);

        var status = transaction.Start();
        if (status != TransactionStatus.Started)
        {
            transaction.Dispose();
            throw new InvalidOperationException(
                "Transaction '" + name + "' did not start (status: " + status + "). " +
                "[Action: check that the document is not read-only and has no open transaction]");
        }

        return new RevitTransactionScope(transaction, name);
    }

    private sealed class RevitTransactionScope : ITransactionScope
    {
        private readonly Transaction _transaction;
        private readonly string _name;
        private bool _finished;

        public RevitTransactionScope(Transaction transaction, string name)
        {
            _transaction = transaction;
            _name = name;
        }

        public bool IsActive => !_finished && _transaction.HasStarted();

        public TransactionStatus Commit()
        {
            if (_finished)
            {
                throw new InvalidOperationException(
                    "Transaction '" + _name + "' is already finished. " +
                    "[Action: commit or roll back exactly once per scope]");
            }

            _finished = true;
            var status = _transaction.Commit();
            if (status != TransactionStatus.Committed)
            {
                SmartConLogger.Warn(
                    $"Transaction '{_name}' was silently rolled back by Revit " +
                    $"(Commit status: {status}) — the document was NOT modified. " +
                    "[Action: check the operation's preconditions and retry the batch]");
            }

            return status;
        }

        public void RollBack()
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            if (_transaction.HasStarted())
            {
                _transaction.RollBack();
            }
        }

        public void Dispose()
        {
            if (!_finished)
            {
                RollBack();
            }

            _transaction.Dispose();
        }
    }
}
