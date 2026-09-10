namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Outcome of marking one staged mini-project file on disk (Issue #189,
/// actualization task <c>mini-project-marker-v1</c>). The service opens the
/// managed .rvt, writes the ES marker (#188), saves in place and cleans up
/// Revit backup files — the first actualization operation that WRITES into
/// a managed file instead of the catalog database.
/// </summary>
/// <param name="Status">What happened with the file.</param>
/// <param name="BackupsDeleted">Number of Revit backup files
/// (<c>name.NNNN.rvt</c>) removed after the save — the version folder must
/// hold exactly one .rvt (owner requirement, 2026-08-04).</param>
/// <param name="ErrorMessage">Failure details when
/// <see cref="Status"/> is <see cref="MiniProjectMarkFileStatus.Failed"/>,
/// <c>null</c> otherwise.</param>
public sealed record MiniProjectMarkFileOutcome(
    MiniProjectMarkFileStatus Status,
    int BackupsDeleted,
    string? ErrorMessage = null);

/// <summary>Status of <see cref="MiniProjectMarkFileOutcome"/>.</summary>
public enum MiniProjectMarkFileStatus
{
    /// <summary>The ES marker was written and the file saved in place.</summary>
    Marked = 0,

    /// <summary>The file already carried a valid marker for the same catalog
    /// item — no rewrite performed (idempotency).</summary>
    AlreadyMarked = 1,

    /// <summary>The managed file is absent from disk (terminal, mirrors the
    /// hash task's -2 convention).</summary>
    Missing = 2,

    /// <summary>Open/mark/save failed (terminal, mirrors the hash task's -1
    /// convention; the error is logged with an Action).</summary>
    Failed = 3,
}
