using System.IO;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

public sealed class LocalFamilyAssetServiceTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly LocalFamilyAssetService _service;
    private readonly LocalFamilyImportService _importService;

    public LocalFamilyAssetServiceTests()
    {
        _fixture = new TempCatalogFixture();


        _service = new LocalFamilyAssetService(
            _fixture.GetDatabase(),
            _fixture.GetPathResolver(),
            _fixture.GetMigrator());

        
        var metadataService = new FileMetadataExtractionService();
        _importService = new LocalFamilyImportService(
            _fixture.GetDatabase(),
            _fixture.GetMigrator(),
            _fixture.GetProvider(),
            _fixture.GetPathResolver(),
            metadataService,
            _fixture.GetTypeRepository(),
            _fixture.GetValueRepository(),
            _fixture.GetRunRepository(),
            _fixture.GetTypeCatalogBaker());
    }

    private async Task<string> SeedItemAsync(string fileName)
    {
        var path = _fixture.CreateFakeRfaFile(fileName);
        var result = await _importService.ImportFileAsync(new FamilyImportRequest(path, 2025, null, null, null));
        Assert.True(result.Success);
        return result.CatalogItemId!;
    }

    private string CreateFakeAssetFile(string fileName, string content = "FAKE_ASSET")
    {
        var path = Path.Combine(_fixture.TempDir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task GetAssetsAsync_NoAssets_ReturnsEmptyList()
    {
        var itemId = await SeedItemAsync("NoAssets.rfa");

        var assets = await _service.GetAssetsAsync(itemId);

        Assert.Empty(assets);
    }

    [Fact]
    public async Task AddAssetAsync_ImageAsset_AppearsInGet()
    {
        var itemId = await SeedItemAsync("AddImg.rfa");
        var sourcePath = CreateFakeAssetFile("preview.png", "PNG_DATA");

        var asset = await _service.AddAssetAsync(itemId, "v1", FamilyAssetType.Image, sourcePath, "Preview image");

        Assert.NotNull(asset.Id);
        Assert.Equal(itemId, asset.CatalogItemId);
        Assert.Equal(FamilyAssetType.Image, asset.AssetType);
        Assert.Equal("Preview image", asset.Description);

        var assets = await _service.GetAssetsAsync(itemId);
        Assert.Single(assets);
        Assert.Equal(asset.Id, assets[0].Id);
    }

    [Fact]
    public async Task DeleteAssetAsync_RemovesAsset()
    {
        var itemId = await SeedItemAsync("DelAsset.rfa");
        var sourcePath = CreateFakeAssetFile("delete_me.png", "TO_DELETE");

        var asset = await _service.AddAssetAsync(itemId, null, FamilyAssetType.Image, sourcePath, null);
        var deleted = await _service.DeleteAssetAsync(asset.Id);

        Assert.True(deleted);
        var assets = await _service.GetAssetsAsync(itemId);
        Assert.Empty(assets);
    }

    [Fact]
    public async Task DeleteAssetAsync_NonExistent_ReturnsFalse()
    {
        var deleted = await _service.DeleteAssetAsync("nonexistent_id");
        Assert.False(deleted);
    }

    [Fact]
    public async Task SetPrimaryAssetAsync_SetsFlag()
    {
        var itemId = await SeedItemAsync("Primary.rfa");
        var source1 = CreateFakeAssetFile("img1.png", "IMG1");
        var source2 = CreateFakeAssetFile("img2.png", "IMG2");

        var asset1 = await _service.AddAssetAsync(itemId, null, FamilyAssetType.Image, source1, null);
        var asset2 = await _service.AddAssetAsync(itemId, null, FamilyAssetType.Image, source2, null);

        await _service.SetPrimaryAssetAsync(asset2.Id);

        var primary = await _service.GetPrimaryImageAsync(itemId);
        Assert.NotNull(primary);
        Assert.Equal(asset2.Id, primary.Id);

        var all = await _service.GetAssetsAsync(itemId);
        var first = all.First(a => a.Id == asset1.Id);
        Assert.False(first.IsPrimary);
    }

    [Fact]
    public async Task ResolvePrimaryAssetAsync_ReturnsPath()
    {
        var itemId = await SeedItemAsync("ResolvePrimary.rfa");
        var sourcePath = CreateFakeAssetFile("resolved.png", "RESOLVE_ME");

        var asset = await _service.AddAssetAsync(itemId, null, FamilyAssetType.Image, sourcePath, null);
        await _service.SetPrimaryAssetAsync(asset.Id);

        var path = await _service.ResolveAssetPathAsync(asset.Id);
        Assert.NotNull(path);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task AddAssetAsync_DifferentAssetTypes_StoredCorrectly()
    {
        var itemId = await SeedItemAsync("MultiType.rfa");
        var imgSource = CreateFakeAssetFile("doc.pdf", "PDF_CONTENT");
        var xlsSource = CreateFakeAssetFile("data.xlsx", "XLS_CONTENT");

        var docAsset = await _service.AddAssetAsync(itemId, null, FamilyAssetType.Document, imgSource, "Spec");
        var xlsAsset = await _service.AddAssetAsync(itemId, null, FamilyAssetType.Spreadsheet, xlsSource, "Data");

        Assert.Equal(FamilyAssetType.Document, docAsset.AssetType);
        Assert.Equal(FamilyAssetType.Spreadsheet, xlsAsset.AssetType);

        var assets = await _service.GetAssetsAsync(itemId);
        Assert.Equal(2, assets.Count);
    }

    [Fact]
    public async Task AddAssetAsync_NonExistentSource_ThrowsFileNotFoundException()
    {
        var itemId = await SeedItemAsync("BadSource.rfa");

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _service.AddAssetAsync(itemId, null, FamilyAssetType.Image,
                Path.Combine(_fixture.TempDir, "missing.png"), null));
    }

    // --- ADR-047 / issue #131: derived avatar.png ---

    [Fact]
    public async Task SaveAvatarAsync_CreatesFile_GetAvatarImagePathPrefersIt()
    {
        var itemId = await SeedItemAsync("AvatarSave.rfa");
        var source = CreateFakeAssetFile("crop.png", "CROPPED_PNG");

        await _service.SaveAvatarAsync(itemId, source);

        var avatarPath = _fixture.GetPathResolver().GetAvatarPath(itemId);
        Assert.True(File.Exists(avatarPath));
        Assert.Equal("CROPPED_PNG", File.ReadAllText(avatarPath));

        var resolved = await _service.GetAvatarImagePathAsync(itemId);
        Assert.Equal(avatarPath, resolved);
    }

    [Fact]
    public async Task GetAvatarImagePathAsync_NoAvatarFile_FallsBackToPrimaryImage()
    {
        var itemId = await SeedItemAsync("AvatarFallback.rfa");
        var source = CreateFakeAssetFile("primary.png", "PRIMARY_IMG");
        var asset = await _service.AddAssetAsync(itemId, null, FamilyAssetType.Image, source, null);
        await _service.SetPrimaryAssetAsync(asset.Id);

        var resolved = await _service.GetAvatarImagePathAsync(itemId);

        Assert.NotNull(resolved);
        Assert.True(File.Exists(resolved));
        Assert.Equal(await _service.ResolveAssetPathAsync(asset.Id), resolved);
    }

    [Fact]
    public async Task GetAvatarImagePathAsync_Nothing_ReturnsNull()
    {
        var itemId = await SeedItemAsync("AvatarNone.rfa");

        var resolved = await _service.GetAvatarImagePathAsync(itemId);

        Assert.Null(resolved);
    }

    [Fact]
    public async Task SetPrimaryAssetAsync_DeletesStaleAvatarFile()
    {
        var itemId = await SeedItemAsync("AvatarInvalidate.rfa");
        var crop = CreateFakeAssetFile("old-crop.png", "OLD_CROP");
        await _service.SaveAvatarAsync(itemId, crop);
        var avatarPath = _fixture.GetPathResolver().GetAvatarPath(itemId);
        Assert.True(File.Exists(avatarPath));

        var source = CreateFakeAssetFile("new-primary.png", "NEW_IMG");
        var asset = await _service.AddAssetAsync(itemId, null, FamilyAssetType.Image, source, null);

        await _service.SetPrimaryAssetAsync(asset.Id);

        Assert.False(File.Exists(avatarPath));
    }

    [Fact]
    public async Task ClearAvatarAsync_RemovesFileAndPrimaryFlags()
    {
        var itemId = await SeedItemAsync("AvatarClear.rfa");
        var source = CreateFakeAssetFile("img.png", "IMG");
        var asset = await _service.AddAssetAsync(itemId, null, FamilyAssetType.Image, source, null);
        await _service.SetPrimaryAssetAsync(asset.Id);
        var crop = CreateFakeAssetFile("clear-crop.png", "CROP");
        await _service.SaveAvatarAsync(itemId, crop);

        await _service.ClearAvatarAsync(itemId);

        Assert.False(File.Exists(_fixture.GetPathResolver().GetAvatarPath(itemId)));
        Assert.Null(await _service.GetPrimaryImageAsync(itemId));
        Assert.Null(await _service.GetAvatarImagePathAsync(itemId));
        // Source image asset itself is kept.
        var assets = await _service.GetAssetsAsync(itemId);
        Assert.Single(assets);
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }
}
