using Autodesk.Revit.DB;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Issue #188: marks SmartCon reference mini-projects (staged .rvt files holding
/// system family types, ADR-061 §2) via ExtensibleStorage so the plugin can
/// distinguish them from real user work projects:
/// <list type="bullet">
///   <item>safe close without save after "Импорт активного файла" (#186) — a
///         work project must NEVER be closed;</item>
///   <item>exclusion from active-database auto-switching on
///         <c>ViewActivated</c> — a mini-project is not a project base;</item>
///   <item>protection from accidental save — closing is always
///         <c>Close(false)</c>, the read-only file on disk (I-16) stays intact.</item>
/// </list>
/// The marker is written during staging (before <c>SaveAs</c>) and travels
/// with the file across reopen sessions.
/// </summary>
public interface IMiniProjectMarker
{
    /// <summary>
    /// Writes the mini-project marker into <paramref name="doc"/> inside its own
    /// transaction (via <see cref="ITransactionService"/>, I-03). Called during
    /// staging BEFORE <c>SaveAs</c> — the marker must persist in the saved file
    /// because the document is closed without saving afterwards.
    /// </summary>
    /// <param name="doc">Staged mini-project document (not yet saved).</param>
    /// <param name="catalogItemId">Owning catalog item when known (new imports
    /// get a precomputed id, overwrite keeps the existing one); null/empty is
    /// allowed — the marker still flags the document as a mini-project.</param>
    void MarkAsMiniProject(Document doc, string? catalogItemId);

    /// <summary>
    /// Returns true when the document carries a valid mini-project ES marker.
    /// Read-only — no transaction required. Tolerates documents without the
    /// schema (legacy staged files → false).
    /// </summary>
    bool IsMiniProject(Document doc);

    /// <summary>
    /// Reads the catalog item id stored in the marker, or null when the marker
    /// is absent or carries no id.
    /// </summary>
    string? ReadCatalogItemId(Document doc);
}
