using Autodesk.Revit.DB;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Transactions;

/// <summary>
/// Реализация ITransactionService (I-03).
/// Каждая транзакция оборачивается в using, подключает SmartConFailurePreprocessor (I-07).
/// </summary>
public sealed class RevitTransactionService : ITransactionService
{
    private readonly IRevitContext _revitContext;

    public RevitTransactionService(IRevitContext revitContext)
    {
        _revitContext = revitContext;
    }

    public bool RunInTransaction(string name, Action<Document> action)
    {
        var doc = _revitContext.GetDocument();

        using var transaction = new Transaction(doc, name);

        var options = transaction.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(new SmartConFailurePreprocessor());
        transaction.SetFailureHandlingOptions(options);

        try
        {
            transaction.Start();
            action(doc);
            transaction.Commit();
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
        var doc = _revitContext.GetDocument();
        return new RevitTransactionGroupSession(doc, name);
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
