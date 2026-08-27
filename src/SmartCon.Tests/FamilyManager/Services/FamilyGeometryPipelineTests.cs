using System.IO;
using System.Numerics;
using Microsoft.Data.Sqlite;
using Moq;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.Geometry;
using SmartCon.Tests.FamilyManager.Repository;
using SmartCon.Tests.TestDoubles;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// Tests for <see cref="FamilyGeometryPipeline"/> terminal no-geometry
/// marker (#157, schema V23): when a family legitimately has no
/// extractable 3D, the pipeline writes <c>catalog_versions.glb_state = -1</c>
/// so the glb-v1 detection clears instead of pending forever; a later
/// import with real geometry heals the marker back to NULL. Transient
/// failures (GLB write error) write NO marker.
/// </summary>
public sealed class FamilyGeometryPipelineTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly Mock<IFamilyGeometryExtractor> _extractor = new();
    private readonly Mock<IGlbWriter> _glbWriter = new();
    private readonly Mock<IFamilyAssetService> _assetService = new();
    private readonly FamilyGeometryPipeline _sut;

    public FamilyGeometryPipelineTests()
    {
        _fixture = new TempCatalogFixture();
        _sut = new FamilyGeometryPipeline(
            _extractor.Object,
            _glbWriter.Object,
            _assetService.Object,
            new FakeFamilyManagerAwaitableEvent(),
            _fixture.GetDatabase());
    }

    public void Dispose() => _fixture.Dispose();

    private static MeshData OneTriangleMesh() => new(
        Positions: new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 },
        Normals: null,
        Indices: new[] { 0, 1, 2 },
        DiffuseColor: Vector4.One,
        NodeName: "Solid_1");

    private static FamilyAsset FakeAsset(string itemId) => new(
        Guid.NewGuid().ToString(), itemId, "v1", FamilyAssetType.Model3D,
        "preview.glb", "files/x/preview.glb", 100, "auto-extracted-preview:FamA::T1",
        DateTimeOffset.UtcNow, false);

    private async Task<int?> ReadGlbStateAsync(string itemId)
    {
        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT glb_state FROM catalog_versions WHERE catalog_item_id = @id";
        cmd.Parameters.Add(new SqliteParameter("@id", itemId));
        var result = await cmd.ExecuteScalarAsync();
        return result is long l ? (int)l : null;
    }

    [Fact]
    public async Task RunAsync_NoGeometryAnywhere_WritesTerminalMarker()
    {
        var (itemId, versionId, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA");
        _extractor
            .Setup(x => x.ExtractAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FamilyGeometryPerType>());

        await _sut.RunAsync(null, "files/x/FamA.rfa", itemId, versionId, "v1", "FamA");

        Assert.Equal(-1, await ReadGlbStateAsync(itemId));
        _assetService.Verify(
            x => x.AddAssetAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<FamilyAssetType>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunAsync_AllTypesEmpty_WritesTerminalMarker()
    {
        var (itemId, versionId, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA");
        IReadOnlyList<FamilyGeometryPerType> emptyTypes =
            [new FamilyGeometryPerType("T1", "FamA", [])];

        await _sut.RunAsync(emptyTypes, null, itemId, versionId, "v1", "FamA");

        Assert.Equal(-1, await ReadGlbStateAsync(itemId));
    }

    [Fact]
    public async Task RunAsync_GeometryWritten_HealsMarker()
    {
        var (itemId, versionId, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "FamA", glbState: -1);
        IReadOnlyList<FamilyGeometryPerType> geometry =
            [new FamilyGeometryPerType("T1", "FamA", [OneTriangleMesh()])];
        _glbWriter
            .Setup(x => x.WriteAsync(It.IsAny<FamilyGeometryPreview>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _assetService
            .Setup(x => x.AddAssetAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<FamilyAssetType>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, string? _, FamilyAssetType _, string _, string? _, CancellationToken _) => FakeAsset(id));

        await _sut.RunAsync(geometry, null, itemId, versionId, "v1", "FamA");

        Assert.Null(await ReadGlbStateAsync(itemId));
        _assetService.Verify(
            x => x.AddAssetAsync(itemId, "v1", FamilyAssetType.Model3D,
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_GlbWriteFails_WritesNoMarker_StaysPending()
    {
        var (itemId, versionId, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA");
        IReadOnlyList<FamilyGeometryPerType> geometry =
            [new FamilyGeometryPerType("T1", "FamA", [OneTriangleMesh()])];
        _glbWriter
            .Setup(x => x.WriteAsync(It.IsAny<FamilyGeometryPreview>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await _sut.RunAsync(geometry, null, itemId, versionId, "v1", "FamA");

        Assert.Null(await ReadGlbStateAsync(itemId));
        _assetService.Verify(
            x => x.AddAssetAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<FamilyAssetType>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── #249 (Phase 5): CAS preview pool ────────────────────────────────

    private static PreviewTypeSnapshot Preview(string typeName) => new(
        typeName,
        [new FormMetrics("Extrusion", true, 0.5, 6, 9, null, 2.5,
            new BoundingBoxSnapshot(0, 0, 0, 1, 1, 1),
            Centroid: new PointSnapshot(0.5, 0.5, 0.5),
            FaceTypes: [new FaceTypeCount("PlanarFace", 6)],
            TotalEdgeLength: 12.0,
            MaterialColor: new MaterialColorSnapshot(255, 128, 0, 255),
            Visibility: new FormVisibilitySnapshot(1, true))],
        []);

    private void SetupWriterCreatingFile()
    {
        _glbWriter
            .Setup(x => x.WriteAsync(It.IsAny<FamilyGeometryPreview>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FamilyGeometryPreview _, string path, CancellationToken _) =>
            {
                File.WriteAllText(path, "GLB-BYTES");
                return true;
            });
    }

    private void SetupPooledRegistration()
    {
        _assetService
            .Setup(x => x.RegisterPooledAssetAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<FamilyAssetType>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, string? label, FamilyAssetType _, string relPath, string? _, CancellationToken _) => new FamilyAsset(
                Guid.NewGuid().ToString(), id, label, FamilyAssetType.Model3D,
                Path.GetFileName(relPath), relPath, 9, "auto-extracted-preview:FamA::T1",
                DateTimeOffset.UtcNow, false));
    }

    [Fact]
    public async Task RunAsync_PerTypeCas_SecondVersionReusesPoolFile_NoSecondWrite()
    {
        var (itemId, versionId, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA");
        IReadOnlyList<FamilyGeometryPerType> geometry =
            [new FamilyGeometryPerType("T1", "FamA", [OneTriangleMesh()], Preview("T1"))];
        SetupWriterCreatingFile();
        SetupPooledRegistration();

        await _sut.RunAsync(geometry, null, itemId, versionId, "v1", "FamA");

        // First run: the pool file was written once and registered pooled.
        _glbWriter.Verify(
            x => x.WriteAsync(It.IsAny<FamilyGeometryPreview>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _assetService.Verify(
            x => x.RegisterPooledAssetAsync(itemId, "v1", FamilyAssetType.Model3D,
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // Second version, identical preview inputs → the SAME VIEW3D hash
        // → pool hit: no serialization, no second file, just a row.
        var version2 = await CatalogSeedHelper.SeedAdditionalVariantAsync(_fixture, itemId, "FamA", "v2", 2025);
        _glbWriter.Invocations.Clear();
        _assetService.Invocations.Clear();

        await _sut.RunAsync(geometry, null, itemId, version2, "v2", "FamA");

        _glbWriter.Verify(
            x => x.WriteAsync(It.IsAny<FamilyGeometryPreview>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _assetService.Verify(
            x => x.RegisterPooledAssetAsync(itemId, "v2", FamilyAssetType.Model3D,
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        // The legacy per-version writer path was never used.
        _assetService.Verify(
            x => x.AddAssetAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<FamilyAssetType>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunAsync_Level1Reuse_SectionsMatch_RelinksAssetsWithoutExtraction()
    {
        // v1 carries section analytics + an auto-preview row into an
        // EXISTING pool file; v2's sections match → the whole pipeline is
        // skipped and v2 simply references the pooled file.
        var (itemId, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "FamA", versionLabel: "v1", currentLabel: "v2");
        var version2 = await CatalogSeedHelper.SeedAdditionalVariantAsync(_fixture, itemId, "FamA", "v2", 2025);

        const string hash = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";
        var poolRel = SmartCon.FamilyManager.Services.LocalCatalog.StoragePathResolver.GetSharedPreviewRelativePath(hash);
        var poolAbs = Path.Combine(_fixture.GetDatabaseRoot(), poolRel);
        Directory.CreateDirectory(Path.GetDirectoryName(poolAbs)!);
        File.WriteAllText(poolAbs, "GLB-BYTES");

        var sectionsJson = "{\"DEF\":\"D1\",\"GEOM\":\"G1\",\"TYPES\":\"T1\",\"NESTED\":\"N1\",\"NONSHARED\":\"NS1\",\"NESTEDHASH\":\"NH1\"}";
        using (var conn = _fixture.GetDatabase().CreateConnection())
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE catalog_versions SET section_hashes = @h WHERE catalog_item_id = @id";
            cmd.Parameters.Add(new SqliteParameter("@h", sectionsJson));
            cmd.Parameters.Add(new SqliteParameter("@id", itemId));
            await cmd.ExecuteNonQueryAsync();

            using var assetCmd = conn.CreateCommand();
            assetCmd.CommandText = """
                INSERT INTO family_assets (id, catalog_item_id, version_label, asset_type, file_name, relative_path, size_bytes, description, created_at_utc)
                VALUES (@aid, @itemId, 'v1', 'Model3D', @fileName, @relPath, 9, 'auto-extracted-preview:FamA::T1', @t)
                """;
            assetCmd.Parameters.Add(new SqliteParameter("@aid", Guid.NewGuid().ToString()));
            assetCmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
            assetCmd.Parameters.Add(new SqliteParameter("@fileName", Path.GetFileName(poolRel)));
            assetCmd.Parameters.Add(new SqliteParameter("@relPath", poolRel));
            assetCmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
            await assetCmd.ExecuteNonQueryAsync();
        }

        await _sut.RunAsync(null, "files/x/FamA.rfa", itemId, version2, "v2", "FamA");

        // No extraction, no GLB write — the row was re-linked.
        _extractor.Verify(
            x => x.ExtractAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _glbWriter.Verify(
            x => x.WriteAsync(It.IsAny<FamilyGeometryPreview>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        using (var conn = _fixture.GetDatabase().CreateConnection())
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT relative_path FROM family_assets WHERE catalog_item_id = @id AND version_label = 'v2' AND asset_type = 'Model3D'";
            cmd.Parameters.Add(new SqliteParameter("@id", itemId));
            var rel = Convert.ToString(await cmd.ExecuteScalarAsync());
            Assert.Equal(poolRel, rel);
        }
    }

    [Fact]
    public async Task RunAsync_Level1Reuse_LegacyRowsAreNotRelinked_FullPipelineRuns()
    {
        // Validator HIGH-1: the previous version's rows point INSIDE its
        // own directory (pre-CAS layout) — re-linking them would break
        // the preview when that directory is deleted. Level-1 must
        // refuse and the full pipeline must run.
        var (itemId, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "FamA", versionLabel: "v1", currentLabel: "v2");
        var version2 = await CatalogSeedHelper.SeedAdditionalVariantAsync(_fixture, itemId, "FamA", "v2", 2025);

        var sectionsJson = "{\"DEF\":\"D1\",\"GEOM\":\"G1\",\"TYPES\":\"T1\",\"NESTED\":\"N1\",\"NONSHARED\":\"NS1\",\"NESTEDHASH\":\"NH1\"}";
        using (var conn = _fixture.GetDatabase().CreateConnection())
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE catalog_versions SET section_hashes = @h WHERE catalog_item_id = @id";
            cmd.Parameters.Add(new SqliteParameter("@h", sectionsJson));
            cmd.Parameters.Add(new SqliteParameter("@id", itemId));
            await cmd.ExecuteNonQueryAsync();

            // LEGACY auto-preview row: files/{itemId}/v1/models/preview.glb
            // (NOT a pool path).
            using var assetCmd = conn.CreateCommand();
            assetCmd.CommandText = """
                INSERT INTO family_assets (id, catalog_item_id, version_label, asset_type, file_name, relative_path, size_bytes, description, created_at_utc)
                VALUES (@aid, @itemId, 'v1', 'Model3D', 'preview.glb', @relPath, 9, 'auto-extracted-preview:FamA::T1', @t)
                """;
            assetCmd.Parameters.Add(new SqliteParameter("@aid", Guid.NewGuid().ToString()));
            assetCmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
            assetCmd.Parameters.Add(new SqliteParameter("@relPath", $"files/{itemId}/v1/models/preview.glb"));
            assetCmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
            await assetCmd.ExecuteNonQueryAsync();
        }

        IReadOnlyList<FamilyGeometryPerType> geometry =
            [new FamilyGeometryPerType("T1", "FamA", [OneTriangleMesh()], Preview("T1"))];
        _extractor
            .Setup(x => x.ExtractAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(geometry);
        SetupWriterCreatingFile();
        SetupPooledRegistration();

        await _sut.RunAsync(null, "files/x/FamA.rfa", itemId, version2, "v2", "FamA");

        // The legacy row was NOT re-linked: the extractor ran and a fresh
        // pooled asset was registered for v2.
        _extractor.Verify(
            x => x.ExtractAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _assetService.Verify(
            x => x.RegisterPooledAssetAsync(itemId, "v2", FamilyAssetType.Model3D,
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        using (var conn = _fixture.GetDatabase().CreateConnection())
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM family_assets WHERE catalog_item_id = @id AND version_label = 'v2' AND relative_path LIKE 'files/_shared/%'";
            cmd.Parameters.Add(new SqliteParameter("@id", itemId));
            Assert.Equal(0, Convert.ToInt32(await cmd.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task RunAsync_OverwriteWithMatchingPrevious_NoDuplicateAssetRows()
    {
        // Validator HIGH-2: OverwriteCurrent re-runs the pipeline for the
        // SAME label (old auto-preview rows exist). With level-1 reuse
        // the stale rows must be deleted BEFORE the re-link — never
        // duplicated.
        var (itemId, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "FamA", versionLabel: "v1", currentLabel: "v2");
        var version2 = await CatalogSeedHelper.SeedAdditionalVariantAsync(_fixture, itemId, "FamA", "v2", 2025);

        const string hash = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";
        var poolRel = SmartCon.FamilyManager.Services.LocalCatalog.StoragePathResolver.GetSharedPreviewRelativePath(hash);
        var poolAbs = Path.Combine(_fixture.GetDatabaseRoot(), poolRel);
        Directory.CreateDirectory(Path.GetDirectoryName(poolAbs)!);
        File.WriteAllText(poolAbs, "GLB-BYTES");

        var sectionsJson = "{\"DEF\":\"D1\",\"GEOM\":\"G1\",\"TYPES\":\"T1\",\"NESTED\":\"N1\",\"NONSHARED\":\"NS1\",\"NESTEDHASH\":\"NH1\"}";
        using (var conn = _fixture.GetDatabase().CreateConnection())
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE catalog_versions SET section_hashes = @h WHERE catalog_item_id = @id";
            cmd.Parameters.Add(new SqliteParameter("@h", sectionsJson));
            cmd.Parameters.Add(new SqliteParameter("@id", itemId));
            await cmd.ExecuteNonQueryAsync();

            // The OVERWRITTEN label's own stale row + v2's pooled row.
            foreach (var (label, relPath) in new[]
            {
                ("v1", $"files/{itemId}/v1/models/preview.glb"),
                ("v2", poolRel),
            })
            {
                using var assetCmd = conn.CreateCommand();
                assetCmd.CommandText = """
                    INSERT INTO family_assets (id, catalog_item_id, version_label, asset_type, file_name, relative_path, size_bytes, description, created_at_utc)
                    VALUES (@aid, @itemId, @label, 'Model3D', @fileName, @relPath, 9, 'auto-extracted-preview:FamA::T1', @t)
                    """;
                assetCmd.Parameters.Add(new SqliteParameter("@aid", Guid.NewGuid().ToString()));
                assetCmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
                assetCmd.Parameters.Add(new SqliteParameter("@label", label));
                assetCmd.Parameters.Add(new SqliteParameter("@fileName", Path.GetFileName(relPath)));
                assetCmd.Parameters.Add(new SqliteParameter("@relPath", relPath));
                assetCmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
                await assetCmd.ExecuteNonQueryAsync();
            }
        }

        // Overwrite of v1: sections still match v2 → level-1 re-link from
        // v2, but the stale v1 row must be gone.
        await _sut.RunAsync(null, "files/x/FamA.rfa", itemId, "ver1", "v1", "FamA");

        using (var conn = _fixture.GetDatabase().CreateConnection())
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT relative_path FROM family_assets
                WHERE catalog_item_id = @id AND version_label = 'v1' AND asset_type = 'Model3D'
                  AND description LIKE 'auto-extracted-preview:%'
                """;
            cmd.Parameters.Add(new SqliteParameter("@id", itemId));
            var rows = new List<string?>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
            var row = Assert.Single(rows);
            Assert.Equal(poolRel, row);
        }
    }

    [Fact]
    public async Task RunAsync_Level1Reuse_PreviousTerminalMarker_CarriedOver()
    {
        // The previous version was a terminal no-geometry family
        // (glb_state = -1); identical sections → the marker carries over
        // without any extraction.
        var (itemId, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "FamA", versionLabel: "v1", currentLabel: "v2", glbState: -1);
        var version2 = await CatalogSeedHelper.SeedAdditionalVariantAsync(_fixture, itemId, "FamA", "v2", 2025);

        var sectionsJson = "{\"DEF\":\"D1\",\"GEOM\":\"G1\",\"TYPES\":\"T1\",\"NESTED\":\"N1\",\"NONSHARED\":\"NS1\",\"NESTEDHASH\":\"NH1\"}";
        using (var conn = _fixture.GetDatabase().CreateConnection())
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE catalog_versions SET section_hashes = @h WHERE catalog_item_id = @id";
            cmd.Parameters.Add(new SqliteParameter("@h", sectionsJson));
            cmd.Parameters.Add(new SqliteParameter("@id", itemId));
            await cmd.ExecuteNonQueryAsync();
        }

        await _sut.RunAsync(null, "files/x/FamA.rfa", itemId, version2, "v2", "FamA");

        _extractor.Verify(
            x => x.ExtractAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        using (var conn = _fixture.GetDatabase().CreateConnection())
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT glb_state FROM catalog_versions WHERE catalog_item_id = @id AND version_label = 'v2'";
            cmd.Parameters.Add(new SqliteParameter("@id", itemId));
            Assert.Equal(-1, Convert.ToInt32(await cmd.ExecuteScalarAsync()));
        }
    }
}
