using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.FamilyManager.Services.Actualization;
using SmartCon.Tests.FamilyManager.Repository;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// Tests for <see cref="SectionHashesActualizationTask"/> (Issue #249,
/// Phase 4): detection of versions lacking section analytics and the
/// backfill write for all Revit variants of a group.
/// </summary>
public sealed class SectionHashesActualizationTaskTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly FamilyContentHasher _hasher = new();
    private readonly SectionHashesActualizationTask _sut;

    public SectionHashesActualizationTaskTests()
    {
        _fixture = new TempCatalogFixture();
        _sut = new SectionHashesActualizationTask(_fixture.GetDatabase(), _hasher);
    }

    public void Dispose() => _fixture.Dispose();

    private async Task<(string? Hashes, string? Strings)> ReadSectionsAsync(string versionId)
    {
        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT section_hashes, section_strings FROM catalog_versions WHERE id = @id";
        cmd.Parameters.Add(new SqliteParameter("@id", versionId));
        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException("version row must exist");
        return (
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    [Fact]
    public async Task Detection_CurrentHashNoSections_Pending()
    {
        await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "FamP", hashFormatVersion: FamilyContentHashFormat.CurrentVersion);

        Assert.Equal(1, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task Detection_LegacyHashFormat_NotPending()
    {
        // hash_format_version < current is the CRITICAL hash task's job
        // (Order 12 < 80) — this task only fills CURRENT rows.
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamL", hashFormatVersion: 11);
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamN");

        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task Detection_TerminalSentinel_NotPending()
    {
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamT", hashFormatVersion: -1);

        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }

    [Fact]
    public async Task Apply_Loadable_WritesJsonForAllVariants()
    {
        var (itemId, versionId, fileId, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "FamA", hashFormatVersion: FamilyContentHashFormat.CurrentVersion);
        var variant2 = await CatalogSeedHelper.SeedAdditionalVariantAsync(_fixture, itemId, "FamA", "v1", 2024);

        var snapshot = CatalogSeedHelper.CreateSnapshot();
        var ctx = new FamilyActualizationContext(
            new ActualizationGroup(itemId, "FamA", "v1", true,
                new[]
                {
                    new ActualizationVariant(versionId, fileId, 2025, "x", "FamA.rfa"),
                    new ActualizationVariant(variant2, "f2", 2024, "y", "FamA.rfa"),
                }),
            new ActualizationVariant(versionId, fileId, 2025, "x", "FamA.rfa"),
            "C:\\root\\x",
            snapshot,
            null);

        await _sut.ApplyAsync(ctx, CancellationToken.None);

        var expectedSections = _hasher.ComputeSectionsForLoadable(snapshot)!;
        foreach (var vid in new[] { versionId, variant2 })
        {
            var (hashesJson, stringsJson) = await ReadSectionsAsync(vid);
            var hashes = ContentSectionJsonSerializer.Deserialize(hashesJson);
            var strings = ContentSectionJsonSerializer.Deserialize(stringsJson);
            Assert.NotNull(hashes);
            Assert.NotNull(strings);
            Assert.Equal(expectedSections.Count, hashes!.Count);
            Assert.Equal(expectedSections.Count, strings!.Count);
            foreach (var section in expectedSections)
            {
                Assert.Equal(section.HashHex, hashes[section.Key]);
                Assert.Equal(section.CanonicalString, strings[section.Key]);
            }
        }

        // Detection clears after the write.
        Assert.Equal(0, await _sut.CountPendingAsync(2025));
    }
}
