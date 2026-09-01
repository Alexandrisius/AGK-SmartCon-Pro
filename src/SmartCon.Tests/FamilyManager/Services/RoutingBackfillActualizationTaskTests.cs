using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.Actualization;
using SmartCon.FamilyManager.Services.LocalCatalog;
using SmartCon.Tests.FamilyManager.Repository;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// Tests for <see cref="RoutingBackfillActualizationTask"/> (ADR-072 World B,
/// audit C1/M14): the item-level routing tables (V37) may be seeded ONLY from
/// the CURRENT version's group — groups are processed in alphabetical label
/// order, so an archived label would otherwise seed first and the current
/// version's slimming would destroy the live routing unsaved. Archived groups
/// still slim. An already-slim current mini materializes the "routing as
/// data: empty" marker settings so sync never falls back to the slim mini.
/// </summary>
public sealed class RoutingBackfillActualizationTaskTests : IDisposable
{
    private const string ItemName = "Труба стальная";

    private readonly TempCatalogFixture _fixture;
    private readonly FakeSlimmingService _slimming = new();
    private readonly LocalFamilyRoutingRuleRepository _repository;
    private readonly RoutingBackfillActualizationTask _sut;

    public RoutingBackfillActualizationTaskTests()
    {
        _fixture = new TempCatalogFixture();
        _repository = new LocalFamilyRoutingRuleRepository(_fixture.GetDatabase());
        _sut = new RoutingBackfillActualizationTask(_fixture.GetDatabase(), _slimming, _repository, new SmartCon.FamilyManager.Services.LocalCatalog.LocalSegmentRuleRepository(_fixture.GetDatabase()));
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task CountPending_SystemUnbackfilled_IsPending_LoadableIsNot()
    {
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "Sys", familySource: "system");
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "Load");

        Assert.Equal(1, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task Apply_ArchivedLabelGroup_DoesNotSeed_ButStillSlims()
    {
        var (itemId, versionId, _, relativePath) = await SeedSystemItemAsync(versionLabel: "v1", currentLabel: "v2");
        _slimming.NextOutcome = SlimmedWithRouting("TypeA", "ArchivedPart: Type");

        await _sut.ApplyAsync(ContextFor(itemId, "v1", isActive: false, versionId, relativePath), CancellationToken.None);

        // C1: an archived label must NEVER seed the item-level truth...
        var (rules, settings) = await _repository.ReadForItemAsync(itemId);
        Assert.Empty(rules);
        Assert.Empty(settings);
        // ...but slimming still ran and the variant is marked done.
        Assert.Equal(1, _slimming.CallCount);
        Assert.Equal(1, await ReadBackfillMarkerAsync(versionId));
    }

    [Fact]
    public async Task Apply_ActiveLabelGroup_SeedsFromPreSlimSnapshot()
    {
        var (itemId, versionId, _, relativePath) = await SeedSystemItemAsync(versionLabel: "v2", currentLabel: "v2");
        _slimming.NextOutcome = SlimmedWithRouting("TypeA", "CurrentPart: Type");

        await _sut.ApplyAsync(ContextFor(itemId, "v2", isActive: true, versionId, relativePath), CancellationToken.None);

        var (rules, settings) = await _repository.ReadForItemAsync(itemId);
        var rule = Assert.Single(rules);
        Assert.Equal("CurrentPart: Type", rule.PartName);
        Assert.Equal("TypeA", rule.TypeName);
        var setting = Assert.Single(settings);
        Assert.Equal("TypeA", setting.TypeName);
        Assert.Equal(1, await ReadBackfillMarkerAsync(versionId));
    }

    [Fact]
    public async Task Apply_ArchivedProcessedFirstThenActive_ItemKeepsActiveRouting()
    {
        // Engine order regression (C1): labels sort alphabetically, so the
        // archived "v1" group is applied BEFORE the current "v2" group. The
        // item tables must end up with the CURRENT version's routing.
        var (itemId, v1, _, rel1) = await SeedSystemItemAsync(versionLabel: "v1", currentLabel: "v2");
        var v2 = await CatalogSeedHelper.SeedAdditionalVariantAsync(_fixture, itemId, ItemName, "v2", 2025);
        var rel2 = $"files/{itemId}/v2/{ItemName}.rfa";

        _slimming.NextOutcome = SlimmedWithRouting("TypeA", "ArchivedPart: Type");
        await _sut.ApplyAsync(ContextFor(itemId, "v1", isActive: false, v1, rel1), CancellationToken.None);

        _slimming.NextOutcome = SlimmedWithRouting("TypeA", "CurrentPart: Type");
        await _sut.ApplyAsync(ContextFor(itemId, "v2", isActive: true, v2, rel2), CancellationToken.None);

        var (rules, _) = await _repository.ReadForItemAsync(itemId);
        var rule = Assert.Single(rules);
        Assert.Equal("CurrentPart: Type", rule.PartName);
        Assert.Equal(1, await ReadBackfillMarkerAsync(v1));
        Assert.Equal(1, await ReadBackfillMarkerAsync(v2));
    }

    [Fact]
    public async Task Apply_AlreadySlimActiveGroup_WritesEmptyRoutingMarkers()
    {
        // M14: an already-slim CURRENT mini carries no routing source — the
        // item is legitimately routing-less. Marker settings (no rules) make
        // the empty state explicit so sync stops reading the slim mini.
        var (itemId, versionId, _, relativePath) = await SeedSystemItemAsync(versionLabel: "v2", currentLabel: "v2");
        await SeedFamilyTypeRowAsync(itemId, "TypeA");
        _slimming.NextOutcome = new MiniProjectSlimmingOutcome(
            MiniProjectSlimmingStatus.AlreadySlim, null, 0, 0, 0, 0, 0);

        await _sut.ApplyAsync(ContextFor(itemId, "v2", isActive: true, versionId, relativePath), CancellationToken.None);

        var (rules, settings) = await _repository.ReadForItemAsync(itemId);
        Assert.Empty(rules);
        var setting = Assert.Single(settings);
        Assert.Equal("TypeA", setting.TypeName);
        Assert.True(await _repository.HasAnyForItemAsync(itemId));
    }

    [Fact]
    public async Task Apply_AlreadySlimArchivedGroup_WritesNothing()
    {
        var (itemId, versionId, _, relativePath) = await SeedSystemItemAsync(versionLabel: "v1", currentLabel: "v2");
        await SeedFamilyTypeRowAsync(itemId, "TypeA");
        _slimming.NextOutcome = new MiniProjectSlimmingOutcome(
            MiniProjectSlimmingStatus.AlreadySlim, null, 0, 0, 0, 0, 0);

        await _sut.ApplyAsync(ContextFor(itemId, "v1", isActive: false, versionId, relativePath), CancellationToken.None);

        Assert.False(await _repository.HasAnyForItemAsync(itemId));
        Assert.Equal(1, await ReadBackfillMarkerAsync(versionId));
    }

    [Fact]
    public async Task Apply_AlreadySlimActiveGroup_FlexItem_NoMarkersWritten()
    {
        // Validator MAJOR-1: a flex item's raw preferred-junction value
        // cannot be assumed (inverted param convention) — writing marker
        // settings with a hardcoded 0 would create an eternal phantom
        // RoutingDrift. Flex items are consciously left WITHOUT markers;
        // the no-opinion guard protects their live routing.
        var (itemId, versionId, _, relativePath) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, ItemName, versionLabel: "v2", familySource: "system", currentLabel: "v2",
            revitCategoryId: RoutingGroupCatalog.FlexDuctCurvesCategoryId);
        await SeedFamilyTypeRowAsync(itemId, "TypeA");
        _slimming.NextOutcome = new MiniProjectSlimmingOutcome(
            MiniProjectSlimmingStatus.AlreadySlim, null, 0, 0, 0, 0, 0);

        await _sut.ApplyAsync(ContextFor(itemId, "v2", isActive: true, versionId, relativePath), CancellationToken.None);

        Assert.False(await _repository.HasAnyForItemAsync(itemId));
        Assert.Equal(1, await ReadBackfillMarkerAsync(versionId));
    }

