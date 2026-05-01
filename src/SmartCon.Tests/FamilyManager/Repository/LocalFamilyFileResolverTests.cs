using System.IO;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

public sealed class LocalFamilyFileResolverTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly LocalFamilyFileResolver _resolver;
    private readonly LocalFamilyImportService _importService;

    public LocalFamilyFileResolverTests()
    {
        _fixture = new TempCatalogFixture();
        _fixture.MigrateAsync().GetAwaiter().GetResult();

        _resolver = new LocalFamilyFileResolver(_fixture.GetDatabase());

        var hasher = new Sha256FileHasher();
        var metadataService = new FileNameOnlyMetadataExtractionService(hasher);
        _importService = new LocalFamilyImportService(
            _fixture.GetDatabase(),
            _fixture.GetMigrator(),
            _fixture.GetProvider(),
            _fixture.GetPathResolver(),
            metadataService);
    }

    private async Task<string> SeedItemAsync(string fileName, int revitVersion = 2025)
    {
        var path = _fixture.CreateFakeRfaFile(fileName);
        var result = await _importService.ImportFileAsync(new FamilyImportRequest(path, revitVersion, null, null, null));
        Assert.True(result.Success);
        return result.CatalogItemId!;
    }

    [Fact]
    public async Task ResolveForLoadAsync_ExistingItem_ReturnsResolvedFile()
    {
        var itemId = await SeedItemAsync("ResolveMe.rfa", 2025);

        var resolved = await _resolver.ResolveForLoadAsync(itemId, 2025);

        Assert.NotNull(resolved);
        Assert.NotEmpty(resolved.AbsolutePath);
        Assert.True(File.Exists(resolved.AbsolutePath));
        Assert.Equal(itemId, resolved.CatalogItemId);
        Assert.NotNull(resolved.VersionId);
    }

    [Fact]
    public async Task ResolveForLoadAsync_NonExistentItem_ReturnsEmptyPath()
    {
        var resolved = await _resolver.ResolveForLoadAsync("nonexistent_item_id", 2025);

        Assert.NotNull(resolved);
        Assert.Equal(string.Empty, resolved.AbsolutePath);
    }

    [Fact]
    public async Task ResolveForLoadAsync_DifferentRevitVersion_ReturnsClosestMatch()
    {
        var itemId = await SeedItemAsync("Versioned.rfa", 2024);

        var resolved = await _resolver.ResolveForLoadAsync(itemId, 2025);

        Assert.NotNull(resolved);
        Assert.NotEmpty(resolved.AbsolutePath);
        Assert.True(File.Exists(resolved.AbsolutePath));
    }

    [Fact]
    public async Task GetDatabaseRoot_ReturnsTempDir()
    {
        var root = _resolver.GetDatabaseRoot();

        Assert.NotNull(root);
        Assert.Equal(_fixture.GetDatabaseRoot(), root);
        Assert.True(Directory.Exists(root));
    }

    [Fact]
    public async Task ResolveForLoadAsync_PathContainsFileName()
    {
        var itemId = await SeedItemAsync("NamedFile.rfa", 2025);

        var resolved = await _resolver.ResolveForLoadAsync(itemId, 2025);

        Assert.NotNull(resolved);
        Assert.Contains("NamedFile.rfa", resolved.AbsolutePath);
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }
}
