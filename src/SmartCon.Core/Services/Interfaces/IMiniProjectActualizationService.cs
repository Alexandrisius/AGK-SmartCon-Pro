using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Writes the mini-project ES marker (#188) into an existing staged .rvt on
/// disk (Issue #189, actualization task <c>mini-project-marker-v1</c>) —
/// the first actualization operation that modifies a MANAGED FILE rather
/// than the catalog database. Implementations marshal to the Revit UI
/// thread internally (<see cref="IFamilyManagerAwaitableEvent"/>), so
/// callers run off the Revit thread.
/// Contract: open → skip when already marked (idempotent) → clear the
/// read-only attribute (I-16 engine-level exception) → mark →
/// <c>Document.Save()</c> (same path, same version — never SaveAs) →
/// delete Revit backup files (<c>name.NNNN.rvt</c>) → restore read-only →
/// close without saving.
/// </summary>
public interface IMiniProjectActualizationService
{
    /// <summary>
    /// Marks <paramref name="absolutePath"/> as a SmartCon mini-project for
    /// <paramref name="catalogItemId"/>. Never throws for expected failure
    /// modes — the outcome carries the status; unexpected exceptions
    /// propagate (the task treats them as a group failure).
    /// </summary>
    Task<MiniProjectMarkFileOutcome> MarkManagedFileAsync(
        string absolutePath, string catalogItemId, CancellationToken ct = default);
}
