using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.FamilyManager;

public sealed partial class RevitFamilyLoadService
{
    /// <summary>
    /// #209: reloads a family definition that is nested inside the active
    /// family document (.rfa in the Family Editor) AT ANY DEPTH. Non-
    /// interactive: <c>onSharedDecision</c> is null, so
    /// <see cref="RevitFamilyLoadOptions"/> takes the default branch
    /// (source=Family, overwrite per the caller's choice).
    /// <para>
    /// Depth-N semantics (integration contracts 2026-08-10,
    /// <c>NestedFamilyReloadTests</c>): a shared family nested at any depth
    /// exists as ONE hoisted definition in the host family document; a
    /// direct overwrite reload into the host updates exactly that
    /// definition, and it is the hoisted definition that reaches projects
    /// when the host is loaded (proven: the project receives the updated
    /// grandchild). The intermediate's internal EditFamily view keeps a
    /// stale embedded copy — documented, cosmetic, and unreachable from
    /// projects; the catalog hashes/verifies the hoisted definition, so
    /// verification and drift detection stay consistent.
    /// </para>
    /// <para>
    /// Mechanism (2026-08-11, probe-proven on the owner's real library,
    /// <c>NestedReloadPokeProbeTests</c>): plain
    /// <c>Document.LoadFamily(path, IFamilyLoadOptions)</c> is a SILENT
    /// NO-OP for diverged-lineage families — Revit compares its internal
    /// changedness stamp, not content, so a family whose perceivable diff
    /// is small (or whose lineage drifted from another parent) is never
    /// merged even though the callbacks fire and <c>true</c> is returned
    /// (Autodesk confirms the LoadFamily API does not replicate the manual
    /// UI reload, Revit API forum 2026-04-30). The proven workaround —
    /// the same one Autodesk support recommends and the owner validated
    /// manually: open the source .rfa in the background, apply a NET-ZERO
    /// in-memory edit (add a scratch family parameter, commit, remove it,
    /// commit + regenerate — two commits, never saved to disk; Revit's
    /// stamp is a dirty flag with no content history, so the net-zero edit
    /// still flips it), then push the definition doc-to-doc
    /// (<c>sourceDoc.LoadFamily(hostDoc, options)</c>). The merge then
    /// takes the full overwrite path and the embedded definition is really
    /// replaced (probe: a new type landed in the pair via the API). If the
    /// poke fails for any reason the method falls back to the legacy
    /// path-load. A <c>null</c> return from the doc-to-doc call AFTER a
    /// successful poke is a hard failure with no path-load retry — by then
    /// the in-memory source is already dirty, while a path-load reads the
    /// untouched (unpoked) file from disk and would hit the same no-op.
    /// Revit NEVER propagates parameter groups on any merge —
    /// the unified FHV10 content hash does not contain groups for exactly
    /// this reason.
    /// </para>
    /// <para>
    /// The caller (StaleFamilyUpdater) post-verifies the reloaded content
    /// hash against the catalog target version before writing any marker —
    /// a failed merge can never produce a lying marker.
    /// </para>
    /// <para>
    /// <see cref="IFamilyLoadServiceSourceAware"/> entry point: nested reload
    /// with an optional caller-provided (borrowed, never closed here) source
    /// document, so one Stale-Update cycle shares a single OpenDocumentFile.
    /// </para>
    /// </summary>
    public FamilyLoadResult ReloadNestedInFamilyDocument(
        string normalizedPath,
        string familyName,
        bool overwriteParameterValues,
        Func<Document?>? preOpenedSourceDocProvider = null)
    {
        var doc = _revitContext.GetDocument();
        if (doc is null)
            return new FamilyLoadResult(false, null, null, "No active document", FamilyLoadStatus.Failed);
        if (!doc.IsFamilyDocument)
        {
            var notFamily = "ReloadNestedInFamilyDocument requires the active document to be a family document";
            SmartConLogger.Warn(
                $"{notFamily} [Action: используйте ReloadFamilyPreservingLoadedTypesAsync — в проекте работает preserve-types reload]");
            return new FamilyLoadResult(false, familyName, null, notFamily, FamilyLoadStatus.Failed);
        }
        return ReloadNestedInFamilyDocumentCore(
            doc, normalizedPath, familyName, overwriteParameterValues, preOpenedSourceDocProvider);
    }

    private FamilyLoadResult ReloadNestedInFamilyDocumentCore(
        Document doc,
        string normalizedPath,
        string familyNameFromPath,
        bool overwriteParameterValues,
        Func<Document?>? preOpenedSourceDocProvider)
    {
        Document? providedSource = null;
        if (preOpenedSourceDocProvider is not null)
        {
            try
            {
                providedSource = preOpenedSourceDocProvider();
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"Pre-opened source provider for '{familyNameFromPath}' threw: {ex.GetType().Name}: {ex.Message} " +
                    "[Action: источник будет открыт заново самим сервисом]");
                providedSource = null;
            }
        }

