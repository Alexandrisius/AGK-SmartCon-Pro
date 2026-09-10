using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.Actualization;
using SmartCon.Tests.FamilyManager.Repository;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// Tests for <see cref="MiniProjectMarkerActualizationTask"/> (Issue #189):
/// SQL detection on the V28 <c>es_marker_version</c> column (system-only
/// scope, pending = 0, terminal markers excluded), apply writing per-variant
/// markers (Marked/AlreadyMarked → 1, Missing → -2, Failed → -1) and
/// clearing detection, and the group-failure terminal markers.
/// </summary>
public sealed class MiniProjectMarkerActualizationTaskTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly FakeMarkerService _markerService = new();
    private readonly MiniProjectMarkerActualizationTask _sut;

    public MiniProjectMarkerActualizationTaskTests()
    {
        _fixture = new TempCatalogFixture();
        _sut = new MiniProjectMarkerActualizationTask(_fixture.GetDatabase(), _markerService);
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task CountPending_SystemRowWithoutMarker_IsPending()
    {
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "Провода", familySource: "system", createFileOnDisk: false);

        Assert.Equal(1, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task CountPending_LoadableRow_IsNotPending()
    {
        // Detection is system-scoped: loadable versions with es_marker_version=0
        // must never pend this task.
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA");

        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task CountPending_MarkedRow_IsNotPending()
    {
        var (itemId, versionId, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "Провода", familySource: "system", createFileOnDisk: false);
        await SetMarkerAsync(versionId, 1);

        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task CountPending_TerminalMarkers_AreNotPending()
    {
        var (_, v1, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "A", familySource: "system", createFileOnDisk: false);
        var (itemId2, v2, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "B", familySource: "system", createFileOnDisk: false);
        await SetMarkerAsync(v1, -1);
        await SetMarkerAsync(v2, -2);

        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task Apply_Marked_Writes1_AndClearsDetection()
    {
        var (itemId, versionId, _, relativePath) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "Провода", familySource: "system", createFileOnDisk: false);
        _markerService.NextOutcome = new MiniProjectMarkFileOutcome(MiniProjectMarkFileStatus.Marked, 1);

        await _sut.ApplyAsync(ContextFor(itemId, "Провода", relativePath, versionId), CancellationToken.None);

        Assert.Equal(1, await ReadMarkerAsync(versionId));
        Assert.Equal(0, await _sut.CountPendingAsync(2025));
        Assert.Equal(itemId, _markerService.LastCatalogItemId);
    }

    [Fact]
    public async Task Apply_AlreadyMarked_Writes1_WithoutRewrite()
    {
        // A legacy file that already carries the ES marker (re-staged before
        // the column existed) must not be rewritten — only registered.
        var (itemId, versionId, _, relativePath) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "Провода", familySource: "system", createFileOnDisk: false);
        _markerService.NextOutcome = new MiniProjectMarkFileOutcome(MiniProjectMarkFileStatus.AlreadyMarked, 0);

        await _sut.ApplyAsync(ContextFor(itemId, "Провода", relativePath, versionId), CancellationToken.None);

        Assert.Equal(1, await ReadMarkerAsync(versionId));
        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task Apply_Failed_WritesTerminalMinus1_NotRetried()
    {
        var (itemId, versionId, _, relativePath) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "Провода", familySource: "system", createFileOnDisk: false);
        _markerService.NextOutcome = new MiniProjectMarkFileOutcome(MiniProjectMarkFileStatus.Failed, 0, "locked");

        await _sut.ApplyAsync(ContextFor(itemId, "Провода", relativePath, versionId), CancellationToken.None);

        Assert.Equal(-1, await ReadMarkerAsync(versionId));
        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task Apply_Missing_WritesTerminalMinus2()
    {
        var (itemId, versionId, _, relativePath) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "Провода", familySource: "system", createFileOnDisk: false);
        _markerService.NextOutcome = new MiniProjectMarkFileOutcome(MiniProjectMarkFileStatus.Missing, 0);

        await _sut.ApplyAsync(ContextFor(itemId, "Провода", relativePath, versionId), CancellationToken.None);

        Assert.Equal(-2, await ReadMarkerAsync(versionId));
        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task HandleGroupFailure_MissingFile_WritesMinus2_OnAllVariants()
    {
        var (itemId, v2025, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "Провода", familySource: "system", revitVersion: 2025);
        var v2021 = await CatalogSeedHelper.SeedAdditionalVariantAsync(_fixture, itemId, "Провода", "v1", 2021);
        var group = new ActualizationGroup(itemId, "Провода", "v1", true,
        [
            new ActualizationVariant(v2025, "f1", 2025, "p", "Провода.rvt"),
            new ActualizationVariant(v2021, "f2", 2021, "p", "Провода.rvt"),
        ]);

        await _sut.HandleGroupFailureAsync(group, ActualizationFailureKind.MissingFile, CancellationToken.None);

        Assert.Equal(-2, await ReadMarkerAsync(v2025));
        Assert.Equal(-2, await ReadMarkerAsync(v2021));
        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task Apply_MixedGroup_NewerVariantSkipped_StaysPendingForNewerHost()
    {
        // Review M1: a variant newer than the openable one must NOT be
        // terminally failed — it stays 0 and gets marked on a newer host.
        var (itemId, v2025, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "Провода", familySource: "system", revitVersion: 2025, createFileOnDisk: false);
        var v2026 = await CatalogSeedHelper.SeedAdditionalVariantAsync(_fixture, itemId, "Провода", "v1", 2026);
        _markerService.NextOutcome = new MiniProjectMarkFileOutcome(MiniProjectMarkFileStatus.Marked, 0);

        var opened = new ActualizationVariant(v2025, "f1", 2025, "p", "Провода.rvt");
        var newer = new ActualizationVariant(v2026, "f2", 2026, "p", "Провода.rvt");
        var context = new FamilyActualizationContext(
            new ActualizationGroup(itemId, "Провода", "v1", true, [opened, newer]),
            opened,
            "C:\\fake\\path.rvt",
            CatalogSeedHelper.CreateSnapshot(),
            Geometry: null);

        await _sut.ApplyAsync(context, CancellationToken.None);

        Assert.Equal(1, await ReadMarkerAsync(v2025));
        Assert.Equal(0, await ReadMarkerAsync(v2026));
        Assert.Equal(1, _markerService.CallCount);
    }

    private static FamilyActualizationContext ContextFor(
        string itemId, string itemName, string relativePath, string versionId)
    {
        var variant = new ActualizationVariant(versionId, "f1", 2025, relativePath, itemName + ".rvt");
        return new FamilyActualizationContext(
            new ActualizationGroup(itemId, itemName, "v1", true, [variant]),
            variant,
            "C:\\fake\\path.rvt",
            CatalogSeedHelper.CreateSnapshot(),
            Geometry: null);
    }

    private async Task SetMarkerAsync(string versionId, int marker)
    {
        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE catalog_versions SET es_marker_version = @m WHERE id = @id";
        cmd.Parameters.Add(new SqliteParameter("@m", marker));
        cmd.Parameters.Add(new SqliteParameter("@id", versionId));
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<int?> ReadMarkerAsync(string versionId)
    {
        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT es_marker_version FROM catalog_versions WHERE id = @id";
        cmd.Parameters.Add(new SqliteParameter("@id", versionId));
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? null : (int?)(long)value;
    }

    private sealed class FakeMarkerService : IMiniProjectActualizationService
    {
        public MiniProjectMarkFileOutcome NextOutcome { get; set; } =
            new(MiniProjectMarkFileStatus.Marked, 0);
        public string? LastCatalogItemId { get; private set; }
        public int CallCount { get; private set; }

        public Task<MiniProjectMarkFileOutcome> MarkManagedFileAsync(
            string absolutePath, string catalogItemId, CancellationToken ct = default)
        {
            LastCatalogItemId = catalogItemId;
            CallCount++;
            return Task.FromResult(NextOutcome);
        }
    }
}
