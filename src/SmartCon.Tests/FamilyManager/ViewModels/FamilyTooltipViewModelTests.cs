using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Moq;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.ViewModels;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

public class FamilyTooltipViewModelTests
{
    private const string CatalogItemId = "catalog-item-1";

    [Fact]
    public void Constructor_SetsDescriptionAndHasDescription_WhenDescriptionProvided()
    {
        var assetService = new Mock<IFamilyAssetService>().Object;

        var vm = new FamilyTooltipViewModel(CatalogItemId, "Some description", assetService);

        Assert.Equal("Some description", vm.Description);
        Assert.True(vm.HasDescription);
    }

    [Fact]
    public void Constructor_HasDescriptionFalse_WhenDescriptionEmpty()
    {
        var assetService = new Mock<IFamilyAssetService>().Object;

        var vm = new FamilyTooltipViewModel(CatalogItemId, "   ", assetService);

        Assert.False(vm.HasDescription);
    }

    [Fact]
    public void Constructor_NullCatalogItemId_ThrowsArgumentNullException()
    {
        var assetService = new Mock<IFamilyAssetService>().Object;

        Assert.Throws<ArgumentNullException>(() => new FamilyTooltipViewModel(null!, "desc", assetService));
    }

    [Fact]
    public void Constructor_NullAssetService_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new FamilyTooltipViewModel(CatalogItemId, "desc", null!));
    }

    [Fact]
    public async Task LoadAsync_NoPrimaryImage_SetsHasAvatarFalse()
    {
        var assetService = new Mock<IFamilyAssetService>();
        assetService
            .Setup(x => x.GetPrimaryImageAsync(CatalogItemId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((FamilyAsset?)null);

        var vm = new FamilyTooltipViewModel(CatalogItemId, "desc", assetService.Object);
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.False(vm.HasAvatar);
        Assert.Null(vm.AvatarImage);
    }

    [Fact]
    public async Task LoadAsync_ResolvePathNull_SetsHasAvatarFalse()
    {
        var asset = new FamilyAsset("asset-1", CatalogItemId, null, FamilyAssetType.Image, "img.png", "files/img.png", 1024, null, DateTimeOffset.UtcNow, true);
        var assetService = new Mock<IFamilyAssetService>();
        assetService
            .Setup(x => x.GetPrimaryImageAsync(CatalogItemId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(asset);
        assetService
            .Setup(x => x.ResolveAssetPathAsync(asset.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var vm = new FamilyTooltipViewModel(CatalogItemId, "desc", assetService.Object);
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.False(vm.HasAvatar);
    }

    [Fact]
    public async Task LoadAsync_ResolvePathMissingFile_SetsHasAvatarFalse()
    {
        var asset = new FamilyAsset("asset-1", CatalogItemId, null, FamilyAssetType.Image, "img.png", "files/img.png", 1024, null, DateTimeOffset.UtcNow, true);
        var assetService = new Mock<IFamilyAssetService>();
        assetService
            .Setup(x => x.GetPrimaryImageAsync(CatalogItemId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(asset);
        assetService
            .Setup(x => x.ResolveAssetPathAsync(asset.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\nonexistent\\img.png");

        var vm = new FamilyTooltipViewModel(CatalogItemId, "desc", assetService.Object);
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.False(vm.HasAvatar);
    }

    [Fact]
    public async Task LoadAsync_SecondCallIsNoop_DoesNotQueryAssetServiceAgain()
    {
        var asset = new FamilyAsset("asset-1", CatalogItemId, null, FamilyAssetType.Image, "img.png", "files/img.png", 1024, null, DateTimeOffset.UtcNow, true);
        var assetService = new Mock<IFamilyAssetService>();
        assetService
            .Setup(x => x.GetPrimaryImageAsync(CatalogItemId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(asset);
        assetService
            .Setup(x => x.ResolveAssetPathAsync(asset.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync("C:\\nonexistent\\img.png");

        var vm = new FamilyTooltipViewModel(CatalogItemId, "desc", assetService.Object);
        await vm.LoadCommand.ExecuteAsync(null);
        await vm.LoadCommand.ExecuteAsync(null);

        assetService.Verify(x => x.GetPrimaryImageAsync(CatalogItemId, null, It.IsAny<CancellationToken>()), Times.Once);
        assetService.Verify(x => x.ResolveAssetPathAsync(asset.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LoadAsync_ValidImageFile_LoadsAvatar()
    {
        var path = CreateTemporaryPng();
        try
        {
            var asset = new FamilyAsset("asset-1", CatalogItemId, null, FamilyAssetType.Image, Path.GetFileName(path), "files/img.png", 1024, null, DateTimeOffset.UtcNow, true);
            var assetService = new Mock<IFamilyAssetService>();
            assetService
                .Setup(x => x.GetPrimaryImageAsync(CatalogItemId, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(asset);
            assetService
                .Setup(x => x.ResolveAssetPathAsync(asset.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(path);

            var vm = new FamilyTooltipViewModel(CatalogItemId, "desc", assetService.Object);
            await vm.LoadCommand.ExecuteAsync(null);

            Assert.True(vm.HasAvatar);
            Assert.NotNull(vm.AvatarImage);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static string CreateTemporaryPng()
    {
        var path = Path.Combine(Path.GetTempPath(), $"smartcon-test-{Guid.NewGuid()}.png");
        using var bitmap = new Bitmap(64, 64);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.White);
        bitmap.Save(path, ImageFormat.Png);
        return path;
    }
}