        var nested = FindExistingFamily(doc, familyNameFromPath);
        if (nested is null)
        {
            var notNested = $"Family '{familyNameFromPath}' is not nested in the active family document";
            SmartConLogger.Warn(
                $"{notNested} [Action: обновление внутри семейства поддерживается только для вложенных в него семейств]");
            return new FamilyLoadResult(false, familyNameFromPath, null, notNested, FamilyLoadStatus.Failed);
        }

        // The caller-provided source document is excluded from both guards:
        // it is a legitimate background open done by the caller, not a user
        // editing session. The caller performs the equivalent "source file
        // open in the editor" check BEFORE opening it. Exclusion is by
        // PATH — ReferenceEquals is unreliable here (Revit may hand out
        // different managed wrappers for the same underlying document).
        var providedPath = providedSource?.PathName;
        var openTopLevel = doc.Application.Documents
            .Cast<Document>()
            .Where(d => d.IsFamilyDocument
                && (string.IsNullOrEmpty(providedPath)
                    || string.IsNullOrEmpty(d.PathName)
                    || !string.Equals(
                        Path.GetFullPath(d.PathName), Path.GetFullPath(providedPath), StringComparison.OrdinalIgnoreCase)))
            .Select(d => d.Title)
            .ToList();
        if (openTopLevel.Contains(familyNameFromPath, StringComparer.OrdinalIgnoreCase))
        {
            var openMsg = $"Family '{familyNameFromPath}' is open in the Family Editor";
            SmartConLogger.Warn(
                $"{openMsg} [Action: закройте семейство в редакторе (сохранив или отменив правки) и повторите «Обновить»]");
            return new FamilyLoadResult(false, familyNameFromPath, null, openMsg, FamilyLoadStatus.Failed);
        }

        // The resolved file may be open under a RENAMED title (the Title
        // guard above misses it): OpenDocumentFile would return the user's
        // live document, the poke would inject phantom edits into it and
        // the finally-Close would destroy unsaved work. Refuse by PathName.
        // Skipped when the caller provided the source document — the caller
        // already performed this exact check before opening it.
        if (providedSource is null)
        {
            var openByPath = doc.Application.Documents
                .Cast<Document>()
                .Any(d => !string.IsNullOrEmpty(d.PathName)
                    && string.Equals(Path.GetFullPath(d.PathName), normalizedPath, StringComparison.OrdinalIgnoreCase));
            if (openByPath)
            {
                var pathMsg = $"Source file for '{familyNameFromPath}' is open in the editor";
                SmartConLogger.Warn(
                    $"{pathMsg} [Action: закройте файл версии в редакторе (сохранив или отменив правки) и повторите «Обновить»]");
                return new FamilyLoadResult(false, familyNameFromPath, null, pathMsg, FamilyLoadStatus.Failed);
            }
        }

        var options = new RevitFamilyLoadOptions(
            overwriteParameterValues,
            onStatusMessage: null,
            onSharedDecision: null,
            nestedSharedNames: null);

