using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Status of <see cref="IMiniProjectRoutingSlimmingService.SlimManagedFileAsync"/>.
/// </summary>
public enum MiniProjectSlimmingStatus
{
    /// <summary>The file was opened, slimmed and saved in place.</summary>
    Slimmed,
    /// <summary>The file was already slim (no fittings, no fitting rules)
    /// — nothing to write; the pre-slim snapshot was NOT extracted (a slim
    /// file has no full routing to backfill).</summary>
    AlreadySlim,
    /// <summary>The managed file is absent on disk.</summary>
    Missing,
    /// <summary>Open/slim/save failed (locked file, Revit error).</summary>
    Failed,
}

/// <summary>
/// Outcome of one slimming pass over a managed mini-project.
/// </summary>
/// <param name="Status">What happened.</param>
/// <param name="PreSlimSnapshot">Routing-relevant snapshot of every
/// MEPCurve type extracted BEFORE slimming (the full legacy routing —
/// the backfill source for versions without section strings);
/// <c>null</c> for <see cref="MiniProjectSlimmingStatus.AlreadySlim"/> and
/// failures.</param>
/// <param name="FittingInstancesDeleted">Placed fitting instances removed.</param>
/// <param name="FittingFamiliesDeleted">Loadable fitting families removed.</param>
/// <param name="OrphanMaterialsDeleted">Unreferenced materials removed.</param>
/// <param name="MaterialsRenamed">Suffixed working copies renamed to the
/// clean base name.</param>
/// <param name="BackupsDeleted">Revit <c>name.NNNN.rvt</c> backups removed.</param>
/// <param name="ErrorMessage">Failure details for
/// <see cref="MiniProjectSlimmingStatus.Failed"/>.</param>
public sealed record MiniProjectSlimmingOutcome(
    MiniProjectSlimmingStatus Status,
    SystemFamilySnapshot? PreSlimSnapshot,
    int FittingInstancesDeleted,
    int FittingFamiliesDeleted,
    int OrphanMaterialsDeleted,
    int MaterialsRenamed,
    int BackupsDeleted,
    string? ErrorMessage = null);

/// <summary>
/// ADR-072 (Phase 2b): heals pre-refactor mini-projects in managed
/// storage — extracts the full legacy routing BEFORE slimming (the
/// backfill source when the version has no section strings), then slims
/// the file to the new reference shape: fitting routing rules removed
/// (segments kept), routing parameters forced to «Нет», dragged fitting
/// instances/families deleted, orphan materials (incl. #254 duplicates)
/// deleted, suffixed working copies renamed to the clean name, saved in
/// place, backups deleted, read-only restored (I-16 exception — the same
/// engine-level pattern as <c>RevitMiniProjectActualizationService</c>).
/// Marshals to the Revit UI thread via the awaitable event (I-01).
/// </summary>
public interface IMiniProjectRoutingSlimmingService
{
    Task<MiniProjectSlimmingOutcome> SlimManagedFileAsync(
        string absolutePath,
        CancellationToken ct = default);
}
