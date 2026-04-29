using System.IO;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

public sealed class StorageModePipelineTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly LocalFamilyImportService _importService;
    private readonly LocalFamilyFileResolver _resolver;

    public StorageModePipelineTests()
    {
        _fixture = new TempCatalogFixture();
        _fixture.MigrateAsync().GetAwaiter().GetResult();

        var hasher = new Sha256FileHasher();
        var metadataService = new FileNameOnlyMetadataExtractionService(hasher);
        _importService = new LocalFamilyImportService(
            _fixture.GetDatabase(),
            _fixture.GetMigrator(),
            _fixture.GetProvider(),
            hasher,
            metadataService);
        _resolver = new LocalFamilyFileResolver(_fixture.GetDatabase());
    }

    public void Dispose() => _fixture.Dispose();

    private string CanonicalRoot()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "SmartCon", "FamilyManager");
    }

    [Fact]
    public async Task LinkedImport_Default_DoesNotCopyFile()
    {
        var path = _fixture.CreateFakeRfaFile("LinkedDefault.rfa");
        var request = new FamilyImportRequest(path, null, null, null);

        var result = await _importService.ImportFileAsync(request);

        Assert.True(result.Success);
        var file = await _fixture.GetProvider().GetFileAsync(result.FileId!);
        Assert.NotNull(file);
        Assert.Equal(FamilyFileStorageMode.Linked, file.StorageMode);
        Assert.Null(file.CachedPath);
        Assert.Equal(path, file.OriginalPath);
    }

    [Fact]
    public async Task LinkedImport_PreservesOriginalPath()
    {
        var path = _fixture.CreateFakeRfaFile("LinkedPath.rfa");
        var request = new FamilyImportRequest(path, "TestCategory", null, null);

        var result = await _importService.ImportFileAsync(request);

        Assert.True(result.Success);
        var file = await _fixture.GetProvider().GetFileAsync(result.FileId!);
        Assert.Equal(path, file!.OriginalPath);
        Assert.Equal("LinkedPath.rfa", file.FileName);
    }

    [Fact]
    public async Task CachedImport_CopiesFileToCache()
    {
        var path = _fixture.CreateFakeRfaFile("Cached.rfa");
        var request = new FamilyImportRequest(path, null, null, null, FamilyFileStorageMode.Cached);

        var result = await _importService.ImportFileAsync(request);

        Assert.True(result.Success);
        var file = await _fixture.GetProvider().GetFileAsync(result.FileId!);
        Assert.Equal(FamilyFileStorageMode.Cached, file!.StorageMode);
        Assert.NotNull(file.CachedPath);
        Assert.StartsWith("cache/rfa/", file.CachedPath);

        var absPath = Path.Combine(CanonicalRoot(), file.CachedPath);
        Assert.True(File.Exists(absPath));
    }

    [Fact]
    public async Task LinkedDedup_SameContent_SkipsDuplicate()
    {
        var content = "DEDUP_CONTENT_LINKED"u8.ToArray();
        var path1 = _fixture.CreateFakeRfaFileWithContent("Dup1.rfa", content);
        var path2 = _fixture.CreateFakeRfaFileWithContent("Dup2.rfa", content);

        var r1 = await _importService.ImportFileAsync(new FamilyImportRequest(path1, null, null, null));
        var r2 = await _importService.ImportFileAsync(new FamilyImportRequest(path2, null, null, null));

        Assert.True(r1.Success);
        Assert.False(r1.WasSkippedAsDuplicate);
        Assert.True(r2.WasSkippedAsDuplicate);
        Assert.Equal(r1.CatalogItemId, r2.DuplicateCatalogItemId);
    }

    [Fact]
    public async Task Resolver_Linked_ReturnsOriginalPath()
    {
        var path = _fixture.CreateFakeRfaFile("ResolveLinked.rfa");
        var importResult = await _importService.ImportFileAsync(new FamilyImportRequest(path, null, null, null));

        var resolved = await _resolver.ResolveForLoadAsync(importResult.VersionId!);

        Assert.Equal(path, resolved.AbsolutePath);
        Assert.Equal(importResult.CatalogItemId, resolved.CatalogItemId);
    }

    [Fact]
    public async Task Resolver_Cached_ReturnsCachedPath()
    {
        var path = _fixture.CreateFakeRfaFile("ResolveCached.rfa");
        var importResult = await _importService.ImportFileAsync(
            new FamilyImportRequest(path, null, null, null, FamilyFileStorageMode.Cached));

        var resolved = await _resolver.ResolveForLoadAsync(importResult.VersionId!);

        Assert.NotEmpty(resolved.AbsolutePath);
        Assert.True(File.Exists(resolved.AbsolutePath));
        Assert.StartsWith(CanonicalRoot(), resolved.AbsolutePath);
    }

    [Fact]
    public async Task Resolver_LinkedFileDeleted_ReturnsEmptyPath()
    {
        var path = _fixture.CreateFakeRfaFile("ResolveDeleted.rfa");
        var importResult = await _importService.ImportFileAsync(new FamilyImportRequest(path, null, null, null));

        File.Delete(path);

        var resolved = await _resolver.ResolveForLoadAsync(importResult.VersionId!);
        Assert.Equal(string.Empty, resolved.AbsolutePath);
    }

    [Fact]
    public async Task SwitchToCached_CopiesFileAndUpdatesDb()
    {
        var path = _fixture.CreateFakeRfaFile("SwitchCached.rfa");
        var importResult = await _importService.ImportFileAsync(new FamilyImportRequest(path, null, null, null));
        var provider = _fixture.GetProvider();

        var fileBefore = await provider.GetFileAsync(importResult.FileId!);
        Assert.Equal(FamilyFileStorageMode.Linked, fileBefore!.StorageMode);
        Assert.Null(fileBefore.CachedPath);

        var switchResult = await provider.SwitchStorageModeAsync(importResult.CatalogItemId!, FamilyFileStorageMode.Cached);
        Assert.True(switchResult);

        var fileAfter = await provider.GetFileAsync(importResult.FileId!);
        Assert.Equal(FamilyFileStorageMode.Cached, fileAfter!.StorageMode);
        Assert.NotNull(fileAfter.CachedPath);

        var absCache = Path.Combine(CanonicalRoot(), fileAfter.CachedPath);
        Assert.True(File.Exists(absCache));

        var resolved = await _resolver.ResolveForLoadAsync(importResult.VersionId!);
        Assert.NotEmpty(resolved.AbsolutePath);
    }

    [Fact]
    public async Task SwitchToLinked_DeletesCacheFileAndUpdatesDb()
    {
        var path = _fixture.CreateFakeRfaFile("SwitchLinked.rfa");
        var importResult = await _importService.ImportFileAsync(
            new FamilyImportRequest(path, null, null, null, FamilyFileStorageMode.Cached));
        var provider = _fixture.GetProvider();

        var fileBefore = await provider.GetFileAsync(importResult.FileId!);
        Assert.Equal(FamilyFileStorageMode.Cached, fileBefore!.StorageMode);
        var cachePathBefore = fileBefore.CachedPath!;
        var absCacheBefore = Path.Combine(CanonicalRoot(), cachePathBefore);
        Assert.True(File.Exists(absCacheBefore));

        var switchResult = await provider.SwitchStorageModeAsync(importResult.CatalogItemId!, FamilyFileStorageMode.Linked);
        Assert.True(switchResult);

        var fileAfter = await provider.GetFileAsync(importResult.FileId!);
        Assert.Equal(FamilyFileStorageMode.Linked, fileAfter!.StorageMode);
        Assert.Null(fileAfter.CachedPath);
        Assert.False(File.Exists(absCacheBefore));

        var resolved = await _resolver.ResolveForLoadAsync(importResult.VersionId!);
        Assert.Equal(path, resolved.AbsolutePath);
    }

    [Fact]
    public async Task SwitchToCached_OriginalMissing_ReturnsFalse()
    {
        var path = _fixture.CreateFakeRfaFile("MissingOriginal.rfa");
        var importResult = await _importService.ImportFileAsync(new FamilyImportRequest(path, null, null, null));
        File.Delete(path);

        var switchResult = await _fixture.GetProvider().SwitchStorageModeAsync(
            importResult.CatalogItemId!, FamilyFileStorageMode.Cached);
        Assert.False(switchResult);
    }

    [Fact]
    public async Task SwitchToLinked_WithOverridePath_UpdatesOriginalPath()
    {
        var path1 = _fixture.CreateFakeRfaFile("Original1.rfa");
        var path2 = _fixture.CreateFakeRfaFile("NewLocation.rfa");
        var importResult = await _importService.ImportFileAsync(
            new FamilyImportRequest(path1, null, null, null, FamilyFileStorageMode.Cached));
        var provider = _fixture.GetProvider();

        var switchResult = await provider.SwitchStorageModeAsync(
            importResult.CatalogItemId!, FamilyFileStorageMode.Linked, path2);
        Assert.True(switchResult);

        var file = await provider.GetFileAsync(importResult.FileId!);
        Assert.Equal(FamilyFileStorageMode.Linked, file!.StorageMode);
        Assert.Equal(path2, file.OriginalPath);
        Assert.Null(file.CachedPath);

        var resolved = await _resolver.ResolveForLoadAsync(importResult.VersionId!);
        Assert.Equal(path2, resolved.AbsolutePath);
    }

    [Fact]
    public async Task SwitchRoundTrip_LinkedToCachedAndBack()
    {
        var path = _fixture.CreateFakeRfaFile("RoundTrip.rfa");
        var importResult = await _importService.ImportFileAsync(new FamilyImportRequest(path, null, null, null));
        var provider = _fixture.GetProvider();

        var r1 = await provider.SwitchStorageModeAsync(importResult.CatalogItemId!, FamilyFileStorageMode.Cached);
        Assert.True(r1);
        var fileCached = await provider.GetFileAsync(importResult.FileId!);
        Assert.Equal(FamilyFileStorageMode.Cached, fileCached!.StorageMode);
        Assert.NotNull(fileCached.CachedPath);

        var r2 = await provider.SwitchStorageModeAsync(importResult.CatalogItemId!, FamilyFileStorageMode.Linked);
        Assert.True(r2);
        var fileLinked = await provider.GetFileAsync(importResult.FileId!);
        Assert.Equal(FamilyFileStorageMode.Linked, fileLinked!.StorageMode);
        Assert.Null(fileLinked.CachedPath);
        Assert.Equal(path, fileLinked.OriginalPath);

        var resolved = await _resolver.ResolveForLoadAsync(importResult.VersionId!);
        Assert.Equal(path, resolved.AbsolutePath);
    }

    [Fact]
    public async Task GetStorageMode_ReturnsCorrectMode()
    {
        var path = _fixture.CreateFakeRfaFile("GetMode.rfa");
        var importResult = await _importService.ImportFileAsync(new FamilyImportRequest(path, null, null, null));
        var provider = _fixture.GetProvider();

        var mode = await provider.GetStorageModeAsync(importResult.CatalogItemId!);
        Assert.Equal(FamilyFileStorageMode.Linked, mode);
    }

    [Fact]
    public async Task GetStorageMode_AfterSwitch_ReturnsNewMode()
    {
        var path = _fixture.CreateFakeRfaFile("GetModeSwitch.rfa");
        var importResult = await _importService.ImportFileAsync(new FamilyImportRequest(path, null, null, null));
        var provider = _fixture.GetProvider();

        await provider.SwitchStorageModeAsync(importResult.CatalogItemId!, FamilyFileStorageMode.Cached);
        var mode = await provider.GetStorageModeAsync(importResult.CatalogItemId!);
        Assert.Equal(FamilyFileStorageMode.Cached, mode);
    }

    [Fact]
    public async Task MetadataIntact_AfterSwitch()
    {
        var path = _fixture.CreateFakeRfaFile("MetaCheck.rfa");
        var tags = new List<string> { "HVAC", "Test" };
        var importResult = await _importService.ImportFileAsync(
            new FamilyImportRequest(path, "TestCategory", tags, "Test description"));
        var provider = _fixture.GetProvider();

        await provider.SwitchStorageModeAsync(importResult.CatalogItemId!, FamilyFileStorageMode.Cached);
        await provider.SwitchStorageModeAsync(importResult.CatalogItemId!, FamilyFileStorageMode.Linked);

        var item = await provider.GetItemAsync(importResult.CatalogItemId!);
        Assert.Equal("MetaCheck", item!.Name);
        Assert.Equal("TestCategory", item.CategoryName);
        Assert.Equal("Test description", item.Description);
        Assert.Equal(FamilyContentStatus.Draft, item.Status);
        Assert.Equal(2, item.Tags.Count);
    }

    [Fact]
    public async Task FolderImport_DefaultLinked_NoCacheFiles()
    {
        var subDir = Path.Combine(_fixture.TempDir, "FolderTest");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "A.rfa"), "A_CONTENT");
        File.WriteAllText(Path.Combine(subDir, "B.rfa"), "B_CONTENT");

        var request = new FamilyFolderImportRequest(subDir, false, "BatchCat", null, null);
        var result = await _importService.ImportFolderAsync(request, null);

        Assert.Equal(2, result.SuccessCount);
        Assert.Equal(0, result.SkippedCount);

        foreach (var r in result.Results)
        {
            Assert.True(r.Success);
            var file = await _fixture.GetProvider().GetFileAsync(r.FileId!);
            Assert.Equal(FamilyFileStorageMode.Linked, file!.StorageMode);
            Assert.Null(file.CachedPath);
            Assert.NotNull(file.OriginalPath);
        }
    }
}
