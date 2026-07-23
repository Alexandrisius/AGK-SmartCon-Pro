using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// A single "update request" of the catalog actualization engine (ADR-054,
/// docs/architecture/database-migrations.md): one artifact family that old
/// databases may lack — content hash, extracted attributes, GLB preview,
/// and every future extraction-time artifact. Tasks are discovered via DI;
/// the engine (<see cref="ICatalogActualizationService"/>) unions their
/// detections, opens each pending family file EXACTLY ONCE, and applies
/// only the tasks pending on that family.
/// </summary>
/// <remarks>
/// Implementation requirements:
/// <list type="bullet">
/// <item>Detection must be cheap SQL ("column emptiness" — no marker
/// column needed) and side-effect free — it runs on every database
/// switch.</item>
/// <item>Every artifact must clear its own detection when written — that
/// is what makes the engine resumable after cancel/crash.</item>
/// <item><see cref="ApplyAsync"/> commits its own writes (short
/// transactions, I-14: DELETE journal, no WAL, connections only via
/// <c>LocalCatalogDatabase</c>).</item>
/// <item>Apply must be idempotent — the same family may be reprocessed
/// after a partial failure.</item>
/// </list>
/// </remarks>
public interface IDatabaseActualizationTask
{
    /// <summary>Stable unique id (e.g. <c>"hash-v3"</c>), used in logs.</summary>
    string Id { get; }

    /// <summary>
    /// Apply order when several tasks are pending on the same family
    /// (ascending). Cheap artifacts first.
    /// </summary>
    int Order { get; }

    /// <summary>
    /// True for critical tasks: pending records raise the banner and gate
    /// every write operation until resolved. False for optional tasks:
    /// they only make the unified "Обновить базу" command visible (for
    /// Owner/BimMaster).
    /// </summary>
    bool IsCritical { get; }

    /// <summary>
    /// Number of records still requiring this task for the given Revit
    /// major version (drives the badge/menu count). Returns 0 when there
    /// is nothing to do. Only PROCESSABLE groups are counted (at least
    /// one file variant openable in the running Revit).
    /// </summary>
    Task<int> CountPendingAsync(int revitMajorVersion, CancellationToken ct = default);

    /// <summary>
    /// Pending groups whose EVERY file variant requires a Revit NEWER than
    /// the running one, plus the minimum Revit version that would make them
    /// all processable in one pass. For CRITICAL tasks these gate write
    /// operations exactly like processable pending (ADR-054 §3a — the
    /// database stays read-only until perfectly updated); for OPTIONAL
    /// tasks they only drive the amber indicator.
    /// </summary>
    Task<NewerOnlyPendingInfo> GetNewerOnlyPendingAsync(int revitMajorVersion, CancellationToken ct = default);

    /// <summary>
    /// Keys (<c>catalogItemId|versionLabel</c>) of family groups needing
    /// FILE extraction for this task, restricted to groups with at least
    /// one variant openable in the running Revit. The engine intersects
    /// these with its group rows; groups whose variants are all newer are
    /// counted as newer-Revit-pending.
    /// </summary>
    Task<IReadOnlyCollection<string>> LoadPendingGroupKeysAsync(
        int revitMajorVersion, CancellationToken ct = default);

    /// <summary>
    /// Instant pre-pass for work that does NOT need to open files (e.g.
    /// the hash task's system-family re-flag). Runs once before the file
    /// loop. Returns the number of records updated (0 for most tasks).
    /// </summary>
    Task<int> RunFileFreePassAsync(int revitMajorVersion, CancellationToken ct = default);

    /// <summary>
    /// Applies this task's artifacts for one successfully extracted family
    /// group. Called only when the group's key was in
    /// <see cref="LoadPendingGroupKeysAsync"/>.
    /// </summary>
    Task ApplyAsync(FamilyActualizationContext context, CancellationToken ct = default);

    /// <summary>
    /// The group could not be extracted. The task marks its own criterion
    /// (terminal markers) or does nothing (stays pending — retried on the
    /// next run).
    /// </summary>
    Task HandleGroupFailureAsync(
        ActualizationGroup group, ActualizationFailureKind kind, CancellationToken ct = default);
}
