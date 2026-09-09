using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.Import;
using SmartCon.FamilyManager.Services.LocalCatalog;

namespace SmartCon.FamilyManager.Services;

public sealed partial class FamilyImportPreparationService
{
    /// <summary>
    /// Close all documents opened during Phase 1 (Prepare).
    /// Call this when the user cancels the batch dialog.
    /// </summary>
    public async Task CloseAllPreparedDocumentsAsync(CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope("FamilyPrep",
            ("Method", nameof(CloseAllPreparedDocumentsAsync)),
            ("HeldOpenCount", _openedDocuments.Count));

        SmartConLogger.Info(
            $"Closing prepared documents: heldOpen={_openedDocuments.Count} " +
            "(will also enumerate app.Documents to detect leaked handles)");

        await _awaitableEvent.RaiseAsync(app =>
        {
            LogAllOpenRevitDocuments();

            foreach (var pair in _openedDocuments)
            {
                try
                {
                    if (pair.Value is not null)
                    {
                        // Phase 27B: after staging (SaveAs + ReleaseDocument),
                        // some documents may have been invalidated by Revit.
                        // IsValidObject check prevents "The referenced object
                        // is not valid" warnings during cleanup.
                        if (!pair.Value.IsValidObject)
                        {
                            SmartConLogger.Debug(
                                $"Document '{Path.GetFileName(pair.Key)}' already invalidated by Revit — skipping Close");
                            continue;
                        }
                        pair.Value.Close(false);
                        SmartConLogger.Debug($"Closed document: {Path.GetFileName(pair.Key)}");
                    }
                }
                catch (Exception ex)
                {
                    SmartConLogger.Warn(
                        $"Failed to close document '{Path.GetFileName(pair.Key)}': {ex.Message} " +
                        "[Action: document may remain open — user can close manually]");
                }
            }
        }, ct).ConfigureAwait(false);

        _openedDocuments.Clear();
        // UC-2: release the active-document override WITHOUT closing — the
        // user's family document stays open and untouched.
        _activeFamilyDoc = null;
        _activeFamilyDocKey = null;
        SmartConLogger.Info("All prepared documents closed");
    }

    /// <summary>
    /// Get a document that was opened during Phase 1 and is held open
    /// for Phase 3 (SaveAs). Returns null if the path was not prepared,
    /// the entry was null, or the document has been invalidated by Revit
    /// (IsValidObject == false). Stale entries are evicted from the cache.
    /// </summary>
    public Document? GetOpenedDocument(string sourcePath)
    {
        if (_openedDocuments.TryGetValue(sourcePath, out var doc))
        {
            if (doc is null)
            {
                _openedDocuments.Remove(sourcePath);
                return null;
            }

            if (!doc.IsValidObject)
            {
                SmartConLogger.Warn(
                    $"GetOpenedDocument: held-open document for '{Path.GetFileName(sourcePath)}' " +
                    "is invalidated by Revit (IsValidObject=false) — evicting from cache " +
                    "[Action: caller will fall back to a fresh OpenDocumentFile/EditFamily]");
                _openedDocuments.Remove(sourcePath);
                return null;
            }

            SmartConLogger.Debug(
                $"GetOpenedDocument HIT: path='{Path.GetFileName(sourcePath)}', " +
                $"PathName='{(string.IsNullOrEmpty(doc.PathName) ? "<empty>" : doc.PathName)}', " +
                $"Title='{doc.Title}'");
            return doc;
        }

        SmartConLogger.Debug($"GetOpenedDocument MISS: path='{Path.GetFileName(sourcePath)}'");
        return null;
    }

    /// <summary>
    /// Remove a document from the held-open dictionary without closing it.
    /// Use this only when the document is known to be already invalidated or
    /// closed by other means (e.g. exception paths). For the normal Phase 3
    /// flow (SaveAs followed by cleanup) prefer <see cref="CloseAndRelease"/>.
    /// </summary>
    public void ReleaseDocument(string sourcePath)
    {
        _openedDocuments.Remove(sourcePath);
    }

