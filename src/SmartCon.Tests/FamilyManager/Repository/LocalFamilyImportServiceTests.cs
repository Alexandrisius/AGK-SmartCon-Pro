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
            _fixture.GetPathResolver(),
            metadataService);
    }

    [Fact]
    public async Task ImportFile_NonExistentFile_ReturnsError()
    {
        var request = new FamilyImportRequest(
            FilePath: Path.Combine(_fixture.TempDir, "nonexistent.rfa"),
            RevitMajorVersion: 2025,
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
        var request = new FamilyImportRequest(path, 2025, "Pipes", null, "Test desc");

        var result = await _importService.ImportFileAsync(request);

        Assert.True(result.Success);
        Assert.NotNull(result.CatalogItemId);
        Assert.NotNull(result.VersionId);
        Assert.NotNull(result.FileId);
        Assert.Equal("TestFamily.rfa", result.FileName);
        Assert.Equal("v1", result.VersionLabel);
        Assert.False(result.WasSkippedAsDuplicate);
    }

    [Fact]
    public async Task ImportFile_SameNameAndHash_SkipsDuplicate()
    {
        var content = "SAME_CONTENT_FOR_DUPLICATE"u8.ToArray();
        var path1 = _fixture.CreateFakeRfaFileWithContent("Family1.rfa", content);

        var result1 = await _importService.ImportFileAsync(new FamilyImportRequest(path1, 2025, null, null, null));
        var result2 = await _importService.ImportFileAsync(new FamilyImportRequest(path1, 2025, null, null, null));

        Assert.True(result1.Success);
        Assert.False(result1.WasSkippedAsDuplicate);

        Assert.True(result2.Success);
        Assert.True(result2.WasSkippedAsDuplicate);
        Assert.Equal(result1.CatalogItemId, result2.CatalogItemId);
    }

    [Fact]
    public async Task ImportFile_CopiesToManagedStorage()
    {
        var path = _fixture.CreateFakeRfaFile("ManagedFamily.rfa");
        var request = new FamilyImportRequest(path, 2025, null, null, null);

        var result = await _importService.ImportFileAsync(request);

        Assert.True(result.Success);
        var file = await _fixture.GetProvider().GetFileAsync(result.FileId!);
        Assert.NotNull(file);
        Assert.NotEmpty(file.RelativePath);
        Assert.Equal(2025, file.RevitMajorVersion);

        var absolutePath = Path.Combine(_fixture.GetDatabaseRoot(), file.RelativePath);
        Assert.True(File.Exists(absolutePath));
    }

    [Fact]
    public async Task ImportFile_SameNameDifferentRevit_CreatesNewVersion()
    {
        var content1 = "CONTENT_R2024"u8.ToArray();
        var content2 = "CONTENT_R2025"u8.ToArray();
        _fixture.CreateFakeRfaFileWithContent("SameName.rfa", content1);
        var path2 = _fixture.CreateFakeRfaFileWithContent("SameName2.rfa", content2);

        var result1 = await _importService.ImportFileAsync(
            new FamilyImportRequest(Path.Combine(_fixture.TempDir, "SameName.rfa"), 2024, null, null, null));

        Assert.True(result1.Success);
        Assert.Equal("v1", result1.VersionLabel);
    }

    [Fact]
    public async Task ImportFolder_MultipleFiles_ReturnsBatchResult()
    {
        _fixture.CreateFakeRfaFile("A.rfa");
        _fixture.CreateFakeRfaFile("B.rfa");
        File.WriteAllText(Path.Combine(_fixture.TempDir, "C.txt"), "not an rfa");

        var request = new FamilyFolderImportRequest(_fixture.TempDir, 2025, false, "TestCategory", null, null);
        var result = await _importService.ImportFolderAsync(request, null);

        Assert.Equal(2, result.TotalFiles);
        Assert.Equal(2, result.SuccessCount);
    }

    [Fact]
    public async Task ImportFile_WithTags_TagsStored()
    {
        var path = _fixture.CreateFakeRfaFile("TaggedFamily.rfa");
        var tags = new List<string> { "HVAC", "Duct" };
        var request = new FamilyImportRequest(path, 2025, null, tags, null);

        var result = await _importService.ImportFileAsync(request);
        Assert.True(result.Success);

        var item = await _fixture.GetProvider().GetItemAsync(result.CatalogItemId!);
        Assert.NotNull(item);
        Assert.Equal(2, item.Tags.Count);
        Assert.Contains("HVAC", item.Tags);
        Assert.Contains("Duct", item.Tags);
    }

    [Fact]
    public async Task ImportFile_DefaultStatusIsActive()
    {
        var path = _fixture.CreateFakeRfaFile("StatusFamily.rfa");
        var request = new FamilyImportRequest(path, 2025, null, null, null);

        var result = await _importService.ImportFileAsync(request);
        Assert.True(result.Success);

        var item = await _fixture.GetProvider().GetItemAsync(result.CatalogItemId!);
        Assert.NotNull(item);
        Assert.Equal(ContentStatus.Active, item.ContentStatus);
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }
}
