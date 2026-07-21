namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Item-level progress of a single database migration (ADR-054). Shared by
/// every migration — the unified update dialog renders it inside the
/// current stage.
/// </summary>
/// <param name="Current">1-based index of the record being processed.</param>
/// <param name="Total">Total records to process in this migration.</param>
/// <param name="CurrentFileName">Managed file being processed (for the status line).</param>
public sealed record DatabaseMigrationProgress(
    int Current,
    int Total,
    string CurrentFileName);
