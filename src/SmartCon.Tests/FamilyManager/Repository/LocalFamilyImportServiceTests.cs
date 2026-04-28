using System.IO;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

public sealed class LocalFamilyImportServiceTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly LocalFamilyImportService _importService;

    public LocalFamilyImportServiceTests()
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
    }

    [Fact]
    public async Task ImportFile_NonExistentFile_ReturnsError()
    {
        var request = new FamilyImportRequest(
            FilePath: Path.Combine(_fixture.TempDir, "nonexistent.rfa"),
            Category: null, Tags: null, Description: null);

        var result = await _importService.ImportFileAsync(request);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("not found", result.ErrorMessage);
    }

    [Fact]
    public async Task ImportFile_ValidFile_ReturnsSuccess()
    {
        var path = _fixture.CreateFakeRfaFile("TestFamily.rfa");
        var request = new FamilyImportRequest(path, "Pipes", null, "Test desc");

        var result = await _importService.ImportFileAsync(request);

        Assert.True(result.Success);
        Assert.NotNull(result.CatalogItemId);
        Assert.NotNull(result.VersionId);
        Assert.NotNull(result.FileId);
        Assert.Equal("TestFamily.rfa", result.FileName);
        Assert.False(result.WasSkippedAsDuplicate);
    }

    [Fact]
    public async Task ImportFile_DuplicateByHash_ReturnsSkipped()
    {
        var content = "SAME_CONTENT_FOR_DUPLICATE"u8.ToArray();
        var path1 = _fixture.CreateFakeRfaFileWithContent("Family1.rfa", content);
        var path2 = _fixture.CreateFakeRfaFileWithContent("Family2.rfa", content);

        var result1 = await _importService.ImportFileAsync(new FamilyImportRequest(path1, null, null, null));
        var result2 = await _importService.ImportFileAsync(new FamilyImportRequest(path2, null, null, null));

        Assert.True(result1.Success);
        Assert.False(result1.WasSkippedAsDuplicate);

        Assert.True(result2.Success);
        Assert.True(result2.WasSkippedAsDuplicate);
        Assert.Equal(result1.CatalogItemId, result2.DuplicateCatalogItemId);
    }

    [Fact]
    public async Task ImportFolder_MultipleFiles_ReturnsBatchResult()
    {
        _fixture.CreateFakeRfaFile("A.rfa");
        _fixture.CreateFakeRfaFile("B.rfa");
        _fixture.CreateFakeRfaFile("C.txt");

        var folder = _fixture.TempDir;
        var request = new FamilyFolderImportRequest(folder, false, "TestCategory", null, null);
        var result = await _importService.ImportFolderAsync(request, null);

        Assert.Equal(2, result.TotalFiles);
        Assert.Equal(2, result.SuccessCount);
        Assert.Equal(0, result.ErrorCount);
    }

    [Fact]
    public async Task ImportFolder_NonExistentFolder_Throws()
    {
        var request = new FamilyFolderImportRequest(
            Path.Combine(_fixture.TempDir, "nonexistent_folder"), false, null, null, null);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            _importService.ImportFolderAsync(request, null));
    }

    [Fact]
    public async Task ImportFile_CachesFile()
    {
        var path = _fixture.CreateFakeRfaFile("CachedFamily.rfa");
        var request = new FamilyImportRequest(path, null, null, null, FamilyFileStorageMode.Cached);

        var result = await _importService.ImportFileAsync(request);

        Assert.True(result.Success);

        var item = await _fixture.GetProvider().GetItemAsync(result.CatalogItemId!);
        Assert.NotNull(item);

        var versions = await _fixture.GetProvider().GetVersionsAsync(result.CatalogItemId!);
        Assert.Single(versions);

        var fileRecord = await _fixture.GetProvider().GetFileAsync(result.FileId!);
        Assert.NotNull(fileRecord);
        Assert.NotNull(fileRecord.CachedPath);
    }

    [Fact]
    public async Task ImportFile_WithTags_TagsStored()
    {
        var path = _fixture.CreateFakeRfaFile("TaggedFamily.rfa");
        var tags = new List<string> { "HVAC", "Duct" };
        var request = new FamilyImportRequest(path, null, tags, null);

        var result = await _importService.ImportFileAsync(request);
        Assert.True(result.Success);

        var item = await _fixture.GetProvider().GetItemAsync(result.CatalogItemId!);
        Assert.NotNull(item);
        Assert.Equal(2, item.Tags.Count);
        Assert.Contains("HVAC", item.Tags);
        Assert.Contains("Duct", item.Tags);
    }

    [Fact]
    public async Task ImportFile_CreatedItemHasDraftStatus()
    {
        var path = _fixture.CreateFakeRfaFile("StatusFamily.rfa");
        var request = new FamilyImportRequest(path, null, null, null);

        var result = await _importService.ImportFileAsync(request);
        Assert.True(result.Success);

        var item = await _fixture.GetProvider().GetItemAsync(result.CatalogItemId!);
        Assert.NotNull(item);
        Assert.Equal(FamilyContentStatus.Draft, item.Status);
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }
}
