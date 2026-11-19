using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Opt-in lifecycle hooks for tools that cannot use the project transaction
/// pipeline (family documents: EditFamily/LoadFamily must run outside it).
/// The defaults keep all existing tools on the old path.
/// </summary>
public abstract partial class AgentWriteToolBase
{
    /// <summary>
    /// Tools that own the document lifecycle (family documents: EditFamily must run
    /// outside any project transaction) return true and implement
    /// <see cref="ExecuteOutsideTransaction"/>. Default false keeps the standard
    /// dry-run/apply transaction pipeline for every existing tool.
    /// </summary>
    protected virtual bool ManagesOwnTransactions => false;

    /// <summary>
    /// Runs the whole operation on the Revit thread when
    /// <see cref="ManagesOwnTransactions"/> is true. The implementation must set
    /// <see cref="WriteOutcome.ModelChanged"/> itself.
    /// </summary>
    protected virtual void ExecuteOutsideTransaction(
        Document document,
        JsonElement args,
        bool dryRun,
        WriteOutcome outcome,
        CancellationToken ct) =>
        throw new InvalidOperationException(
            "The tool declares its own transaction pipeline but does not implement it.");

    /// <summary>
    /// Post-transaction hook that runs outside the transaction, on the Revit thread.
    /// Used by undo_last to restore family documents, which cannot be loaded back
    /// while a project transaction is open. Default no-op.
    /// </summary>
    protected virtual void AfterTransaction(
        Document document,
        JsonElement args,
        bool dryRun,
        WriteOutcome outcome,
        CancellationToken ct)
    {
    }

}