    /// <summary>
    /// Close a held-open document with <c>Close(false)</c> and remove it from
    /// the cache. Must be called on the Revit UI thread (e.g. from inside an
    /// <c>IFamilyManagerAwaitableEvent.RaiseAsync</c> callback) because it
    /// touches <c>Document.IsValidObject</c> and <c>Document.Close</c>.
    /// Silently evicts entries that are null or already invalidated by Revit.
    /// </summary>
    public void CloseAndRelease(string sourcePath)
    {
        if (!_openedDocuments.TryGetValue(sourcePath, out var doc))
        {
            SmartConLogger.Debug($"CloseAndRelease MISS: path='{Path.GetFileName(sourcePath)}'");
            return;
        }

        _openedDocuments.Remove(sourcePath);

        if (doc is null)
            return;

        if (!doc.IsValidObject)
        {
            SmartConLogger.Debug(
                $"CloseAndRelease: document '{Path.GetFileName(sourcePath)}' already invalidated by Revit — " +
                "evicted from cache without Close");
            return;
        }

        try
        {
            doc.Close(false);
            SmartConLogger.Info(
                $"CloseAndRelease: closed held-open document '{Path.GetFileName(sourcePath)}' " +
                $"(PathName='{(string.IsNullOrEmpty(doc.PathName) ? "<empty>" : doc.PathName)}')");
        }
        catch (Autodesk.Revit.Exceptions.InvalidObjectException)
        {
            SmartConLogger.Debug(
                $"CloseAndRelease: document '{Path.GetFileName(sourcePath)}' already closed by Revit " +
                "(InvalidObjectException after SaveAs-overwrite) — no leak");
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"CloseAndRelease: failed to close document '{Path.GetFileName(sourcePath)}': {ex.Message} " +
                "[Action: document may remain open in Revit — user can close it manually]");
        }
    }

    /// <summary>
    /// Enumerate every open document in the Revit session and log it with
    /// its title, path, validity and family-document flag. Documents whose
    /// path contains <paramref name="filterId"/> are tagged so callers (e.g.
    /// <c>DeleteFamilyAsync</c> catching <c>IOException</c>) can identify
    /// which open document holds the lock on a managed path. Marshalled via
    /// <c>IFamilyManagerAwaitableEvent</c>, so safe to call from any thread.
    /// </summary>
    public async Task LogOpenRevitDocumentsStateAsync(
        string contextTag,
        string? filterId = null,
        CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope("FamilyPrep",
            ("Method", nameof(LogOpenRevitDocumentsStateAsync)),
            ("ContextTag", contextTag),
            ("FilterId", filterId ?? "<none>"));

        await _awaitableEvent.RaiseAsync(_ =>
        {
            LogAllOpenRevitDocuments(filterId);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Synchronous helper that walks <c>Application.Documents</c> and writes
    /// one structured <c>Info</c> line per open document. Must be called on
    /// the Revit UI thread. Best-effort: any per-document access failure is
    /// logged and skipped so a single corrupted document does not hide the
    /// rest of the session state.
    /// </summary>
    private void LogAllOpenRevitDocuments(string? filterId = null)
    {
        try
        {
            var activeDoc = _revitContext.GetDocument();
            if (activeDoc is null)
            {
                SmartConLogger.Info("LogAllOpenRevitDocuments: no active document (Revit context is null)");
                return;
            }

            var revitApp = activeDoc.Application;
            var allDocs = revitApp.Documents.OfType<Document>().ToList();
            var heldKeys = new HashSet<string>(_openedDocuments.Keys, StringComparer.Ordinal);

            var lines = new List<string>(allDocs.Count);
            var matchCount = 0;

            foreach (var d in allDocs)
            {
                string title;
                try { title = d.Title; }
                catch (Exception ex) { title = $"<title-threw:{ex.GetType().Name}>"; }

                string path;
                try { path = d.PathName ?? string.Empty; }
                catch (Exception ex) { path = $"<path-threw:{ex.GetType().Name}>"; }

                bool isValid;
                try { isValid = d.IsValidObject; }
                catch (Exception ex) { isValid = false; title += $"(IsValidObject-threw:{ex.GetType().Name})"; }

                bool isFamilyDoc;
                try { isFamilyDoc = d.IsFamilyDocument; }
                catch { isFamilyDoc = false; }

                var heldFlag = heldKeys.Contains(path) ? " [HELD-OPEN]" : string.Empty;

                var filterFlag = string.Empty;
                if (ContainsOrdinalIgnoreCase(path, filterId))
                {
                    filterFlag = " [MATCHES-FILTER]";
                    matchCount++;
                }

                var pathDisplay = string.IsNullOrEmpty(path) ? "<empty>" : path;
                lines.Add(
                    $"  title='{title}', path='{pathDisplay}', valid={isValid}, " +
                    $"isFamilyDoc={isFamilyDoc}{heldFlag}{filterFlag}");
            }

            SmartConLogger.Info(
                $"app.Documents.Size={allDocs.Count}, heldOpenInCache={_openedDocuments.Count}, " +
                $"filterMatches={matchCount}. Open docs:\n{string.Join("\n", lines)}");
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"LogAllOpenRevitDocuments failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