        Document? sourceDoc = providedSource;
        var ownsSource = sourceDoc is null;
        try
        {
            if (sourceDoc is null)
            {
                sourceDoc = doc.Application.OpenDocumentFile(normalizedPath);
            }

            if (TryPokeFamilyDocumentForReload(sourceDoc, familyNameFromPath))
            {
                // Doc-to-doc LoadFamily manages its own transaction — the
                // target document must not be modifiable at call time.
                var pushed = sourceDoc.LoadFamily(doc, options);
                if (pushed is null)
                {
                    return LoadFamilyRejectedResult(familyNameFromPath);
                }

                SmartConLogger.Info(
                    $"Nested family '{familyNameFromPath}' reloaded into the family document via poke + doc-to-doc " +
                    $"(overwriteParameterValues={overwriteParameterValues}) — pending caller's content verification");
                return new FamilyLoadResult(
                    true, familyNameFromPath, $"Nested family '{familyNameFromPath}' reloaded", null, FamilyLoadStatus.Updated);
            }

            // Poke unavailable (unusual family) — legacy path-load fallback.
            SmartConLogger.Warn(
                $"Poke failed for '{familyNameFromPath}', falling back to path-load " +
                "[Action: если верификация не пройдёт — обновите вложенное семейство вручную в редакторе]");
            var loaded = false;
            var ok = _transactionService.RunInTransaction(
                doc,
                "SmartCon: Reload Nested Family",
                d => { loaded = d.LoadFamily(normalizedPath, options, out _); });
            if (!ok || !loaded)
            {
                return LoadFamilyRejectedResult(familyNameFromPath);
            }

            SmartConLogger.Info(
                $"Nested family '{familyNameFromPath}' reloaded into the family document " +
                $"(overwriteParameterValues={overwriteParameterValues}) — pending caller's content verification");
            return new FamilyLoadResult(
                true, familyNameFromPath, $"Nested family '{familyNameFromPath}' reloaded", null, FamilyLoadStatus.Updated);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"ReloadNestedInFamilyDocument('{familyNameFromPath}') failed: {ex.GetType().Name}: {ex.Message} " +
                "[Action: семейство пропущено, batch продолжится; проверьте, что семейство не открыто в редакторе]");
            return new FamilyLoadResult(false, familyNameFromPath, null, ex.Message, FamilyLoadStatus.Failed);
        }
        finally
        {
            // BORROW rule: a caller-provided source document is never closed
            // here — ownership (and closing) stays with the caller.
            if (ownsSource && sourceDoc is not null)
            {
                try { sourceDoc.Close(false); } catch { }
            }
        }
    }

    /// <summary>
    /// Net-zero in-memory poke that flips Revit's internal changedness
    /// stamp of a family document so the subsequent doc-to-doc LoadFamily
    /// takes the full merge path (see the reload method's doc). Adds a
    /// scratch family parameter in one transaction, removes it + regenerates
    /// in a second — two commits are required (the owner proved manually
    /// that a single-session add+Apply / remove+Apply cycle triggers the
    /// overwrite dialog; Revit keeps no content history, the stamp is a
    /// dirty flag). The document is never saved — the catalog file on disk
    /// stays untouched. Returns false when the scratch parameter cannot be
    /// added/removed (the caller then falls back to a plain path-load).
    /// </summary>
    private bool TryPokeFamilyDocumentForReload(Document sourceDoc, string familyName)
    {
        try
        {
            var existingNames = new HashSet<string>(
                sourceDoc.FamilyManager.GetParameters().Select(p => p.Definition.Name),
                StringComparer.OrdinalIgnoreCase);
            var pokeName = "__SmartConPoke__";
            for (var i = 1; existingNames.Contains(pokeName); i++)
            {
                pokeName = $"__SmartConPoke{i}__";
            }

            var added = _transactionService.RunInTransaction(
                sourceDoc,
                "SmartCon: Nested Reload Poke (add)",
                d =>
                {
#if REVIT2022_OR_GREATER
                    d.FamilyManager.AddParameter(pokeName, GroupTypeId.General, SpecTypeId.String.Text, false);
#else
                    d.FamilyManager.AddParameter(pokeName, BuiltInParameterGroup.PG_GENERAL, ParameterType.Text, false);
#endif
                });
            if (!added)
            {
                SmartConLogger.Warn(
                    $"Poke add-parameter transaction failed for '{familyName}' " +
                    "[Action: будет использована обычная загрузка по пути — верификация покажет результат]");
                return false;
            }

            var removed = _transactionService.RunInTransaction(
                sourceDoc,
                "SmartCon: Nested Reload Poke (remove)",
                d =>
                {
                    var scratch = d.FamilyManager.GetParameters()
                        .FirstOrDefault(p => string.Equals(p.Definition.Name, pokeName, StringComparison.Ordinal));
                    if (scratch is not null)
                    {
                        d.FamilyManager.RemoveParameter(scratch);
                    }
                    d.Regenerate();
                });
            if (!removed)
            {
                SmartConLogger.Warn(
                    $"Poke remove-parameter transaction failed for '{familyName}' " +
                    "[Action: будет использована обычная загрузка по пути — верификация покажет результат]");
                return false;
            }

            SmartConLogger.Info($"Poke applied to '{familyName}' (net-zero, in-memory only) — changedness stamp flipped");
            return true;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Poke failed for '{familyName}': {ex.GetType().Name}: {ex.Message} " +
                "[Action: будет использована обычная загрузка по пути — верификация покажет результат]");
            return false;
        }
    }

    private static FamilyLoadResult LoadFamilyRejectedResult(string familyName)
    {
        var msg = $"LoadFamily returned false for nested '{familyName}' (conflict auto-abort or rejection)";
        SmartConLogger.Warn(
            $"{msg} [Action: семейство осталось прежним — проверьте лог выше; повторите обновление]");
        return new FamilyLoadResult(false, familyName, null, msg, FamilyLoadStatus.Failed);
    }
}
