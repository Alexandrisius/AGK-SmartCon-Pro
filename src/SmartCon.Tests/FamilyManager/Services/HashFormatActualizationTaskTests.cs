using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.FamilyManager.Services.Actualization;
using SmartCon.Tests.FamilyManager.Repository;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// Tests for <see cref="HashFormatActualizationTask"/> (ADR-056/065, Issue
/// #159; FHV7 — #215): stale-hash detection (fmt NULL/not in {9,-1,-2} →
/// recompute to FHV9) across BOTH loadable and system sources, hash apply
/// with item sync, catalog-name trimming for staged system snapshots,
/// family_key healing from the staged snapshot, and the terminal markers
/// -1/-2. FHV3+ has no file-free pass — the system canonical string changed
/// structurally.
/// </summary>
public sealed class HashFormatActualizationTaskTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly FamilyContentHasher _hasher = new();
    private readonly HashFormatActualizationTask _sut;

    public HashFormatActualizationTaskTests()
    {
        _fixture = new TempCatalogFixture();
        _sut = new HashFormatActualizationTask(_fixture.GetDatabase(), _hasher);
    }

    public void Dispose() => _fixture.Dispose();

    private static FamilyActualizationContext ContextFor(
        string itemId, string itemName, string label, bool isActive, params ActualizationVariant[] variants)
    {
        return new FamilyActualizationContext(
            new ActualizationGroup(itemId, itemName, label, isActive, variants),
            variants[0],
            "C:\\fake\\path.rfa",
            CatalogSeedHelper.CreateSnapshot(),
            Geometry: null);
    }

    private static FamilyActualizationContext SystemContextFor(
        string itemId, string itemName, string label, bool isActive,
        SystemFamilySnapshot systemSnapshot, params ActualizationVariant[] variants)
    {
        return new FamilyActualizationContext(
            new ActualizationGroup(itemId, itemName, label, isActive, variants),
            variants[0],
            "C:\\fake\\path.rvt",
            CatalogSeedHelper.CreateSnapshot(),
            Geometry: null,
            SystemSnapshot: systemSnapshot);
    }

    private static SystemFamilySnapshot CreateSystemSnapshot(params string[] typeNames)
    {
        return new SystemFamilySnapshot(
            CategoryName: "Трубы",
            CategoryId: -2008044,
            Types: typeNames
                .Select(n => new SystemTypeSnapshot(n,
                    [new SystemParameterValue("Diameter", "Double", true, "25", 25.0, null)]))
                .ToList());
    }

    [Fact]
    public async Task CountPending_CountsLoadableLegacyAndSystem_SkipsTerminalCurrentAndNewer()
    {
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA");
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "SysCat", familySource: "system", createFileOnDisk: false);
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "StaleV2", hashFormatVersion: 2);   // v2 is stale for v9
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "Current", hashFormatVersion: 9);
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "Skipped", hashFormatVersion: -1);
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "TooNew", revitVersion: 2026);

        var pending = await _sut.CountPendingAsync(2025);

        Assert.Equal(3, pending);   // FamA + SysCat + StaleV2
    }

    [Fact]
    public async Task LoadPendingKeys_IncludesLoadableAndSystemGroupsOfAnyRevit()
    {
        var (itemA, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA");
        var (itemNew, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "TooNew", revitVersion: 2026);
        var (itemSys, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "SysCat", familySource: "system", createFileOnDisk: false);
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "Current", hashFormatVersion: 9);

        var keys = await _sut.LoadPendingGroupKeysAsync(2025);

        Assert.Equal(3, keys.Count);
        Assert.Contains(itemA + "|v1", keys);
        Assert.Contains(itemNew + "|v1", keys);   // newer groups included — engine classifies
        Assert.Contains(itemSys + "|v1", keys);   // system groups are file-processed in v3
    }

    [Fact]
    public async Task CountNewerOnly_OnlyGroupsWithoutOpenableVariant()
    {
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA");                        // openable
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "TooNew", revitVersion: 2026);  // newer-only
        // Mixed group: 2026 pending variant, but an openable 2023 variant of
        // the same label — NOT newer-only (the engine applies to all).
        var (itemM, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamMixed", revitVersion: 2026);
        await CatalogSeedHelper.SeedAdditionalVariantAsync(_fixture, itemM, "FamMixed", "v1", 2023);

        var newer = await _sut.GetNewerOnlyPendingAsync(2025);

        Assert.Equal(1, newer.Count);          // only TooNew
        Assert.Equal(2026, newer.RequiredRevitVersion);
        // Processable count sees FamA and FamMixed's openable variant.
        Assert.Equal(2, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task FileFreePass_IsNoop_SystemRowsStayPending()
    {
        // FHV3 has no cheap re-flag: the system canonical string changed
        // structurally (ordinal + STRUCT + ROUTING) — system rows need a
        // full recomputation from the staged .rvt (ADR-056).
        await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "Трубы", familySource: "system", createFileOnDisk: false);
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA");

        var processed = await _sut.RunFileFreePassAsync(2025);

        Assert.Equal(0, processed);
        Assert.Equal(2, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task Apply_ActiveLabel_WritesHashToAllVariants_AndSyncsItem()
    {
        var (itemId, v2025, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA", revitVersion: 2025);
        var v2021 = await CatalogSeedHelper.SeedAdditionalVariantAsync(_fixture, itemId, "FamA", "v1", 2021);

        var ctx = ContextFor(itemId, "FamA", "v1", isActive: true,
            new ActualizationVariant(v2025, "f1", 2025, "p", "FamA.rfa"),
            new ActualizationVariant(v2021, "f2", 2021, "p", "FamA.rfa"));
        await _sut.ApplyAsync(ctx, CancellationToken.None);

        var expectedHash = _hasher.ComputeForLoadable(CatalogSeedHelper.CreateSnapshot())!.HexString;
        foreach (var vid in new[] { v2025, v2021 })
        {
            var (fmt, hash, _, _) = await CatalogSeedHelper.ReadVersionAsync(_fixture, vid);
            Assert.Equal(9, fmt);
            Assert.Equal(expectedHash, hash);
        }
        var item = await _fixture.GetProvider().GetItemAsync(itemId);
        Assert.Equal(expectedHash, item?.ContentHash);
        Assert.Equal(9, item?.HashFormatVersion);
    }

    [Fact]
    public async Task Apply_NonActiveLabel_DoesNotTouchItem()
    {
        var (itemId, versionId, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "FamA", versionLabel: "v2", currentLabel: "v1");

        var ctx = ContextFor(itemId, "FamA", "v2", isActive: false,
            new ActualizationVariant(versionId, "f1", 2025, "p", "FamA.rfa"));
        await _sut.ApplyAsync(ctx, CancellationToken.None);

        var (fmt, hash, _, _) = await CatalogSeedHelper.ReadVersionAsync(_fixture, versionId);
        Assert.Equal(9, fmt);
        Assert.NotNull(hash);
        var item = await _fixture.GetProvider().GetItemAsync(itemId);
        Assert.Null(item?.ContentHash);
    }

    [Fact]
    public async Task Apply_SystemGroup_TrimsToCatalogTypeNames_AndWritesSystemHash()
    {
        // Staged snapshot carries a template-default type of the default
        // project ("Generic - 200mm") alongside the imported ones — the
        // catalog's family_types is the authoritative type list.
        var (itemId, versionId, fileId, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "Стены", familySource: "system", createFileOnDisk: false);
        await CatalogSeedHelper.SeedDataExtractedAsync(_fixture, itemId, versionId, fileId);   // seeds type 'DN50'

        var staged = CreateSystemSnapshot("DN50", "Generic - 200mm");
        var ctx = SystemContextFor(itemId, "Стены", "v1", isActive: true, staged,
            new ActualizationVariant(versionId, fileId, 2025, "p", "Стены.rvt"));
        await _sut.ApplyAsync(ctx, CancellationToken.None);

        var expectedHash = _hasher.ComputeForSystem(CreateSystemSnapshot("DN50"))!.HexString;
        var (fmt, hash, _, _) = await CatalogSeedHelper.ReadVersionAsync(_fixture, versionId);
        Assert.Equal(9, fmt);
        Assert.Equal(expectedHash, hash);
        var item = await _fixture.GetProvider().GetItemAsync(itemId);
        Assert.Equal(expectedHash, item?.ContentHash);
    }

    [Fact]
    public async Task Apply_SystemGroup_NoCatalogTypeNames_HashesFullStagedList()
    {
        var (itemId, versionId, fileId, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "Перекрытия", familySource: "system", createFileOnDisk: false);

        var staged = CreateSystemSnapshot("TypeA", "TypeB");
        var ctx = SystemContextFor(itemId, "Перекрытия", "v1", isActive: true, staged,
            new ActualizationVariant(versionId, fileId, 2025, "p", "Перекрытия.rvt"));
        await _sut.ApplyAsync(ctx, CancellationToken.None);

        var expectedHash = _hasher.ComputeForSystem(staged)!.HexString;
        var (fmt, hash, _, _) = await CatalogSeedHelper.ReadVersionAsync(_fixture, versionId);
        Assert.Equal(9, fmt);
        Assert.Equal(expectedHash, hash);
    }

    [Fact]
    public async Task Apply_SystemGroup_ClearsDetection()
    {
        var (itemId, versionId, fileId, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "Воздуховоды", familySource: "system", createFileOnDisk: false);

        var staged = CreateSystemSnapshot("TypeA");
        var ctx = SystemContextFor(itemId, "Воздуховоды", "v1", isActive: true, staged,
            new ActualizationVariant(versionId, fileId, 2025, "p", "Воздуховоды.rvt"));
        await _sut.ApplyAsync(ctx, CancellationToken.None);

        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task Apply_SystemGroup_HealsLegacySingleFamilyKey_AndTrimsDespiteKeyMismatch()
    {
        // FHV7 (#215): a duct row stored with the legacy "Single" key (a)
        // still matches the staged shape-keyed type for trimming (no
        // misleading "matches none" path) and (b) gets its family_key
        // healed from the staged snapshot; a row with empty family_name
        // (loadable/legacy) is never touched.
        var (itemId, versionId, fileId, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "Воздуховоды", familySource: "system", createFileOnDisk: false);
        await SeedTypeRowAsync(itemId, versionId, fileId, "Круглый", "Воздуховод круглого сечения", SystemFamilyKeys.SingleFamily);
        await SeedTypeRowAsync(itemId, versionId, fileId, "LegacyNoFamily", "", "");

        var stagedTypes = new[]
        {
            new SystemTypeSnapshot("Круглый",
                [new SystemParameterValue("Diameter", "Double", true, "25", 25.0, null)],
                FamilyKey: SystemFamilyKeys.DuctRound,
                FamilyName: "Воздуховод круглого сечения"),
            new SystemTypeSnapshot("LegacyNoFamily",
                [new SystemParameterValue("Diameter", "Double", true, "25", 25.0, null)],
                FamilyKey: SystemFamilyKeys.DuctRound,
                FamilyName: "Воздуховод круглого сечения"),
            new SystemTypeSnapshot("TemplateDefault",
                [new SystemParameterValue("Diameter", "Double", true, "25", 25.0, null)],
                FamilyKey: SystemFamilyKeys.DuctRectangular,
                FamilyName: "Воздуховод прямоугольного сечения"),
        };
        var staged = new SystemFamilySnapshot(
            CategoryName: "Воздуховоды", CategoryId: -2008000, Types: stagedTypes);
        var ctx = SystemContextFor(itemId, "Воздуховоды", "v1", isActive: true, staged,
            new ActualizationVariant(versionId, fileId, 2025, "p", "Воздуховоды.rvt"));
        await _sut.ApplyAsync(ctx, CancellationToken.None);

        // (a) Trim: "TemplateDefault" отсутствует в каталоге → хэш без него.
        var expectedHash = _hasher.ComputeForSystem(staged with
        {
            Types = stagedTypes.Where(t => t.Name != "TemplateDefault").ToList(),
        })!.HexString;
        var (_, hash, _, _) = await CatalogSeedHelper.ReadVersionAsync(_fixture, versionId);
        Assert.Equal(expectedHash, hash);

        // (b) Heal: "Single" → shape-ключ; пустая family_name — не тронута.
        Assert.Equal(SystemFamilyKeys.DuctRound, await ReadFamilyKeyAsync(itemId, "Круглый"));
        Assert.Equal(string.Empty, await ReadFamilyKeyAsync(itemId, "LegacyNoFamily"));
    }

    private async Task SeedTypeRowAsync(
        string itemId, string versionId, string fileId,
        string typeName, string familyName, string familyKey)
    {
        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO family_types (id, catalog_item_id, type_name, sort_order, version_id, file_id, family_name, family_key)
            VALUES (@id, @itemId, @name, 0, @versionId, @fileId, @family, @key)
            """;
        cmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@id", Guid.NewGuid().ToString()));
        cmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@itemId", itemId));
        cmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@name", typeName));
        cmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@versionId", versionId));
        cmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@fileId", fileId));
        cmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@family", familyName));
        cmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@key", familyKey));
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<string> ReadFamilyKeyAsync(string itemId, string typeName)
    {
        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT family_key FROM family_types WHERE catalog_item_id = @itemId AND type_name = @name LIMIT 1";
        cmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@itemId", itemId));
        cmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@name", typeName));
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Failure_MissingFile_MarksMinus2_Failed_MarksMinus1()
    {
        var (_, vMissing, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA", createFileOnDisk: false);
        var (_, vFailed, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamB");

        await _sut.HandleGroupFailureAsync(
            new ActualizationGroup("a", "FamA", "v1", true,
                new[] { new ActualizationVariant(vMissing, "f", 2025, "p", "FamA.rfa") }),
            ActualizationFailureKind.MissingFile, CancellationToken.None);
        await _sut.HandleGroupFailureAsync(
            new ActualizationGroup("b", "FamB", "v1", true,
                new[] { new ActualizationVariant(vFailed, "f", 2025, "p", "FamB.rfa") }),
            ActualizationFailureKind.ExtractionFailed, CancellationToken.None);

        Assert.Equal(-2, (await CatalogSeedHelper.ReadVersionAsync(_fixture, vMissing)).Fmt);
        Assert.Equal(-1, (await CatalogSeedHelper.ReadVersionAsync(_fixture, vFailed)).Fmt);

        // Terminal markers are excluded from the pending count forever.
        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }
}