    private async Task<(string ItemId, string VersionId, string FileId, string RelativePath)> SeedSystemItemAsync(
        string versionLabel, string currentLabel)
        => await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, ItemName, versionLabel: versionLabel, familySource: "system", currentLabel: currentLabel);

    private async Task SeedFamilyTypeRowAsync(string itemId, string typeName)
    {
        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO family_types (id, catalog_item_id, type_name, family_name, family_key)
            VALUES (@id, @item, @type, 'Pipes', 'Single')
            """;
        cmd.Parameters.Add(new SqliteParameter("@id", Guid.NewGuid().ToString()));
        cmd.Parameters.Add(new SqliteParameter("@item", itemId));
        cmd.Parameters.Add(new SqliteParameter("@type", typeName));
        await cmd.ExecuteNonQueryAsync();
    }

    private static MiniProjectSlimmingOutcome SlimmedWithRouting(string typeName, string partName)
        => new(
            MiniProjectSlimmingStatus.Slimmed,
            new SystemFamilySnapshot("Трубы", -2008044,
            [
                new SystemTypeSnapshot(typeName, [],
                    Routing: new RoutingPreferencesSnapshot(0,
                    [
                        new RoutingRuleSnapshot(1, partName, "отвод", []),
                    ]),
                    FamilyKey: "Single"),
            ]),
            1, 1, 0, 0, 1);

    private static FamilyActualizationContext ContextFor(
        string itemId, string label, bool isActive, string versionId, string relativePath)
    {
        var variant = new ActualizationVariant(versionId, "f1", 2025, relativePath, ItemName + ".rvt");
        return new FamilyActualizationContext(
            new ActualizationGroup(itemId, ItemName, label, isActive, [variant]),
            variant,
            "C:\\fake\\path.rvt",
            CatalogSeedHelper.CreateSnapshot(),
            Geometry: null);
    }

    private async Task<int?> ReadBackfillMarkerAsync(string versionId)
    {
        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT routing_backfilled FROM catalog_versions WHERE id = @id";
        cmd.Parameters.Add(new SqliteParameter("@id", versionId));
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? null : (int?)(long)value;
    }

    private sealed class FakeSlimmingService : IMiniProjectRoutingSlimmingService
    {
        public MiniProjectSlimmingOutcome NextOutcome { get; set; } =
            new(MiniProjectSlimmingStatus.AlreadySlim, null, 0, 0, 0, 0, 0);
        public int CallCount { get; private set; }

        public Task<MiniProjectSlimmingOutcome> SlimManagedFileAsync(string absolutePath, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(NextOutcome);
        }
    }
}
