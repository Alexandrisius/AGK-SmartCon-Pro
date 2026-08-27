using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.FamilyManager.Services.LocalCatalog;
using SmartCon.Tests.FamilyManager.Repository;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

/// <summary>
/// Issue #249, Phase 5 (CAS refcount rules): pooled preview files are
/// deleted only when the last <c>family_assets</c> reference disappears;
/// the garbage sweep collects zero-reference pool files.
/// </summary>
public sealed class SharedPreviewPoolCleanupTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;

    public SharedPreviewPoolCleanupTests()
    {
        _fixture = new TempCatalogFixture();
    }

    public void Dispose() => _fixture.Dispose();

    private const string Hash = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";

    private string CreatePoolFile(out string relativePath)
    {
        relativePath = StoragePathResolver.GetSharedPreviewRelativePath(Hash);
        var absolute = Path.Combine(_fixture.GetDatabaseRoot(), relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, "GLB-BYTES");
        return absolute;
    }

    private async Task SeedAssetRowAsync(string itemId, string label, string relativePath)
    {
        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO family_assets (id, catalog_item_id, version_label, asset_type, file_name, relative_path, size_bytes, description, created_at_utc)
            VALUES (@id, @itemId, @label, 'Model3D', @fileName, @relPath, 8, 'auto-extracted-preview:Fam::T', @t)
            """;
        cmd.Parameters.Add(new SqliteParameter("@id", Guid.NewGuid().ToString()));
        cmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
        cmd.Parameters.Add(new SqliteParameter("@label", label));
        cmd.Parameters.Add(new SqliteParameter("@fileName", Path.GetFileName(relativePath)));
        cmd.Parameters.Add(new SqliteParameter("@relPath", relativePath));
        cmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task RemoveAssetRowAsync(string itemId, string label)
    {
        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM family_assets WHERE catalog_item_id = @itemId AND version_label = @label";
        cmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
        cmd.Parameters.Add(new SqliteParameter("@label", label));
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task DeleteIfOrphaned_ReferencesRemain_KeepsFile()
    {
        var (itemId, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA");
        var absolute = CreatePoolFile(out var relativePath);
        await SeedAssetRowAsync(itemId, "v1", relativePath);

        await SharedPreviewPoolCleanup.DeletePoolFileIfOrphanedAsync(
            _fixture.GetDatabase(), relativePath, CancellationToken.None);

        Assert.True(File.Exists(absolute));
    }

    [Fact]
    public async Task DeleteIfOrphaned_LastReferenceGone_DeletesFile()
    {
        var (itemId, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA");
        var absolute = CreatePoolFile(out var relativePath);
        await SeedAssetRowAsync(itemId, "v1", relativePath);
        await RemoveAssetRowAsync(itemId, "v1");

        await SharedPreviewPoolCleanup.DeletePoolFileIfOrphanedAsync(
            _fixture.GetDatabase(), relativePath, CancellationToken.None);

        Assert.False(File.Exists(absolute));
    }

    [Fact]
    public async Task Sweep_DeletesOnlyUnreferencedFiles()
    {
        var (itemId, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA");
        var referenced = CreatePoolFile(out var referencedRel);
        // A second, orphaned pool file (different hash).
        var orphanRel = StoragePathResolver.GetSharedPreviewRelativePath(
            "1111111111111111111111111111111111111111111111111111111111111111");
        var orphan = Path.Combine(_fixture.GetDatabaseRoot(), orphanRel);
        Directory.CreateDirectory(Path.GetDirectoryName(orphan)!);
        File.WriteAllText(orphan, "ORPHAN");
        await SeedAssetRowAsync(itemId, "v1", referencedRel);

        var deleted = await SharedPreviewPoolCleanup.SweepOrphanedPoolFilesAsync(
            _fixture.GetDatabase(), CancellationToken.None);

        Assert.Equal(1, deleted);
        Assert.True(File.Exists(referenced));
        Assert.False(File.Exists(orphan));
    }

    [Fact]
    public async Task DeleteVersionAsync_SharedPoolFile_SurvivesUntilLastVersionDeleted()
    {
        // Two NON-active versions referencing the SAME pooled file (the
        // level-1 re-link scenario). Deleting one must keep the file;
        // deleting the last must collect it. (The active version v3 can
        // never be deleted — the guard stays untouched.)
        var (itemId, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "FamA", versionLabel: "v1", currentLabel: "v3");
        await CatalogSeedHelper.SeedAdditionalVariantAsync(_fixture, itemId, "FamA", "v2", 2025);
        await CatalogSeedHelper.SeedAdditionalVariantAsync(_fixture, itemId, "FamA", "v3", 2025);
        var absolute = CreatePoolFile(out var relativePath);
        await SeedAssetRowAsync(itemId, "v1", relativePath);
        await SeedAssetRowAsync(itemId, "v2", relativePath);

        var provider = _fixture.GetProvider();
        var r1 = await provider.DeleteVersionAsync(itemId, "v1");
        Assert.True(r1.Success, r1.ErrorMessage);
        Assert.True(File.Exists(absolute), "pool file must survive while v2 references it");

        var r2 = await provider.DeleteVersionAsync(itemId, "v2");
        Assert.True(r2.Success, r2.ErrorMessage);
        Assert.False(File.Exists(absolute), "pool file must be deleted with the last reference");
    }
}
