using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.Geometry;

/// <summary>
/// Default implementation of <see cref="IFamilyGeometryPipeline"/>. Combines
/// <see cref="IFamilyGeometryExtractor"/> (Revit-side), <see cref="IGlbWriter"/>
/// (custom zero-dependency GLB writer, pure C#), and <see cref="IFamilyAssetService"/> (managed-storage
/// asset registration) into a single best-effort pipeline.
/// </summary>
/// <remarks>
/// <b>Threading:</b> the extractor — which calls Revit API
/// (<c>OpenDocumentFile</c>, <c>get_Geometry</c>, <c>Face.Triangulate</c>) —
/// is invoked via <see cref="IFamilyManagerAwaitableEvent.RaiseAsync{T}"/>
/// so it runs on the Revit UI thread (I-01). The GLB write, asset cleanup
/// and registration happen on the calling (background) thread using their
/// own async SQLite/I/O operations.
/// <para>
/// <b>CAS preview pool (#249, Phase 5):</b> generated GLBs live in the
/// shared content-addressed pool
/// (<c>files/_shared/models/{shard2}/{view3dHash-40}.glb</c>) — immutable
/// files keyed by the VIEW3D hash of their normalized per-type inputs.
/// Two reuse tiers: (1) the whole pipeline is SKIPPED when the new
/// version's DEF/GEOM/TYPES section hashes match another version of the
/// same item (the preview assets are simply re-linked); (2) per type,
/// the GLB serialization+write is skipped when the pool already holds
/// the file for the type's VIEW3D hash (a geometry edit of one type
/// reuses every other type's preview — across versions AND families).
/// Legacy extraction products without a preview snapshot
/// (<see cref="FamilyGeometryPerType.Preview"/> == <c>null</c>) fall back
/// to the pre-CAS per-version write.
/// </para>
/// <para>
/// <b>Failure contract:</b> all exceptions are caught and logged as Warn
/// with an <c>[Action: ...]</c> suggestion (smartcon-logging L9). The
/// pipeline NEVER rethrows — the import transaction already committed
/// before the hook was reached, and a preview failure must not abort the
/// import UX.
/// </para>
/// </remarks>
public sealed partial class FamilyGeometryPipeline : IFamilyGeometryPipeline
{
    private readonly IFamilyGeometryExtractor _extractor;
    private readonly IGlbWriter _glbWriter;
    private readonly IFamilyAssetService _assetService;
    private readonly IFamilyManagerAwaitableEvent _awaitableEvent;
    private readonly LocalCatalog.LocalCatalogDatabase _database;
    private readonly LocalCatalog.StoragePathResolver _pathResolver;

    public FamilyGeometryPipeline(
        IFamilyGeometryExtractor extractor,
        IGlbWriter glbWriter,
        IFamilyAssetService assetService,
        IFamilyManagerAwaitableEvent awaitableEvent,
        LocalCatalog.LocalCatalogDatabase database)
    {
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _glbWriter = glbWriter ?? throw new ArgumentNullException(nameof(glbWriter));
        _assetService = assetService ?? throw new ArgumentNullException(nameof(assetService));
        _awaitableEvent = awaitableEvent ?? throw new ArgumentNullException(nameof(awaitableEvent));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _pathResolver = new LocalCatalog.StoragePathResolver(database);
    }

