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
public sealed class FamilyGeometryPipeline : IFamilyGeometryPipeline
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

    /// <summary>
    /// #249 (Phase 5), reuse tier 1: re-links the previous version's
    /// pooled preview assets when its DEF/GEOM/TYPES section hashes
    /// match the CURRENT version's stored section hashes (written by the
    /// import transaction before this hook runs). A match proves the
    /// per-type preview inputs are identical — no extraction, no GLB
    /// write, no new files. Returns <c>true</c> when the pipeline's work
    /// is done (assets re-linked or the terminal no-geometry marker
    /// carried over).
    /// </summary>
    private async Task<bool> TryReuseFromPreviousVersionAsync(
        string catalogItemId, string versionLabel, CancellationToken ct)
    {
        try
        {
            using var connection = _database.CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);

            // The CURRENT version's section hashes — no analytics (legacy
            // import path) → no reuse decision possible.
            string? currentJson;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT section_hashes FROM catalog_versions
                    WHERE catalog_item_id = @itemId AND version_label = @label
                    LIMIT 1
                    """;
                cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
                cmd.Parameters.Add(new SqliteParameter("@label", versionLabel));
                currentJson = Convert.ToString(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            }
            var current = ContentSectionJsonSerializer.Deserialize(currentJson);
            if (current is null)
            {
                return false;
            }

            // The latest OTHER version with section analytics.
            string? previousLabel = null;
            string? previousJson = null;
            int? previousGlbState = null;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT version_label, section_hashes, glb_state FROM catalog_versions
                    WHERE catalog_item_id = @itemId AND version_label <> @label
                      AND section_hashes IS NOT NULL
                    ORDER BY published_at_utc DESC
                    LIMIT 1
                    """;
                cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
                cmd.Parameters.Add(new SqliteParameter("@label", versionLabel));
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    previousLabel = reader.GetString(0);
                    previousJson = reader.IsDBNull(1) ? null : reader.GetString(1);
                    previousGlbState = reader.IsDBNull(2) ? null : reader.GetInt32(2);
                }
            }
            var previous = ContentSectionJsonSerializer.Deserialize(previousJson);
            if (previous is null || previousLabel is null)
            {
                return false;
            }

            foreach (var key in new[] { "DEF", "GEOM", "TYPES", "NESTED", "NONSHARED", "NESTEDHASH" })
            {
                if (!current.TryGetValue(key, out var currentHash)
                    || !previous.TryGetValue(key, out var previousHash)
                    || !string.Equals(currentHash, previousHash, StringComparison.Ordinal))
                {
                    SmartConLogger.Debug(
                        $"Preview reuse: section '{key}' differs from {previousLabel} — full pipeline");
                    return false;
                }
            }

            // The previous version was a terminal no-geometry family —
            // the same content yields the same verdict, carry the marker.
            if (previousGlbState == -1)
            {
                await WriteGlbStateAsync(catalogItemId, versionLabel, -1, ct).ConfigureAwait(false);
                SmartConLogger.Info(
                    $"Preview reuse: carried over the terminal no-geometry marker from {previousLabel}");
                return true;
            }

            // Re-link the previous version's POOLED auto-preview rows
            // (they reference pool files — the rows, never the bytes).
            // LEGACY (pre-CAS) rows point inside the previous version's
            // own directory — re-linking them would break the preview the
            // moment that directory is deleted with the old version
            // (validator HIGH-1). Only pool paths are re-linkable; a
            // previous version without pooled rows sends us through the
            // full pipeline (which then writes into the pool).
            var linked = 0;
            var previousAssets = new List<(string FileName, string RelativePath, long SizeBytes, string? Description)>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT file_name, relative_path, size_bytes, description FROM family_assets
                    WHERE catalog_item_id = @itemId AND version_label = @prevLabel
                      AND asset_type = 'Model3D' AND description LIKE @prefix
                      AND relative_path LIKE @poolPrefix
                    """;
                cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
                cmd.Parameters.Add(new SqliteParameter("@prevLabel", previousLabel));
                cmd.Parameters.Add(new SqliteParameter("@prefix",
                    FamilyGeometryGlbWriter.AutoExtractedAssetDescriptionPrefix + "%"));
                cmd.Parameters.Add(new SqliteParameter("@poolPrefix",
                    LocalCatalog.StoragePathResolver.SharedPreviewPoolRelativePrefix + "%"));
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    previousAssets.Add((
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetInt64(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3)));
                }
            }

            if (previousAssets.Count == 0)
            {
                return false;
            }

            var now = DateTimeOffset.UtcNow.ToString("o");
            foreach (var asset in previousAssets)
            {
                using var insertCmd = connection.CreateCommand();
                insertCmd.CommandText = """
                    INSERT INTO family_assets (id, catalog_item_id, version_label, asset_type, file_name, relative_path, size_bytes, description, created_at_utc, is_primary)
                    VALUES (@id, @itemId, @label, 'Model3D', @fileName, @relPath, @size, @description, @created, 0)
                    """;
                insertCmd.Parameters.Add(new SqliteParameter("@id", Guid.NewGuid().ToString()));
                insertCmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
                insertCmd.Parameters.Add(new SqliteParameter("@label", versionLabel));
                insertCmd.Parameters.Add(new SqliteParameter("@fileName", asset.FileName));
                insertCmd.Parameters.Add(new SqliteParameter("@relPath", asset.RelativePath));
                insertCmd.Parameters.Add(new SqliteParameter("@size", asset.SizeBytes));
                insertCmd.Parameters.Add(new SqliteParameter("@description", (object?)asset.Description ?? DBNull.Value));
                insertCmd.Parameters.Add(new SqliteParameter("@created", now));
                await insertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                linked++;
            }

            await WriteGlbStateAsync(catalogItemId, versionLabel, null, ct).ConfigureAwait(false);
            SmartConLogger.Info(
                $"Preview reuse: re-linked {linked} pooled preview asset(s) from {previousLabel} " +
                $"(DEF/GEOM/TYPES sections match) — extraction and GLB writes skipped");
            return true;
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"TryReuseFromPreviousVersionAsync skipped: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Writes one type's GLB into the shared CAS pool: serialize to a
    /// temp file IN THE TARGET SHARD DIRECTORY (same volume → the move
    /// is an atomic rename; a %TEMP%-based move could degrade to
    /// copy+delete and publish a truncated file on a crash — validator
    /// LOW-1), then rename onto the pool path. A concurrent writer
    /// winning the race is fine: pool files are immutable and
    /// content-identical for the same hash, so an
    /// <see cref="IOException"/> from the losing rename is treated as a
    /// race-win, never as a failure (validator MED-3). Returns
    /// <c>false</c> only on a GLB serialization failure.
    /// </summary>
    private async Task<bool> WriteGlbToPoolAsync(
        FamilyGeometryPerType gpt, string pooledAbsPath, string view3dHash, CancellationToken ct)
    {
        var preview = new FamilyGeometryPreview(
            string.Empty, string.Empty,
            // #249 (Phase 5): content-pure bytes — a NEUTRAL root node
            // name (no family/type names in the pooled bytes; the asset
            // row's description carries the display name).
            "preview",
            gpt.Meshes);

        string? tempPath = null;
        try
        {
            _pathResolver.EnsureSharedPreviewDirectory(view3dHash);
            // Same-directory temp → atomic rename on every filesystem.
            tempPath = pooledAbsPath + ".tmp-" + Guid.NewGuid().ToString("N");
            var ok = await _glbWriter.WriteAsync(preview, tempPath, ct).ConfigureAwait(false);
            if (!ok)
            {
                return false;
            }

            if (File.Exists(pooledAbsPath))
            {
                // A concurrent writer won the race before the rename —
                // identical content by construction (the name IS the
                // content hash).
                return true;
            }
            try
            {
                File.Move(tempPath, pooledAbsPath);
            }
            catch (IOException)
            {
                // Race-loser: the file appeared between the check and the
                // rename — same content, so this IS the win case.
                return File.Exists(pooledAbsPath);
            }
            return true;
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

    /// <summary>
    /// #157: writes/clears the terminal "no extractable geometry" marker on
    /// every variant of (<paramref name="catalogItemId"/>,
    /// <paramref name="versionLabel"/>) — the content is identical across
    /// variants, so the marker is too. <paramref name="state"/> = -1 marks
    /// terminal no-geometry (glb-v1 detection clears);
    /// <see langword="null"/> heals it (only rows currently at -1 are
    /// touched). Best-effort: a marker failure never breaks the pipeline.
    /// </summary>
    private async Task WriteGlbStateAsync(
        string catalogItemId, string versionLabel, int? state, CancellationToken ct)
    {
        try
        {
            using var connection = _database.CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = state is not null
                ? """
                    UPDATE catalog_versions SET glb_state = @state
                    WHERE catalog_item_id = @itemId AND version_label = @label
                    """
                : """
                    UPDATE catalog_versions SET glb_state = NULL
                    WHERE catalog_item_id = @itemId AND version_label = @label
                      AND glb_state = -1
                    """;
            cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
            cmd.Parameters.Add(new SqliteParameter("@label", versionLabel));
            if (state is not null)
                cmd.Parameters.Add(new SqliteParameter("@state", state.Value));
            var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            SmartConLogger.Debug(
                $"WriteGlbStateAsync: item={catalogItemId}, v={versionLabel}, state={(state?.ToString() ?? "NULL")}, rows={rows}");
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"WriteGlbStateAsync skipped: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes any previous Model3D asset whose description starts with the
    /// <see cref="FamilyGeometryGlbWriter.AutoExtractedAssetDescriptionPrefix"/>,
    /// for the same (catalogItemId, versionLabel). Also deletes the physical
    /// GLB file from managed storage so the version directory does not
    /// accumulate stale previews on OverwriteCurrent.
    /// </summary>
    private async Task DeletePreviousAutoExtractedAssetAsync(
        string catalogItemId,
        string versionLabel,
        CancellationToken ct)
    {
        try
        {
            using var connection = _database.CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var staleAssets = new List<(string Id, string? RelativePath)>();

            using (var selectCmd = connection.CreateCommand())
            {
                selectCmd.CommandText = """
                    SELECT id, relative_path FROM family_assets
                    WHERE catalog_item_id = @itemId
                      AND version_label = @label
                      AND asset_type = 'Model3D'
                      AND description LIKE @prefix
                    """;
                selectCmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
                selectCmd.Parameters.Add(new SqliteParameter("@label", versionLabel));
                selectCmd.Parameters.Add(new SqliteParameter("@prefix",
                    FamilyGeometryGlbWriter.AutoExtractedAssetDescriptionPrefix + "%"));

                using var reader = await selectCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var id = reader.GetString(0);
                    var relPath = reader.IsDBNull(1) ? null : reader.GetString(1);
                    staleAssets.Add((id, relPath));
                }
            }

            if (staleAssets.Count == 0) return;

            var dbRoot = _database.GetDatabaseRoot();
            foreach (var (id, relPath) in staleAssets)
            {
                try
                {
                    using var delCmd = connection.CreateCommand();
                    delCmd.CommandText = "DELETE FROM family_assets WHERE id = @id";
                    delCmd.Parameters.Add(new SqliteParameter("@id", id));
                    await delCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                    if (!string.IsNullOrEmpty(relPath))
                    {
                        if (LocalCatalog.StoragePathResolver.IsSharedPreviewPoolPath(relPath))
                        {
                            // #249 (Phase 5): pooled files are shared —
                            // delete only when the last reference is gone
                            // (refcount, Plan v3).
                            await LocalCatalog.SharedPreviewPoolCleanup
                                .DeletePoolFileIfOrphanedAsync(_database, relPath, ct)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            var absPath = Path.Combine(dbRoot, relPath);
                            if (File.Exists(absPath))
                            {
                                try { File.Delete(absPath); }
                                catch (Exception ioEx)
                                {
                                    SmartConLogger.Debug($"Stale GLB file delete failed for '{Path.GetFileName(absPath)}': {ioEx.Message}");
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    SmartConLogger.Debug($"Stale GLB asset cleanup failed for id={id}: {ex.Message}");
                }
            }

            SmartConLogger.Debug($"Deleted {staleAssets.Count} stale auto-extracted preview asset(s) for {(catalogItemId, versionLabel)}");
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"DeletePreviousAutoExtractedAssetAsync skipped: {ex.Message}");
        }
    }
}
