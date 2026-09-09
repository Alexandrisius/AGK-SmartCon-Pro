using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.Stale;

internal sealed partial class StaleFamilyUpdater
{
    private enum FamilyDocumentUpdateKind
    {
        NotApplicable,
        PreVerified,
        Reloaded,
        Arbitrated,
        Failed,
    }

    /// <summary>
    /// Single-open orchestration of the FAMILY-document update cycle, running
    /// entirely on the Revit thread (inside one RaiseAsync): compute the
    /// embedded hash, open the resolved file ONCE, pre-verify, hand the open
    /// document to the source-aware load service for the poke+merge reload
    /// (borrowed — this method closes it in the finally), then arbitrate a
    /// failed reload / post-verify a successful one against the CACHED file
    /// hash (the file on disk never changes during the cycle — the poke is
    /// net-zero in-memory and the doc-to-doc merge does not touch the
    /// source). Semantics mirror the legacy
    /// <see cref="VerifyEmbeddedMatchesResolvedFileAsync"/> flow exactly;
    /// <see cref="FamilyDocumentUpdateKind.NotApplicable"/> means "fall back
    /// to the legacy path" (project document).
    /// </summary>
    private (FamilyDocumentUpdateKind Kind, string? FamilyName) UpdateInFamilyDocumentOnRevitThread(
        string catalogItemId,
        FamilyResolvedFile resolved,
        IFamilyLoadServiceSourceAware sourceAware,
        bool overwriteParameterValues)
    {
        var doc = _revitContext.GetDocument();
        if (doc is null || !doc.IsFamilyDocument)
        {
            return (FamilyDocumentUpdateKind.NotApplicable, null);
        }

        var name = System.IO.Path.GetFileNameWithoutExtension(resolved.AbsolutePath);
        var resolvedFullPath = System.IO.Path.GetFullPath(resolved.AbsolutePath);

        var (embeddedHash, _) = ComputeEmbeddedVerificationHashOnRevitThread(doc, name, catalogItemId);

        // Same C1 guard as the legacy verify: opening an ALREADY-OPEN file
        // would return the user's live document.
        var alreadyOpen = doc.Application.Documents
            .Cast<Document>()
            .Any(d => !string.IsNullOrEmpty(d.PathName)
                && string.Equals(
                    System.IO.Path.GetFullPath(d.PathName), resolvedFullPath, StringComparison.OrdinalIgnoreCase));
        if (alreadyOpen)
        {
            // Same failure the legacy path produces (the load service's own
            // guard) — reported here directly to avoid duplicate Warns and a
            // doomed reload attempt.
            SmartConLogger.Warn(
                $"UpdateFamily[{catalogItemId}]: the resolved file is open in the editor — " +
                "its on-disk content cannot be trusted for verification " +
                "[Action: закройте файл версии в редакторе (сохранив или отменив правки) и повторите «Обновить»]");
            return (FamilyDocumentUpdateKind.Failed, name);
        }

        string? fileHash = null;
        Document? fileDoc = null;

        // Family-document context: FULL file hash (validator finding
        // 2026-08-14) — the poke + doc-to-doc merge transfers the whole
        // type set, so a type-adding/removing version bump must mismatch
        // pre-reload (else the update is falsely skipped and the new type
        // never lands) and match post-reload (else a landed reload is
        // falsely failed). The type-set restriction is a PROJECT-context
        // rule only — see the legacy verify path.
        string? ComputeFullFileHash(Document openFileDoc)
        {
            // FHV15: the extractor measures at the deterministic reference
            // type (first Ordinal own type) — the embedded side uses the
            // same default rule, and family-doc comparisons are
            // full-vs-full, so the type sets are equal.
            var snap = _snapshotExtractor!.ExtractFromFamilyDocument(openFileDoc);
            return _contentHasher!.ComputeForLoadable(snap)?.HexString;
        }

        try
        {
            fileDoc = doc.Application.OpenDocumentFile(resolved.AbsolutePath);
            fileHash = ComputeFullFileHash(fileDoc);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"UpdateFamily[{catalogItemId}]: could not extract the resolved file for verification: " +
                $"{ex.GetType().Name}: {ex.Message} " +
                "[Action: верификация пропущена — проверьте, что файл версии доступен на диске]");
        }