    public async Task RunAsync(
        IReadOnlyList<FamilyGeometryPerType>? geometryPerType,
        string? managedRfaPath,
        string catalogItemId,
        string versionId,
        string versionLabel,
        string familyName,
        IReadOnlyDictionary<string, string>? overwriteBaselineSectionHashes = null,
        CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope("Geo3DPipeline",
            ("Method", nameof(RunAsync)),
            ("CatalogItemId", catalogItemId),
            ("VersionLabel", versionLabel),
            ("VersionId", versionId),
            ("FilePath", Path.GetFileName(managedRfaPath ?? "")));

        SmartConLogger.Info(
            $"Geometry pipeline start: family='{familyName}', v='{versionLabel}', " +
            $"hasPreextractedGeometry={geometryPerType is not null && geometryPerType.Count > 0}, " +
            $"managedRfaPath='{Path.GetFileName(managedRfaPath ?? "")}'");

        // 0a. #252 (overwrite baseline): OverwriteCurrent rewrites the
        //     version row BEFORE this hook runs, so the tier-1 check below
        //     can only compare against OTHER versions — for an overwrite
        //     that baseline (v3 for v4) almost always differs and the
        //     re-link never fires. The import service therefore captured
        //     the PRE-OVERWRITE sections of the same version: when the new
        //     content still matches them on the preview-relevant keys, the
        //     existing pooled previews are already correct — keep them
        //     untouched and skip the deletion AND the extraction entirely.
        if (overwriteBaselineSectionHashes is not null
            && await CurrentSectionsMatchBaselineAsync(
                catalogItemId, versionLabel, overwriteBaselineSectionHashes, ct).ConfigureAwait(false))
        {
            SmartConLogger.Info(
                $"Preview reuse (overwrite): DEF/GEOM/TYPES/NESTED* sections match the pre-overwrite " +
                $"content of {versionLabel} — existing pooled previews kept, extraction and GLB writes skipped");
            return;
        }

        // 0. #249 (Phase 5): delete any previous auto-extracted Preview
        //    assets for this (catalog_item_id, version_label) FIRST —
        //    OverwriteCurrent (ADR-040) re-imports the same version with
        //    new geometry, and both reuse tiers below INSERT rows;
        //    without this order the overwrite would duplicate them.
        await DeletePreviousAutoExtractedAssetAsync(catalogItemId, versionLabel, ct).ConfigureAwait(false);

        // 1. #249 (Phase 5), reuse tier 1: when another version of the
        //    same item carries identical DEF/GEOM/TYPES/NESTED* section
        //    hashes, the per-type preview content is identical by
        //    construction — re-link its POOLED preview assets instead of
        //    re-extracting and re-writing anything (a text-only edit
        //    costs ZERO Revit work and ZERO new files).
        if (await TryReuseFromPreviousVersionAsync(catalogItemId, versionLabel, ct).ConfigureAwait(false))
        {
            return;
        }

        // 2. Obtain geometry: either pre-extracted from Prepare (H1)
        //    or extract now from managed .rfa (H2/H3).
        IReadOnlyList<FamilyGeometryPerType>? geometry = geometryPerType;

        if (geometry is null || geometry.Count == 0)
        {
            if (string.IsNullOrEmpty(managedRfaPath))
            {
                SmartConLogger.Warn(
                    $"Geometry pipeline skipped: no pre-extracted geometry and no managedRfaPath for '{familyName}' v{versionLabel} " +
                    "[Action: import continues; 3D preview will be unavailable for this version]");
                return;
            }

            SmartConLogger.Info(
                $"Geometry pipeline extracting from managed .rfa: '{Path.GetFileName(managedRfaPath)}'");

            // H2/H3 path: extract geometry from managed .rfa (one OpenDocumentFile).
            // Use RaiseAsyncTask rather than RaiseAsync<T> + GetAwaiter().GetResult()
            // so a future truly-async extractor cannot deadlock on the Revit UI thread.
            IReadOnlyList<FamilyGeometryPerType>? extracted = null;
            await _awaitableEvent.RaiseAsyncTask(
                async _ => extracted = await _extractor.ExtractAsync(managedRfaPath!, familyName, ct).ConfigureAwait(false),
                ct).ConfigureAwait(false);
            geometry = extracted;

            if (geometry is null || geometry.Count == 0)
            {
                SmartConLogger.Warn(
                    $"Geometry pipeline skipped: no preview extracted for '{familyName}' v{versionLabel} " +
                    "[Action: verify family has visible 3D solids; 3D preview will be unavailable for this version]");
                // #157: the family legitimately has no extractable 3D
                // (2D/annotation-only symbol). Write the terminal marker so
                // the glb-v1 detection clears instead of pending forever.
                await WriteGlbStateAsync(catalogItemId, versionLabel, -1, ct).ConfigureAwait(false);
                return;
            }

            SmartConLogger.Info(
                $"Geometry pipeline extracted {geometry.Count} type(s) from '{Path.GetFileName(managedRfaPath)}'");
        }
        else
        {
            SmartConLogger.Info(
                $"Geometry pipeline using pre-extracted geometry: {geometry.Count} type(s)");
        }

        // 3. Write N GLBs (one per type) and register each as a Model3D asset.
        var writtenCount = 0;
        var reusedCount = 0;
        var skippedEmptyCount = 0;
        foreach (var gpt in geometry)
        {
            ct.ThrowIfCancellationRequested();

            if (gpt.IsEmpty)
            {
                SmartConLogger.Info(
                    $"Skipping type '{gpt.TypeName}' — empty geometry");
                skippedEmptyCount++;
                continue;
            }

            // Description encodes the type name so the UI can
            // filter/select per-type assets.
            var description = gpt.TypeName.Length == 0
                ? FamilyGeometryGlbWriter.AutoExtractedAssetDescriptionPrefix + familyName + "::"
                : FamilyGeometryGlbWriter.AutoExtractedAssetDescriptionPrefix + familyName + "::" + gpt.TypeName;

            // #249 (Phase 5), reuse tier 2 (per-type CAS): the VIEW3D hash
            // of the type's normalized preview inputs keys the shared
            // pool — a hit skips the GLB serialization AND the file write;
            // the version simply references the pooled file. Legacy
            // extraction products without a preview snapshot fall back to
            // the pre-CAS per-version write.
            if (gpt.Preview is not null)
            {
                var view3dHash = FamilyPreviewHasher.ComputeForType(gpt.Preview);
                if (view3dHash is not null)
                {
                    var pooledRelPath = LocalCatalog.StoragePathResolver.GetSharedPreviewRelativePath(view3dHash);
                    var pooledAbsPath = _pathResolver.GetSharedPreviewFilePath(view3dHash);
                    var poolHit = File.Exists(pooledAbsPath);
                    if (!poolHit)
                    {
                        if (!await WriteGlbToPoolAsync(gpt, pooledAbsPath, view3dHash, ct).ConfigureAwait(false))
                        {
                            SmartConLogger.Warn(
                                $"GLB write failed for type '{gpt.TypeName}' '{familyName}' v{versionLabel} " +
                                "[Action: import continues; this type's 3D preview will be unavailable]");
                            continue;
                        }
                    }

                    var pooledAsset = await _assetService.RegisterPooledAssetAsync(
                        catalogItemId, versionLabel, FamilyAssetType.Model3D, pooledRelPath, description, ct).ConfigureAwait(false);
                    writtenCount++;
                    if (poolHit) reusedCount++;

                    SmartConLogger.Info(
                        $"Geometry pipeline {(poolHit ? "REUSED" : "OK")}: type='{gpt.TypeName}', " +
                        $"view3d={view3dHash[..12]}…, asset={pooledAsset.Id}, " +
                        $"{gpt.Meshes.Count} meshes, {gpt.TotalTriangleCount} triangles");
                    continue;
                }
            }

            // Pre-CAS fallback (legacy extraction products without a
            // preview snapshot — unit-test fakes and pre-#249 callers).
            var preview = new FamilyGeometryPreview(
                catalogItemId, versionLabel,
                string.IsNullOrEmpty(gpt.TypeName) ? gpt.FamilyName : $"{gpt.FamilyName} [{gpt.TypeName}]",
                gpt.Meshes);

            string? tempPath = null;
            try
            {
                tempPath = Path.Combine(Path.GetTempPath(),
                    $"sc_preview_{Guid.NewGuid().ToString("N")}.glb");
                var ok = await _glbWriter.WriteAsync(preview, tempPath, ct).ConfigureAwait(false);
                if (!ok)
                {
                    SmartConLogger.Warn(
                        $"GLB write failed for type '{gpt.TypeName}' '{familyName}' v{versionLabel} " +
                        "[Action: import continues; this type's 3D preview will be unavailable]");
                    continue;
                }

                var asset = await _assetService.AddAssetAsync(
                    catalogItemId, versionLabel, FamilyAssetType.Model3D, tempPath, description, ct).ConfigureAwait(false);
                writtenCount++;

                SmartConLogger.Info(
                    $"Geometry pipeline OK (legacy path): type='{gpt.TypeName}', GLB asset={asset.Id}, file='{asset.FileName}', " +
                    $"{gpt.Meshes.Count} meshes, {gpt.TotalTriangleCount} triangles");
            }
            finally
            {
                if (tempPath is not null)
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                    catch (Exception cleanupEx)
                    {
                        SmartConLogger.Debug($"Temp GLB cleanup failed for '{Path.GetFileName(tempPath)}': {cleanupEx.Message}");
                    }
                }
            }
        }

        SmartConLogger.Info(
            $"Geometry pipeline finished: family='{familyName}', v='{versionLabel}', " +
            $"written={writtenCount} (reusedFromPool={reusedCount}), skippedEmpty={skippedEmptyCount}, totalTypes={geometry.Count}");

        if (writtenCount > 0)
        {
            // #157 heal: a previous terminal marker (family had no geometry
            // at an earlier import) is obsolete — the family now produces
            // real previews. Clear it on every variant of the label.
            await WriteGlbStateAsync(catalogItemId, versionLabel, null, ct).ConfigureAwait(false);
        }
        else if (skippedEmptyCount == geometry.Count)
        {
            // Every type produced empty geometry — genuinely no 3D content.
            // GLB write failures are NOT marked: those are transient and
            // must stay pending for a retry.
            await WriteGlbStateAsync(catalogItemId, versionLabel, -1, ct).ConfigureAwait(false);
        }
    }

}
