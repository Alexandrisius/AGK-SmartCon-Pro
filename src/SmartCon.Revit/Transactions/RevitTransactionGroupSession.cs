using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Transactions;

public sealed class RevitTransactionGroupSession : ITransactionGroupSession
{
    private readonly TransactionGroup _group;
    private readonly IRevitContext _revitContext;
    private readonly string _name;
    private bool _disposed;

    internal RevitTransactionGroupSession(IRevitContext revitContext, string name)
    {
        _revitContext = revitContext;
        _name = name;
        var doc = revitContext.GetDocument();
        _group = new TransactionGroup(doc, name);
        _group.Start();
        SmartConLogger.Info($"[TxGroup] Group started '{name}'");
    }

    public bool IsActive => !_disposed && _group.HasStarted();

    public void RunInTransaction(string name, Action<Document> action)
    {
        if (!IsActive)
            throw new InvalidOperationException("TransactionGroup сессия уже завершена.");

        SmartConLogger.Info($"[TxGroup] Transaction '{name}' inside '{_name}'");
        var doc = _revitContext.GetDocument();
        using var tx = new Transaction(doc, name);
        var options = tx.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(new SmartConFailurePreprocessor());
        tx.SetFailureHandlingOptions(options);

        try
        {
            tx.Start();
            action(doc);
            tx.Commit();
            SmartConLogger.Info($"[TxGroup] Transaction '{name}' committed");
        }
        catch
        {
            if (tx.HasStarted()) tx.RollBack();
            SmartConLogger.Info($"[TxGroup] Transaction '{name}' rolled back");
            throw;
        }
    }

    public void Assimilate()
    {
        if (!IsActive) return;
        _group.Assimilate();
        _disposed = true;
        SmartConLogger.Info($"[TxGroup] Group '{_name}' assimilated (single undo record)");
    }

    public void RollBack()
    {
        if (!IsActive) return;
        _group.RollBack();
        _disposed = true;
        SmartConLogger.Info($"[TxGroup] Group '{_name}' rolled back (full rollback)");
    }

    public void Dispose()
    {
        if (_disposed) return;
        try
        {
            if (_group.HasStarted())
            {
                _group.RollBack();
                SmartConLogger.Info($"[TxGroup] Group '{_name}' rolled back in Dispose (safe rollback)");
            }
        }
        finally
        {
            _group.Dispose();
            _disposed = true;
        }
    }
}
