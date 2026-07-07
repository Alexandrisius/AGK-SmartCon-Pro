using System.IO;
using SmartCon.Core.Services.FamilyManager;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// v2.0.0 regression tests for the legacy <c>files/_stage/</c> folder
/// cleanup. The folder was used by SmartCon &lt; v2.0.0 for transient
/// family staging; after the temp-removal sweep the staging path lives
/// directly in managed storage and the folder accumulates orphan files
/// if it exists.
/// </summary>
public sealed class LegacyStageFolderCleanerTests : IDisposable
{
    private readonly string _familyManagerRoot;

    public LegacyStageFolderCleanerTests()
    {
        _familyManagerRoot = Path.Combine(Path.GetTempPath(), $"fm-cleanup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_familyManagerRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_familyManagerRoot)) Directory.Delete(_familyManagerRoot, recursive: true); } catch { }
    }

    [Fact]
    public void Cleanup_NullOrEmptyRoot_IsNoOp()
    {
        LegacyStageFolderCleaner.Cleanup(null!);
        LegacyStageFolderCleaner.Cleanup(string.Empty);
        LegacyStageFolderCleaner.Cleanup("   ");
    }

    [Fact]
    public void Cleanup_NoCatalogFolders_IsNoOp()
    {
        // Empty FamilyManager dir — no catalog subdirs.

        LegacyStageFolderCleaner.Cleanup(_familyManagerRoot);

        Assert.True(Directory.Exists(_familyManagerRoot));
    }

    [Fact]
    public void Cleanup_NoStageFolder_IsNoOp()
    {
        var catalogDir = CreateCatalogFolder("catalog-1", withStage: false);

        LegacyStageFolderCleaner.Cleanup(_familyManagerRoot);

        Assert.True(Directory.Exists(catalogDir));
    }

    [Fact]
    public void Cleanup_RemovesStageFolderAndContents()
    {
        var catalogDir = CreateCatalogFolder("catalog-1", withStage: true);
        var stageDir = Path.Combine(catalogDir, "files", "_stage");
        var stageFile = Path.Combine(stageDir, "orphan.rfa");
        File.WriteAllBytes(stageFile, new byte[] { 0x00 });

        LegacyStageFolderCleaner.Cleanup(_familyManagerRoot);

        Assert.True(Directory.Exists(catalogDir), "Catalog dir is preserved");
        Assert.False(Directory.Exists(stageDir), "_stage folder is removed");
        Assert.True(Directory.Exists(Path.Combine(catalogDir, "files", "catalog-1", "v1")),
            "Catalog-managed subdirs are preserved");
    }

    [Fact]
    public void Cleanup_RemovesStageInAllCatalogFolders()
    {
        CreateCatalogFolder("catalog-1", withStage: true);
        CreateCatalogFolder("catalog-2", withStage: true);
        CreateCatalogFolder("catalog-3", withStage: false);

        LegacyStageFolderCleaner.Cleanup(_familyManagerRoot);

        Assert.False(Directory.Exists(Path.Combine(_familyManagerRoot, "catalog-1", "files", "_stage")));
        Assert.False(Directory.Exists(Path.Combine(_familyManagerRoot, "catalog-2", "files", "_stage")));
        Assert.True(Directory.Exists(Path.Combine(_familyManagerRoot, "catalog-3")));
    }

    [Fact]
    public void Cleanup_IsIdempotent()
    {
        CreateCatalogFolder("catalog-1", withStage: true);

        LegacyStageFolderCleaner.Cleanup(_familyManagerRoot);
        LegacyStageFolderCleaner.Cleanup(_familyManagerRoot);

        Assert.False(Directory.Exists(Path.Combine(_familyManagerRoot, "catalog-1", "files", "_stage")));
    }

    [Fact]
    public void Cleanup_PreservesManagedVersions()
    {
        var catalogDir = CreateCatalogFolder("catalog-keep", withStage: true);
        var versionDir = Path.Combine(catalogDir, "files", "catalog-keep", "v1");
        var managedFile = Path.Combine(versionDir, "Family.rfa");
        File.WriteAllBytes(managedFile, new byte[] { 0x01, 0x02 });

        LegacyStageFolderCleaner.Cleanup(_familyManagerRoot);

        Assert.True(File.Exists(managedFile), "Managed .rfa in v1 must survive the cleanup");
    }

    private string CreateCatalogFolder(string name, bool withStage)
    {
        var catalogDir = Path.Combine(_familyManagerRoot, name);
        Directory.CreateDirectory(catalogDir);
        if (withStage)
        {
            var stageDir = Path.Combine(catalogDir, "files", "_stage");
            Directory.CreateDirectory(stageDir);
            File.WriteAllBytes(Path.Combine(stageDir, "leftover.rfa"), new byte[] { 0xFF });
        }
        Directory.CreateDirectory(Path.Combine(catalogDir, "files", name, "v1"));
        return catalogDir;
    }
}
