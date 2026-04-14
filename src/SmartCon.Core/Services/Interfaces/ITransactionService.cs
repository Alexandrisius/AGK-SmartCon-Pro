using Autodesk.Revit.DB;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// All model changes must go through this interface.
/// Creating new Transaction(doc) directly is forbidden (I-03).
/// </summary>
public interface ITransactionService
{
    /// <summary>
    /// Starts a Transaction, executes action, commits.
    /// On exception — rolls back. Returns false on failure.
    /// </summary>
    bool RunInTransaction(string name, Action<Document> action);

    /// <summary>
    /// Starts a TransactionGroup. Used for one-shot operations (single Undo record).
    /// </summary>
    bool RunInTransactionGroup(string name, Action<Document> action);

    /// <summary>
    /// Opens a long-lived TransactionGroup and returns a session.
    /// The session stays open until Assimilate() or RollBack() is called.
    /// Used for Phase 8: PipeConnectEditor (modal window + series of operations).
    /// Call only from ExternalEventHandler.Execute() (I-01).
    /// </summary>
    ITransactionGroupSession BeginGroupSession(string name);

    /// <summary>
    /// Starts a Transaction, executes action, and always rolls back.
    /// Used for read-only trial operations (e.g., iterating FamilySymbols to read radii).
    /// Returns the result of the action. Returns <c>default</c> on failure.
    /// </summary>
    T? RunAndRollback<T>(string name, Func<Document, T> action) where T : struct;

    /// <summary>
    /// Starts a Transaction, executes action, and always rolls back.
    /// Void overload for side-effect-free trial operations.
    /// </summary>
    bool RunAndRollback(string name, Action<Document> action);
}
