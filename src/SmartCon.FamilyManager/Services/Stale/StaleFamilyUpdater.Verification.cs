using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.Stale;

internal sealed partial class StaleFamilyUpdater
{
    /// <summary>
    /// #209 round-4 + #222: content verification for BOTH the family-document
    /// and the project context. Compares the unified FHV10 content hash
    /// (<see cref="IFamilyContentHasher.ComputeForLoadable"/>)
    /// of the family embedded/loaded in the active document against
    /// the same hash of the resolved source .rfa — both extracted in
    /// the same live session, so the comparison is context-consistent
    /// (probe 2026-08-12: the embedded EditFamily document keeps the
    /// authored state byte-for-byte even under a host drive — the merge
    /// transfers everything except parameter groups, which FHV10 no longer
    /// hashes at all; the check side applies the same proof to project-loaded
    /// families, see StaleCheckContentFallbackTests). The file hash is
    /// restricted to the embedded type set ONLY in a project (type-set rule,
    /// stress test 2026-08-14) — a partially loaded multi-type family
    /// otherwise mismatches the TYPES section structurally; in a family
    /// document the comparison is full-vs-full because the merge transfers
    /// every type.
    /// Tri-state: <c>true</c> — embedded matches the file; <c>false</c> —
    /// differs (the reload did not land and the update MUST fail before
    /// any marker is written); <c>null</c> — indeterminate (file unreadable;
    /// family not found in the document; open in the editor; transient
    /// extraction failure). Callers must distinguish via
    /// <see cref="StaleUpdateVerificationPolicy"/>: only an explicit
    /// <c>true</c> may skip/arbitrate a reload, only an explicit
    /// <c>false</c> fails an otherwise successful one.
    /// </summary>
    private async Task<bool?> VerifyEmbeddedMatchesResolvedFileAsync(
        string catalogItemId,
        FamilyResolvedFile resolved,
        string? familyName,
        CancellationToken ct,
        bool isPostReload = true)
    {
        var name = !string.IsNullOrEmpty(familyName)
            ? familyName!
            : System.IO.Path.GetFileNameWithoutExtension(resolved.AbsolutePath);

        var (embeddedHash, fileHash) = await _awaitable.RaiseAsync<(string?, string?)>(app =>
        {
            // TryGetDocument (#219): zero-document state is a quiet
            // "indeterminate", never an NRE.
            var doc = _revitContext.TryGetDocument();
            if (doc is null)
            {
                return (null, null);
            }

            var (embedded, typeNames) = ComputeEmbeddedVerificationHashOnRevitThread(doc, name, catalogItemId);
            if (embedded is null)
            {
                return (null, null);
            }

            // Type-set rule (stress test 2026-08-14, validator round): the
            // file hash is restricted to the embedded type set ONLY in a
            // project (preserve-types reload keeps the loaded subset —
            // an unrestricted comparison never matches). In a family
            // document the full merge transfers every type, so the
            // comparison stays full-vs-full: a type-adding version bump
            // must mismatch pre-reload, otherwise the update would be
            // falsely skipped and the new type would never land.
            var file = EmbeddedContentVerifier.ComputeFileHash(
                doc, resolved.AbsolutePath, _snapshotExtractor!, _contentHasher!,
                $"UpdateFamily[{catalogItemId}]",
                restrictToTypeNames: doc.IsFamilyDocument ? null : typeNames);
            if (file is null)
            {
                return (null, null);
            }

            // Post-reload mismatch diagnostics (#239 follow-up, manual test
            // 2026-08-23): a failed post-verify must name the differing
            // content, not just two hash prefixes — log the first differing
            // canonical tokens with their section context. Failure-only:
            // costs one extra EditFamily + file open per failed update.
            if (isPostReload && !string.Equals(embedded, file, StringComparison.OrdinalIgnoreCase))
            {
                LogVerificationCanonicalDiff(doc, name, resolved.AbsolutePath, typeNames, catalogItemId);
            }

            return (embedded, file);
        }, ct).ConfigureAwait(true);

        if (embeddedHash is null || fileHash is null)
        {
            return null;
        }

        if (string.Equals(embeddedHash, fileHash, StringComparison.OrdinalIgnoreCase))
        {
            SmartConLogger.Info(
                $"UpdateFamily[{catalogItemId}]: verified — embedded '{name}' matches the resolved file ({resolved.VersionLabel})");
            return true;
        }

        var actualShort = embeddedHash.Length > 8 ? embeddedHash[..8] : embeddedHash;
        var expectedShort = fileHash.Length > 8 ? fileHash[..8] : fileHash;
        if (!isPostReload)
        {
            // Pre-verify mismatch is the NORMAL "reload needed" branch —
            // not a failure. The scary Warn is reserved for real
            // post-reload failures (manual test 2026-08-11: the pre-verify
            // Warn read as "the family stays stale" right before the
            // reload succeeded and the marker was written).
            SmartConLogger.Info(
                $"UpdateFamily[{catalogItemId}]: pre-verify — embedded '{name}' differs from the resolved file " +
                $"{resolved.VersionLabel} (embedded {actualShort}… ≠ file {expectedShort}…) — reload will run");
            return false;
        }

        SmartConLogger.Warn(
            $"UpdateFamily[{catalogItemId}]: POST-RELOAD VERIFICATION FAILED for '{name}' — embedded content " +
            $"does not match the resolved file {resolved.VersionLabel} (embedded hash {actualShort}… ≠ file {expectedShort}…). " +
            "No marker is written; the family stays stale. " +
            "[Action: обновление не заменило дефиницию — откройте родительское " +
            "семейство в редакторе, удалите проблемное вложенное и загрузите его заново из каталога (привязки " +
            "придётся восстановить), либо пересоберите родителя; затем повторите «Проверить»]");
        return false;
    }

