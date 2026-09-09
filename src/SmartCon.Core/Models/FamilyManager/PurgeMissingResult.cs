namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// One routing link reset by the purge: the parent family's routing rule
/// referenced the purged (missing) fitting via the "Family:Type" token —
/// the token stays in the rule, but the referenced family is gone from the
/// catalog (the rule shows a "not in catalog" presence issue). Reported as
/// a name pair so the UI can list WHAT was affected, not just a count.
/// </summary>
/// <param name="PurgedItemName">Name of the purged fitting (child).</param>
/// <param name="ParentItemName">Name of the family whose routing rule lost the fitting.</param>
public sealed record ResetRoutingLinkInfo(string PurgedItemName, string ParentItemName);

/// <summary>
/// One family whose ACTIVE version was purged — the active pointer was
/// switched to the newest remaining version automatically (Issue #133:
/// purging a VERSION must not kill the family while other versions survive).
/// </summary>
/// <param name="ItemName">Family display name.</param>
/// <param name="NewActiveVersionLabel">The version label that became active.</param>
public sealed record SwitchedActiveVersionInfo(string ItemName, string NewActiveVersionLabel);

/// <summary>
/// Outcome of <c>ICatalogActualizationService.PurgeMissingAsync</c> — the
/// user-facing numbers AND name lists for the summary screens (the
/// database-update dialog and the "Очистить недоступные записи" tool,
/// Issue #133). Name lists instead of bare counts: the user must see which
/// families were affected.
/// </summary>
/// <param name="DeletedItems">Catalog items removed (ALL their versions were missing).</param>
/// <param name="DeletedVersions">Catalog version rows removed.</param>
/// <param name="FailedDirectories">
/// Managed directories that could not be deleted on disk (locked/unreachable)
/// — the rows were removed DB-only; the folders are reported for manual removal.
/// </param>
/// <param name="ResetRoutingLinks">
/// Routing links that were reset at parent families because the referenced
/// fitting was purged. The user must be warned that project routing using
/// those fittings became stale.
/// </param>
/// <param name="SwitchedActiveVersions">
/// Families whose purged version was the ACTIVE one — the active pointer
/// was switched to the newest remaining version automatically.
/// </param>
public sealed record PurgeMissingResult(
    int DeletedItems,
    int DeletedVersions,
    int FailedDirectories,
    IReadOnlyList<ResetRoutingLinkInfo> ResetRoutingLinks,
    IReadOnlyList<SwitchedActiveVersionInfo> SwitchedActiveVersions)
{
    public static readonly PurgeMissingResult Empty = new(0, 0, 0, Array.Empty<ResetRoutingLinkInfo>(), Array.Empty<SwitchedActiveVersionInfo>());
}
