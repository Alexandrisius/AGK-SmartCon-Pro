using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.Stale;

/// <summary>
/// Shared verification-grade (FHV10 — the single unified content hash)
/// computation for a family embedded in an open family document vs a
/// resolved source .rfa. Extracted from
/// <see cref="StaleFamilyUpdater"/> so the stale CHECK can reuse the exact
/// same content proof the UPDATE path relies on (owner stress test
/// 2026-08-12: a marker-less freshly imported assembly was reported all-stale
/// although its embedded content matched the current catalog versions).
/// All methods run on the Revit thread (callers marshal via RaiseAsync).
/// Tri-state everywhere: <c>null</c> = indeterminate (guard tripped,
/// extraction failed) — never evidence.
/// <para>
/// TYPE-SET RULE (owner stress test 2026-08-14): in a PROJECT the file
/// hash is computed over the INTERSECTION of types — restricted to the
/// type names present in the embedded copy. The preserve-types reload
/// (#101) deliberately keeps the project's loaded subset (e.g. 1 of 2
/// types), while FHV10 hashes the TYPES section whole — an unrestricted
/// comparison could never match and reported false POST-RELOAD
/// VERIFICATION FAILED / false ContentDrift for every partially loaded
/// multi-type family. In a FAMILY DOCUMENT the comparison stays
/// full-vs-full: the poke + doc-to-doc merge transfers the WHOLE type
/// set, so a version bump that adds/removes types must mismatch
/// pre-reload (otherwise the update would be falsely skipped and the new
/// type would never land — validator finding 2026-08-14).
/// </para>
/// </summary>
internal static class EmbeddedContentVerifier
{
    /// <summary>
    /// Deterministically aligns the family document's current type before
    /// verification extraction (CURRENT-TYPE RULE, #240 — owner manual
    /// test 2026-08-23): the GEOM/CONN sections are evaluated at the CURRENT
    /// type — EditFamily from a project opens with the project's current
    /// type (e.g. Ф250), while the resolved .rfa opens with its saved
    /// active type (Ф100); same content, different geometry → false
    /// POST-RELOAD VERIFICATION FAILED for every multi-type family whose
    /// project-side current type differs from the file's saved one
    /// (single-type families are immune). Target: the first (Ordinal)
    /// name of <paramref name="preferredTypeNames"/> (the embedded type
    /// set — the file side of a restricted comparison) or of the
    /// document's own type set (embedded side / unrestricted comparison).
    /// Both sides are aligned to the same target, so the comparison is
    /// current-type-independent. This does NOT change the catalog hash
    /// semantics (DB extraction keeps the file's saved active type) —
    /// verification hashes are ephemeral. Best-effort: a failed switch is
    /// logged and the extraction proceeds — an honest mismatch is better
    /// than breaking the verification flow.
    /// </summary>
    internal static void AlignCurrentTypeForVerification(
        Document familyDoc,
        IReadOnlyCollection<string>? preferredTypeNames,
        string logContext)
    {
        try
        {
            var fm = familyDoc.FamilyManager;
            var byName = new Dictionary<string, FamilyType>(StringComparer.Ordinal);
            foreach (FamilyType t in fm.Types)
            {
                if (!byName.ContainsKey(t.Name))
                {
                    byName[t.Name] = t;
                }
            }

            var target = preferredTypeNames is not null
                ? preferredTypeNames
                    .Where(byName.ContainsKey)
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .FirstOrDefault()
                : byName.Keys.OrderBy(n => n, StringComparer.Ordinal).FirstOrDefault();
            if (target is null || string.Equals(fm.CurrentType?.Name, target, StringComparison.Ordinal))
            {
                return;
            }

            var before = fm.CurrentType?.Name ?? "<none>";
            using (var tx = new Transaction(familyDoc, "SmartCon_VerifyAlignType"))
            {
                tx.Start();
                fm.CurrentType = byName[target];
                tx.Commit();
            }

            SmartConLogger.Debug(
                $"{logContext}: aligned the current type for verification: '{before}' -> '{target}'");
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"{logContext}: could not align the current type for verification: {ex.GetType().Name}: {ex.Message} " +
                "[Action: верификация продолжится, но геометрия может ложно не совпасть — сообщите разработчикам]");
        }
    }

    /// <summary>
    /// Verification-grade hash of a family nested inside an open document
    /// (family document or project): EditFamily → snapshot →
    /// <see cref="IFamilyContentHasher.ComputeForLoadable"/> (FHV10: the
    /// unified hash — fair on an embedded document, groups aside they are
    /// not hashed at all). Also returns the type-name set present in the
    /// embedded copy — the caller restricts the file hash to the same set
    /// (see the type-set rule in the class summary).
    /// Returns <c>(null, [])</c> when the family is not found or is open as
    /// a top-level document (EditFamily would return the user's live document
    /// with unsaved edits, and closing it would destroy them — skip instead).
    /// </summary>
    public static (string? Hash, IReadOnlyList<string> TypeNames) ComputeEmbeddedHash(
        Document doc,
        string familyName,
        IFamilySnapshotExtractor snapshotExtractor,
        IFamilyContentHasher contentHasher,
        string logContext)
    {
        var nested = new FilteredElementCollector(doc)
            .OfClass(typeof(Autodesk.Revit.DB.Family))
            .Cast<Autodesk.Revit.DB.Family>()
            .FirstOrDefault(f => string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase));
        if (nested is null)
        {
            SmartConLogger.Warn(
                $"{logContext}: family '{familyName}' not found in the active document " +
                "[Action: верификация пропущена — семейство не загружено в активный документ]");
            return (null, []);
        }

        // Guard: the family is open as a top-level document — EditFamily
        // would return the user's live document (unsaved edits), and the
        // finally-block Close(false) would destroy them. Match by Title
        // (file name) AND by OwnerFamily name (renamed files), M2.
        var isOpenTopLevel = doc.Application.Documents
            .Cast<Document>()
            .Any(d => d.IsFamilyDocument
                && (string.Equals(d.Title, familyName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(d.OwnerFamily?.Name, familyName, StringComparison.OrdinalIgnoreCase)));
        if (isOpenTopLevel)
        {
            SmartConLogger.Warn(
                $"{logContext}: '{familyName}' is open in the Family Editor — embedded content cannot be trusted " +
                "[Action: закройте семейство в редакторе (сохранив или отменив правки) и повторите «Проверить»]");
            return (null, []);
        }

        Document? copy = null;
        try
        {
            copy = doc.EditFamily(nested);
            AlignCurrentTypeForVerification(copy, preferredTypeNames: null, logContext);
            var snapshot = snapshotExtractor.ExtractFromFamilyDocument(copy);
            var hash = contentHasher.ComputeForLoadable(snapshot)?.HexString;
            return (hash, snapshot.Types.Select(t => t.Name).ToList());
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"{logContext}: embedded hash computation failed for '{familyName}': {ex.GetType().Name}: {ex.Message} " +
                "[Action: верификация пропущена — семейство останется stale, повторите «Обновить»]");
            return (null, []);
        }
        finally
        {
            try { copy?.Close(false); } catch { }
        }
    }

    /// <summary>
    /// Verification-grade hash of a resolved source .rfa, opened in the
    /// background and closed afterwards. Returns <c>null</c> when the file
    /// is open in the editor (OpenDocumentFile would return the user's live
    /// document — unsaved edits would be hashed and destroyed on close) or
    /// extraction fails. <paramref name="fileHashCache"/> (keyed by full
    /// path, case-insensitive) makes a multi-family check pay ONE
    /// OpenDocumentFile per version file per run — duplicate catalog items
    /// resolving to the same file reuse the cached hash (a cached
    /// <c>null</c> is a cached "indeterminate", not a re-open).
    /// <paramref name="restrictToTypeNames"/> — the type-set rule: when
    /// supplied, the file snapshot's TYPES are filtered to these names
    /// before hashing (the embedded copy of a partially loaded family
    /// carries only the loaded subset — see the class summary). The cache
    /// key then carries the sorted name set.
    /// </summary>
    public static string? ComputeFileHash(
        Document doc,
        string absolutePath,
        IFamilySnapshotExtractor snapshotExtractor,
        IFamilyContentHasher contentHasher,
        string logContext,
        IDictionary<string, string?>? fileHashCache = null,
        IReadOnlyCollection<string>? restrictToTypeNames = null)
    {
        var resolvedFullPath = System.IO.Path.GetFullPath(absolutePath);
        var cacheKey = restrictToTypeNames is null
            ? resolvedFullPath
            : resolvedFullPath + "|" + string.Join(",",
                restrictToTypeNames.OrderBy(n => n, StringComparer.Ordinal));
        if (fileHashCache is not null && fileHashCache.TryGetValue(cacheKey, out var cached))
        {
            SmartConLogger.Debug($"{logContext}: file hash cache hit for {System.IO.Path.GetFileName(resolvedFullPath)}");
            return cached;
        }

        var alreadyOpen = doc.Application.Documents
            .Cast<Document>()
            .Any(d => !string.IsNullOrEmpty(d.PathName)
                && string.Equals(
                    System.IO.Path.GetFullPath(d.PathName), resolvedFullPath, StringComparison.OrdinalIgnoreCase));
        if (alreadyOpen)
        {
            SmartConLogger.Warn(
                $"{logContext}: the resolved file is open in the editor — " +
                "its on-disk content cannot be trusted for verification " +
                "[Action: закройте файл версии в редакторе (сохранив или отменив правки) и повторите «Обновить»]");
            if (fileHashCache is not null)
            {
                fileHashCache[cacheKey] = null;
            }
            return null;
        }

        string? file = null;
        Document? fileDoc = null;
        try
        {
            fileDoc = doc.Application.OpenDocumentFile(absolutePath);
            AlignCurrentTypeForVerification(fileDoc, restrictToTypeNames, logContext);
            var snap = snapshotExtractor.ExtractFromFamilyDocument(fileDoc);
            if (restrictToTypeNames is not null)
            {
                var allowed = new HashSet<string>(restrictToTypeNames, StringComparer.OrdinalIgnoreCase);
                snap = snap with { Types = snap.Types.Where(t => allowed.Contains(t.Name)).ToList() };
            }
            file = contentHasher.ComputeForLoadable(snap)?.HexString;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"{logContext}: could not extract the resolved file for verification: " +
                $"{ex.GetType().Name}: {ex.Message} " +
                "[Action: верификация пропущена — проверьте, что файл версии доступен на диске]");
            file = null;
        }
        finally
        {
            try { fileDoc?.Close(false); } catch { }
        }

        if (fileHashCache is not null)
        {
            fileHashCache[cacheKey] = file;
        }
        return file;
    }

    /// <summary>
    /// Tri-state comparison: <c>null</c> when either side is indeterminate.
    /// </summary>
    public static bool? Compare(string? embeddedHash, string? fileHash)
    {
        if (embeddedHash is null || fileHash is null)
        {
            return null;
        }
        return string.Equals(embeddedHash, fileHash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Full verification: embedded vs file (the file hash restricted to the
    /// embedded type set — see the class summary). <c>true</c> — content
    /// matches; <c>false</c> — differs; <c>null</c> — indeterminate.
    /// <paramref name="fileHashCache"/> — optional per-run cache, see
    /// <see cref="ComputeFileHash"/>.
    /// </summary>
    public static bool? VerifyEmbeddedAgainstFile(
        Document doc,
        string familyName,
        string absolutePath,
        IFamilySnapshotExtractor snapshotExtractor,
        IFamilyContentHasher contentHasher,
        string logContext,
        IDictionary<string, string?>? fileHashCache = null)
    {
        var (embedded, typeNames) = ComputeEmbeddedHash(doc, familyName, snapshotExtractor, contentHasher, logContext);
        if (embedded is null)
        {
            return null;
        }
        // Type-set rule: restriction ONLY in a project (preserve-types
        // keeps the loaded subset). In a family document the full merge
        // transfers every type — full-vs-full is the honest comparison
        // (a type-adding version bump must mismatch pre-reload).
        var file = ComputeFileHash(
            doc, absolutePath, snapshotExtractor, contentHasher, logContext,
            fileHashCache,
            restrictToTypeNames: doc.IsFamilyDocument ? null : typeNames);
        return Compare(embedded, file);
    }
}