    /// <summary>
    /// Verification-grade hash of a family nested inside an open document —
    /// delegates to <see cref="EmbeddedContentVerifier"/> (shared with the
    /// stale-check content fallback). Returns the hash AND the embedded
    /// type-name set: the file hash is restricted to the same set ONLY in a
    /// project (type-set rule — a partially loaded family otherwise never
    /// verifies); the family-document orchestrated path hashes the file
    /// full-vs-full and ignores the names.
    /// </summary>
    private (string? Hash, IReadOnlyList<string> TypeNames) ComputeEmbeddedVerificationHashOnRevitThread(
        Document doc, string familyName, string catalogItemId)
    {
        return EmbeddedContentVerifier.ComputeEmbeddedHash(
            doc, familyName, _snapshotExtractor!, _contentHasher!, $"UpdateFamily[{catalogItemId}]");
    }

    /// <summary>
    /// Post-verify failure diagnostics (#239 follow-up, manual test
    /// 2026-08-23): recomputes the embedded and file snapshots (same
    /// restriction as the verification) and logs the first differing
    /// canonical tokens with the nearest section marker (PARAMS/TYPES/GEOM/
    /// CONN/LOOKUP/…), so the operator sees WHICH content diverged instead
    /// of two opaque hash prefixes. Revit thread, failure-path only — every
    /// step is individually guarded (diagnostics must never break the flow).
    /// </summary>
    private void LogVerificationCanonicalDiff(
        Document doc, string familyName, string filePath, IReadOnlyList<string> typeNames, string catalogItemId)
    {
        try
        {
            string? embeddedCanonical = null;
            string? fileCanonical = null;

            var nested = new FilteredElementCollector(doc)
                .OfClass(typeof(Autodesk.Revit.DB.Family))
                .Cast<Autodesk.Revit.DB.Family>()
                .FirstOrDefault(f => string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase));
            if (nested is not null)
            {
                Document? copy = null;
                try
                {
                    copy = doc.EditFamily(nested);
                    // FHV15: the extractor measures at the deterministic
                    // reference type — no committed alignment needed.
                    embeddedCanonical = _contentHasher!
                        .BuildLoadableCanonicalStringForDiagnostics(
                            _snapshotExtractor!.ExtractFromFamilyDocument(copy));
                }
                finally
                {
                    try { copy?.Close(false); } catch { }
                }
            }

            Document? fileDoc = null;
            try
            {
                fileDoc = doc.Application.OpenDocumentFile(filePath);
                // FHV15: the restriction set doubles as the reference-type
                // preference — the extractor measures both sides at the
                // same intersection type.
                var fileSnapshot = _snapshotExtractor!.ExtractFromFamilyDocument(
                    fileDoc, doc.IsFamilyDocument ? null : typeNames);
                if (!doc.IsFamilyDocument)
                {
                    var allowed = new HashSet<string>(typeNames, StringComparer.OrdinalIgnoreCase);
                    fileSnapshot = fileSnapshot with
                    {
                        Types = fileSnapshot.Types.Where(t => allowed.Contains(t.Name)).ToList(),
                    };
                }

                fileCanonical = _contentHasher!.BuildLoadableCanonicalStringForDiagnostics(fileSnapshot);
            }
            finally
            {
                try { fileDoc?.Close(false); } catch { }
            }

            if (embeddedCanonical is null || fileCanonical is null)
            {
                SmartConLogger.Info(
                    $"UpdateFamily[{catalogItemId}]: VERIFY-DIFF unavailable (embedded={embeddedCanonical is not null}, " +
                    $"file={fileCanonical is not null})");
                return;
            }

            var e = embeddedCanonical.Split('|');
            var f = fileCanonical.Split('|');
            var shown = 0;
            var max = Math.Max(e.Length, f.Length);
            for (var i = 0; i < max && shown < 8; i++)
            {
                var et = i < e.Length ? e[i] : "<end>";
                var ft = i < f.Length ? f[i] : "<end>";
                if (et == ft) continue;

                shown++;
                var section = FindSectionMarker(e, i);
                SmartConLogger.Info(
                    $"UpdateFamily[{catalogItemId}]: VERIFY-DIFF[{shown}] section≈{section} token#{i}: " +
                    $"embedded=[{Truncate(et)}] file=[{Truncate(ft)}]");
            }

            SmartConLogger.Info(
                $"UpdateFamily[{catalogItemId}]: VERIFY-DIFF summary: tokens embedded={e.Length} file={f.Length}, " +
                $"first {shown} difference(s) shown");
        }
        catch (Exception ex)
        {
            SmartConLogger.Info(
                $"UpdateFamily[{catalogItemId}]: VERIFY-DIFF failed (diagnostics only): {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static readonly string[] SectionMarkers =
    [
        "PARAMS", "TYPES", "PHANTOM", "GEOM", "GEOM2D", "NESTED", "NONSHARED",
        "NESTEDHASH", "FACTS", "FLAGS", "CONN", "LOOKUP", "STRUCT", "ROUTING",
    ];

    private static string FindSectionMarker(string[] tokens, int index)
    {
        for (var i = index; i >= 0; i--)
        {
            if (Array.IndexOf(SectionMarkers, tokens[i]) >= 0)
                return tokens[i];
        }

        return "<prefix>";
    }

    private static string Truncate(string token)
    {
        return token.Length > 60 ? token[..60] + "…" : token;
    }
}
