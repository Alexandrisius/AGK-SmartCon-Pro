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
/// </summary>
internal static class EmbeddedContentVerifier
{
    /// <summary>
    /// Verification-grade hash of a family nested inside an open family
    /// document: EditFamily → snapshot →
    /// <see cref="IFamilyContentHasher.ComputeForLoadable"/> (FHV10: the
    /// unified hash — fair on an embedded document, groups aside they are
    /// not hashed at all).
    /// Returns <c>null</c> when the family is not found or is open as a
    /// top-level document (EditFamily would return the user's live document
    /// with unsaved edits, and closing it would destroy them — skip instead).
    /// </summary>
    public static string? ComputeEmbeddedHash(
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
                $"{logContext}: family '{familyName}' not found in the family document " +
                "[Action: верификация пропущена — семейство не вложено в активный документ]");
            return null;
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
            return null;
        }

        Document? copy = null;
        try
        {
            copy = doc.EditFamily(nested);
            var snapshot = snapshotExtractor.ExtractFromFamilyDocument(copy);
            return contentHasher.ComputeForLoadable(snapshot)?.HexString;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"{logContext}: embedded hash computation failed for '{familyName}': {ex.GetType().Name}: {ex.Message} " +
                "[Action: верификация пропущена — семейство останется stale, повторите «Обновить»]");
            return null;
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
    /// </summary>
    public static string? ComputeFileHash(
        Document doc,
        string absolutePath,
        IFamilySnapshotExtractor snapshotExtractor,
        IFamilyContentHasher contentHasher,
        string logContext,
        IDictionary<string, string?>? fileHashCache = null)
    {
        var resolvedFullPath = System.IO.Path.GetFullPath(absolutePath);
        if (fileHashCache is not null && fileHashCache.TryGetValue(resolvedFullPath, out var cached))
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
                fileHashCache[resolvedFullPath] = null;
            }
            return null;
        }

        string? file = null;
        Document? fileDoc = null;
        try
        {
            fileDoc = doc.Application.OpenDocumentFile(absolutePath);
            var snap = snapshotExtractor.ExtractFromFamilyDocument(fileDoc);
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
            fileHashCache[resolvedFullPath] = file;
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
    /// Full verification: embedded vs file. <c>true</c> — content matches;
    /// <c>false</c> — differs; <c>null</c> — indeterminate.
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
        var embedded = ComputeEmbeddedHash(doc, familyName, snapshotExtractor, contentHasher, logContext);
        if (embedded is null)
        {
            return null;
        }
        var file = ComputeFileHash(doc, absolutePath, snapshotExtractor, contentHasher, logContext, fileHashCache);
        return Compare(embedded, file);
    }
}
