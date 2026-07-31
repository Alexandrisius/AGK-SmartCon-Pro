namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Outcome of a single system-type synchronization (Issue #104). One result
/// per type inside a <see cref="SystemFamilySyncResult"/>.
/// </summary>
public enum SystemTypeSyncStatus
{
    /// <summary>The type did not exist in the project; it was created by
    /// duplicating a prototype type of the same category and writing all
    /// reference data.</summary>
    Created,

    /// <summary>The type existed in the project; its data was overwritten
    /// from the catalog reference.</summary>
    Updated,

    /// <summary>The type was not found in the source mini-project.</summary>
    NotFoundInSource,

    /// <summary>The type does not exist in the project and no prototype type
    /// of the same category is available to duplicate.</summary>
    NoPrototypeType,

    /// <summary>Issue #183: the system FAMILY of the reference type (e.g.
    /// "Conduit without Fittings") does not exist in the project, so no
    /// same-family prototype can be duplicated. A system family cannot be
    /// created via the API — the type is skipped, never created in a foreign
    /// family.</summary>
    FamilyNotFound,

    /// <summary>Synchronization failed (transaction or API error).</summary>
    Failed,
}

/// <summary>
/// Per-type result of a system-type synchronization.
/// </summary>
/// <param name="TypeName">Name of the synchronized type.</param>
/// <param name="Status">Outcome of the synchronization.</param>
/// <param name="ParametersWritten">Number of type parameters written
/// (including cleared values).</param>
/// <param name="ParametersSkipped">Number of source parameters that could
/// not be applied (read-only, missing on target, unresolved ElementId
/// reference, failed <c>Set</c>).</param>
/// <param name="ErrorMessage">Failure details when
/// <see cref="Status"/> is a failure state.</param>
/// <param name="NotConvergedCount">Number of dependency items that could not
/// be fully brought to the catalog reference: unresolved routing rules
/// (missing fitting in project and catalog) and segment sizes that could not
/// be removed/corrected (in use by placed MEP curves). The type still counts
/// as synchronized — the residue is reported to the user.</param>
public sealed record SystemTypeSyncResult(
    string TypeName,
    SystemTypeSyncStatus Status,
    int ParametersWritten,
    int ParametersSkipped,
    string? ErrorMessage = null,
    int NotConvergedCount = 0)
{
    public bool IsSuccess =>
        Status is SystemTypeSyncStatus.Created or SystemTypeSyncStatus.Updated;
}

/// <summary>
/// Aggregated result of synchronizing one or more types of a system
/// catalog item (mini-project).
/// </summary>
/// <param name="CatalogItemId">Catalog item whose types were synchronized.</param>
/// <param name="TypeResults">Per-type results, in request order.</param>
public sealed record SystemFamilySyncResult(
    string CatalogItemId,
    IReadOnlyList<SystemTypeSyncResult> TypeResults)
{
    public int SuccessCount
    {
        get
        {
            var count = 0;
            foreach (var r in TypeResults)
            {
                if (r.IsSuccess) count++;
            }
            return count;
        }
    }

    public int FailedCount => TypeResults.Count - SuccessCount;

    /// <summary><c>true</c> when at least one type was requested and every
    /// type succeeded. A partial success (some types failed) is reported as
    /// <c>false</c> so the stale snapshot keeps the entry and the next Check
    /// re-evaluates the item.</summary>
    public bool AllSucceeded => TypeResults.Count > 0 && FailedCount == 0;

    /// <summary>Total number of dependency items across all types that could
    /// not be fully brought to the catalog reference (see
    /// <see cref="SystemTypeSyncResult.NotConvergedCount"/>).</summary>
    public int TotalNotConverged
    {
        get
        {
            var total = 0;
            foreach (var r in TypeResults)
            {
                total += r.NotConvergedCount;
            }
            return total;
        }
    }
}
