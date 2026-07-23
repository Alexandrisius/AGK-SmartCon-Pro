using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.Actualization;
using SmartCon.Tests.FamilyManager.Repository;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// Tests for <see cref="GlbPreviewActualizationTask"/> (ADR-054): detection
/// of the missing auto-extracted Model3D asset on ACTIVE labels and the
/// pipeline invocation with the same-session geometry.
/// </summary>
public sealed class GlbPreviewActualizationTaskTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly FakeGeometryPipeline _pipeline = new();
    private readonly GlbPreviewActualizationTask _sut;

    public GlbPreviewActualizationTaskTests()
    {
        _fixture = new TempCatalogFixture();
        _sut = new GlbPreviewActualizationTask(_fixture.GetDatabase(), _pipeline);
    }

    public void Dispose() => _fixture.Dispose();

    private sealed record PipelineCall(
        IReadOnlyList<FamilyGeometryPerType>? Geometry,
        string? ManagedPath,
        string ItemId,
        string VersionId,
        string VersionLabel,
        string FamilyName);

    private sealed class FakeGeometryPipeline : IFamilyGeometryPipeline
    {
        public List<PipelineCall> Calls { get; } = new();

        public Task RunAsync(
            IReadOnlyList<FamilyGeometryPerType>? geometryPerType,
            string? managedRfaPath,
            string catalogItemId,
            string versionId,
            string versionLabel,
            string familyName,
            CancellationToken ct = default)
        {
            Calls.Add(new PipelineCall(geometryPerType, managedRfaPath, catalogItemId, versionId, versionLabel, familyName));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task CountPending_NoAsset_Pending_WithAsset_NotPending()
    {
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamNoGlb");
        var (itemId, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamOk");
        await CatalogSeedHelper.SeedGlbAssetAsync(_fixture, itemId, "v1");

        Assert.Equal(1, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task CountNewerOnly_NoAsset2026Row_NewerOnlyNotProcessable()
    {
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamNew", revitVersion: 2026);

        var newer = await _sut.GetNewerOnlyPendingAsync(2025);
        Assert.Equal(1, newer.Count);
        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task LoadPendingKeys_ActiveLabelOnly()
    {
        // GLB missing on the ACTIVE label of FamA and on a NON-active label
        // of FamB — only FamA is pending (older labels are history).
        var (itemA, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA");
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamB", versionLabel: "v1", currentLabel: "v2");

        var keys = await _sut.LoadPendingGroupKeysAsync(2025);

        Assert.Equal(new[] { itemA + "|v1" }, keys.OrderBy(k => k).ToArray());
    }

    [Fact]
    public async Task CountPending_TerminalMarker_NotPending()
    {
        // #157: glb_state = -1 (family legitimately has no extractable 3D)
        // clears the detection even though no Model3D asset exists.
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamNo3D", glbState: -1);

        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task LoadPendingKeys_ExcludesTerminalMarker()
    {
        var (markedItem, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "FamNo3D", glbState: -1);
        var (pendingItem, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamPending");

        var keys = await _sut.LoadPendingGroupKeysAsync(2025);

        Assert.Contains(pendingItem + "|v1", keys);
        Assert.DoesNotContain(markedItem + "|v1", keys);
    }

    [Fact]
    public async Task Apply_RunsPipeline_WithContextGeometryAndOpenedVariant()
    {
        var geometry = Array.Empty<FamilyGeometryPerType>();
        var ctx = new FamilyActualizationContext(
            new ActualizationGroup("item1", "FamA", "v1", true,
                new[] { new ActualizationVariant("ver1", "f1", 2025, "files/item1/v1/FamA.rfa", "FamA.rfa") }),
            new ActualizationVariant("ver1", "f1", 2025, "files/item1/v1/FamA.rfa", "FamA.rfa"),
            "C:\\root\\files/item1/v1/FamA.rfa",
            CatalogSeedHelper.CreateSnapshot(),
            geometry);

        await _sut.ApplyAsync(ctx, CancellationToken.None);

        var call = Assert.Single(_pipeline.Calls);
        Assert.Same(geometry, call.Geometry);
        Assert.Equal("C:\\root\\files/item1/v1/FamA.rfa", call.ManagedPath);
        Assert.Equal("item1", call.ItemId);
        Assert.Equal("ver1", call.VersionId);
        Assert.Equal("v1", call.VersionLabel);
        Assert.Equal("FamA", call.FamilyName);
    }
}
