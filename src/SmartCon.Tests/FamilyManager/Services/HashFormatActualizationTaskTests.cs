using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.FamilyManager.Services.Actualization;
using SmartCon.Tests.FamilyManager.Repository;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// Tests for <see cref="HashFormatActualizationTask"/> (ADR-054, Issue #126):
/// stale-hash detection, system re-flag (file-free pass), hash apply with
/// item sync, and the terminal markers -1/-2.
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

    [Fact]
    public async Task CountPending_CountsLoadableLegacyAndSystem_SkipsTerminalAndNewer()
    {
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA");
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "SysCat", familySource: "system", createFileOnDisk: false);
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "Migrated", hashFormatVersion: 2);
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "Skipped", hashFormatVersion: -1);
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "TooNew", revitVersion: 2026);

        var pending = await _sut.CountPendingAsync(2025);

        Assert.Equal(2, pending);   // FamA + SysCat
    }

    [Fact]
    public async Task LoadPendingKeys_IncludesStaleGroupsOfAnyRevit_LoadableOnly()
    {
        var (itemA, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA");
        var (itemNew, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "TooNew", revitVersion: 2026);
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "SysCat", familySource: "system", createFileOnDisk: false);
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "Migrated", hashFormatVersion: 2);

        var keys = await _sut.LoadPendingGroupKeysAsync(2025);

        Assert.Equal(2, keys.Count);
        Assert.Contains(itemA + "|v1", keys);
        Assert.Contains(itemNew + "|v1", keys);   // newer groups included — engine classifies
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
    public async Task FileFreePass_ReflagsSystemRows_VersionsAndItems()
    {
        var (itemId, versionId, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "Трубы", familySource: "system", createFileOnDisk: false);
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA");

        var reflagged = await _sut.RunFileFreePassAsync(2025);

        Assert.Equal(1, reflagged);
        var (fmt, _, _, _) = await CatalogSeedHelper.ReadVersionAsync(_fixture, versionId);
        Assert.Equal(2, fmt);
        var item = await _fixture.GetProvider().GetItemAsync(itemId);
        Assert.Equal(2, item?.HashFormatVersion);

        // Loadable rows untouched by the file-free pass.
        Assert.Equal(1, await _sut.CountPendingAsync(2025));
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
            Assert.Equal(2, fmt);
            Assert.Equal(expectedHash, hash);
        }
        var item = await _fixture.GetProvider().GetItemAsync(itemId);
        Assert.Equal(expectedHash, item?.ContentHash);
        Assert.Equal(2, item?.HashFormatVersion);
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
        Assert.Equal(2, fmt);
        Assert.NotNull(hash);
        var item = await _fixture.GetProvider().GetItemAsync(itemId);
        Assert.Null(item?.ContentHash);
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
