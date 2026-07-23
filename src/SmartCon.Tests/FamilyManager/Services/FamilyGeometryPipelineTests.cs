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
    }
}