        try
        {
            var canCompare = embeddedHash is not null && fileHash is not null;
            if (canCompare && string.Equals(embeddedHash, fileHash, StringComparison.OrdinalIgnoreCase))
            {
                SmartConLogger.Info(
                    $"UpdateFamily[{catalogItemId}]: verified — embedded '{name}' matches the resolved file ({resolved.VersionLabel})");
                SmartConLogger.Info(
                    $"UpdateFamily[{catalogItemId}]: embedded content already matches catalog " +
                    $"{resolved.VersionLabel} — reload skipped, writing marker only");
                return (FamilyDocumentUpdateKind.PreVerified, name);
            }
            if (canCompare)
            {
                var a = embeddedHash!.Length > 8 ? embeddedHash[..8] : embeddedHash;
                var e = fileHash!.Length > 8 ? fileHash[..8] : fileHash;
                SmartConLogger.Info(
                    $"UpdateFamily[{catalogItemId}]: pre-verify — embedded '{name}' differs from the resolved file " +
                    $"{resolved.VersionLabel} (embedded {a}… ≠ file {e}…) — reload will run");
            }

            var capturedSource = fileDoc;
            var result = sourceAware.ReloadNestedInFamilyDocument(
                resolvedFullPath,
                name,
                overwriteParameterValues,
                preOpenedSourceDocProvider: () => capturedSource);

            // The source document is no longer needed — arbitration and
            // post-verify compare against the CACHED file hash. Retry the
            // file-hash extraction once when it failed earlier (transient),
            // then close the document BEFORE any further EditFamily: the M2
            // guard in ComputeEmbeddedVerificationHashOnRevitThread would
            // otherwise trip on our own background-open document (its Title
            // equals the family name) and every post-verify/arbitration would
            // return "not applicable" (validator finding, gate 2026-08-11).
            if (fileHash is null && fileDoc is not null)
            {
                try
                {
                    fileHash = ComputeFullFileHash(fileDoc);
                }
                catch (Exception ex)
                {
                    SmartConLogger.Warn(
                        $"UpdateFamily[{catalogItemId}]: file-hash retry after reload failed: " +
                        $"{ex.GetType().Name}: {ex.Message} " +
                        "[Action: верификация пропущена — повторите «Проверить»]");
                }
            }
            if (fileDoc is not null)
            {
                try { fileDoc.Close(false); } catch { }
                fileDoc = null;
            }

            if (!result.Success)
            {
                // Failed-reload arbitration: content may already match (the
                // "unchanged" false-failure) — treat as success.
                var (postArb, _) = ComputeEmbeddedVerificationHashOnRevitThread(doc, name, catalogItemId);
                if (postArb is not null && fileHash is not null
                    && string.Equals(postArb, fileHash, StringComparison.OrdinalIgnoreCase))
                {
                    SmartConLogger.Info(
                        $"UpdateFamily[{catalogItemId}]: reload reported failure but embedded content " +
                        $"matches catalog {resolved.VersionLabel} — treating as already up-to-date");
                    return (FamilyDocumentUpdateKind.Arbitrated, result.FamilyName ?? name);
                }
                return (FamilyDocumentUpdateKind.Failed, result.FamilyName ?? name);
            }

            if (fileHash is not null)
            {
                var (post, _) = ComputeEmbeddedVerificationHashOnRevitThread(doc, name, catalogItemId);
                if (post is null)
                {
                    // Same tri-state rule as the legacy policy: null is
                    // indeterminate (transient EditFamily/extract failure
                    // right after a successful merge) — trust the reload;
                    // the next Check/Update re-verifies.
                    SmartConLogger.Info(
                        $"UpdateFamily[{catalogItemId}]: post-verify indeterminate for '{name}' — trusting the successful reload");
                }
                else if (!string.Equals(post, fileHash, StringComparison.OrdinalIgnoreCase))
                {
                    var a = post.Length > 8 ? post[..8] : post;
                    var e = fileHash.Length > 8 ? fileHash[..8] : fileHash;
                    SmartConLogger.Warn(
                        $"UpdateFamily[{catalogItemId}]: POST-RELOAD VERIFICATION FAILED for '{name}' — embedded content " +
                        $"does not match the resolved file {resolved.VersionLabel} (embedded hash {a}… ≠ file {e}…). " +
                        "No marker is written; the family stays stale. " +
                        "[Action: обновление не заменило дефиницию — откройте родительское " +
                        "семейство в редакторе, удалите проблемное вложенное и загрузите его заново из каталога (привязки " +
                        "придётся восстановить), либо пересоберите родителя; затем повторите «Проверить»]");
                    return (FamilyDocumentUpdateKind.Failed, result.FamilyName ?? name);
                }
                else
                {
                    SmartConLogger.Info(
                        $"UpdateFamily[{catalogItemId}]: verified — embedded '{name}' matches the resolved file ({resolved.VersionLabel})");
                }
            }

            return (FamilyDocumentUpdateKind.Reloaded, result.FamilyName ?? name);
        }
        finally
        {
            try { fileDoc?.Close(false); } catch { }
        }
    }
}
