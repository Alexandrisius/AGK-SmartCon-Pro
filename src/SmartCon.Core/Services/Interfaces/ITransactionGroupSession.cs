using Autodesk.Revit.DB;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Long-lived TransactionGroup session for modal operations (Phase 8: PipeConnectEditor).
/// The group stays open while the user works in the editor window.
/// "Connect" -> Assimilate() — single Undo record. "Cancel" -> RollBack() — full rollback (I-04).
/// Created via ITransactionService.BeginGroupSession().
/// Call only from ExternalEventHandler.Execute() (I-01).
/// </summary>
public interface ITransactionGroupSession : IDisposable
{
    /// <summary>
    /// True — TransactionGroup is open and active.
    /// </summary>
    bool IsActive { get; }

    /// <summary>
    /// Execute a Transaction inside the open group.
    /// </summary>
    void RunInTransaction(string name, Action<Document> action);

    /// <summary>
    /// Merge all nested transactions into a single Undo record and close the group.
    /// </summary>
    void Assimilate();

    /// <summary>
    /// Roll back all group changes and close it.
    /// </summary>
    void RollBack();
}
