using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Transactions;

/// <summary>
/// Долгоживущая TransactionGroup для сессии PipeConnectEditor (Phase 8, ADR-003).
/// Группа остаётся открытой между вызовами ExternalEventHandler.Execute(),
/// позволяя пользователю интерактивно примерять фитинги и повороты.
/// Вызывать только из Revit main thread (I-01).
/// </summary>
public sealed class RevitTransactionGroupSession : ITransactionGroupSession
{
    private readonly TransactionGroup _group;
    private readonly Document _doc;
    private readonly string _name;
    private bool _disposed;

    internal RevitTransactionGroupSession(Document doc, string name)
    {
        _doc   = doc;
        _name  = name;
        _group = new TransactionGroup(doc, name);
        _group.Start();
        SmartConLogger.Info($"[TxGroup] Начата группа '{name}'");
    }

    public bool IsActive => !_disposed && _group.HasStarted();

    public void RunInTransaction(string name, Action<Document> action)
    {
        if (!IsActive)
            throw new InvalidOperationException("TransactionGroup сессия уже завершена.");

        SmartConLogger.Info($"[TxGroup] Транзакция '{name}' внутри '{_name}'");
        using var tx = new Transaction(_doc, name);
        var options = tx.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(new SmartConFailurePreprocessor());
        tx.SetFailureHandlingOptions(options);

        try
        {
            tx.Start();
            action(_doc);
            tx.Commit();
            SmartConLogger.Info($"[TxGroup] Транзакция '{name}' закоммичена");
        }
        catch
        {
            if (tx.HasStarted()) tx.RollBack();
            SmartConLogger.Info($"[TxGroup] Транзакция '{name}' откачена");
            throw;
        }
    }

    public void Assimilate()
    {
        if (!IsActive) return;
        _group.Assimilate();
        _disposed = true;
        SmartConLogger.Info($"[TxGroup] Группа '{_name}' ассимилирована (одна undo-запись)");
    }

    public void RollBack()
    {
        if (!IsActive) return;
        _group.RollBack();
        _disposed = true;
        SmartConLogger.Info($"[TxGroup] Группа '{_name}' откачена (полный откат)");
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_group.HasStarted())
        {
            _group.RollBack();
            SmartConLogger.Info($"[TxGroup] Группа '{_name}' откачена в Dispose (безопасный откат)");
        }
        _group.Dispose();
        _disposed = true;
    }
}
