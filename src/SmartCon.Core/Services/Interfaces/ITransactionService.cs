using Autodesk.Revit.DB;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// All model changes must go through this interface.
/// Creating new Transaction(doc) directly is forbidden (I-03).
/// </summary>
public interface ITransactionService
{
    /// <summary>
    /// Starts a Transaction on the active document, executes action, commits.
    /// On exception — rolls back. Returns false on failure.
    /// </summary>
    bool RunInTransaction(string name, Action<Document> action);

    /// <summary>
    /// Starts a Transaction on the supplied document, executes action, commits.
    /// Use for documents other than the active one (e.g. freshly created project
    /// documents from <c>Application.NewProjectDocument</c>).
    /// </summary>
    bool RunInTransaction(Document document, string name, Action<Document> action);

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

    /// <summary>
    /// Opens a transaction scope with explicit commit/rollback control.
    /// Needed by the batch job engine: one transaction per slice, where the
    /// decision to commit or roll back depends on per-item results (J-03).
    /// The scope must be disposed; disposing an open scope rolls it back.
    /// </summary>
    ITransactionScope BeginTransaction(Document document, string name);
}

/// <summary>
/// Explicit transaction control (I-03 compliant: created only by
/// <see cref="ITransactionService"/>). Always commit or roll back before dispose.
/// </summary>
public interface ITransactionScope : IDisposable
{
    bool IsActive { get; }

    /// <summary>Commits and returns the real Revit status (RolledBack is possible without an exception).</summary>
    TransactionStatus Commit();

    void RollBack();
}
