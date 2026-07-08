using SmartCon.Core.Models.FamilyManager;
using Xunit;

namespace SmartCon.Tests.FamilyManager;

public sealed class FamilyAssetTypeExtensionsTests
{
    [Theory]
    [InlineData("photo.png", FamilyAssetType.Image)]
    [InlineData("photo.jpg", FamilyAssetType.Image)]
    [InlineData("photo.jpeg", FamilyAssetType.Image)]
    [InlineData("photo.bmp", FamilyAssetType.Image)]
    [InlineData("photo.gif", FamilyAssetType.Image)]
    [InlineData("photo.tif", FamilyAssetType.Image)]
    [InlineData("photo.tiff", FamilyAssetType.Image)]
    [InlineData("video.mp4", FamilyAssetType.Video)]
    [InlineData("video.avi", FamilyAssetType.Video)]
    [InlineData("video.mov", FamilyAssetType.Video)]
    [InlineData("video.wmv", FamilyAssetType.Video)]
    [InlineData("video.mkv", FamilyAssetType.Video)]
    [InlineData("doc.pdf", FamilyAssetType.Document)]
    [InlineData("doc.doc", FamilyAssetType.Document)]
    [InlineData("doc.docx", FamilyAssetType.Document)]
    [InlineData("readme.txt", FamilyAssetType.Document)]
    [InlineData("doc.rtf", FamilyAssetType.Document)]
    [InlineData("model.glb", FamilyAssetType.Model3D)]
    [InlineData("model.gltf", FamilyAssetType.Model3D)]
    [InlineData("model.fbx", FamilyAssetType.Model3D)]
    [InlineData("model.obj", FamilyAssetType.Model3D)]
    [InlineData("model.stl", FamilyAssetType.Model3D)]
    [InlineData("data.csv", FamilyAssetType.LookupTable)]
    [InlineData("report.xls", FamilyAssetType.Spreadsheet)]
    [InlineData("report.xlsx", FamilyAssetType.Spreadsheet)]
    [InlineData("report.xlsm", FamilyAssetType.Spreadsheet)]
    [InlineData("unknown.xyz", FamilyAssetType.Other)]
    [InlineData("noextension", FamilyAssetType.Other)]
    [InlineData("", FamilyAssetType.Other)]
    public void DetectFromExtension_ReturnsExpectedType(string filePath, FamilyAssetType expected)
    {
        var result = FamilyAssetTypeExtensions.DetectFromExtension(filePath);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("PHOTO.PNG", FamilyAssetType.Image)]
    [InlineData("Photo.JPG", FamilyAssetType.Image)]
    [InlineData("VIDEO.MP4", FamilyAssetType.Video)]
    [InlineData("Doc.PDF", FamilyAssetType.Document)]
    [InlineData("MODEL.GLB", FamilyAssetType.Model3D)]
    [InlineData("DATA.CSV", FamilyAssetType.LookupTable)]
    [InlineData("REPORT.XLSX", FamilyAssetType.Spreadsheet)]
    public void DetectFromExtension_IsCaseInsensitive(string filePath, FamilyAssetType expected)
    {
        var result = FamilyAssetTypeExtensions.DetectFromExtension(filePath);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void DetectFromExtension_WithFullPath_ResolvesCorrectly()
    {
        var result = FamilyAssetTypeExtensions.DetectFromExtension(@"C:\Users\test\Documents\photo.png");
        Assert.Equal(FamilyAssetType.Image, result);
    }

    [Fact]
    public void AllAssetFilters_ContainsAllCategories()
    {
        var filters = FamilyAssetTypeExtensions.AllAssetFilters();

        Assert.Contains("*.png", filters);
        Assert.Contains("*.mp4", filters);
        Assert.Contains("*.pdf", filters);
        Assert.Contains("*.glb", filters);
        Assert.Contains("*.csv", filters);
        Assert.Contains("*.xlsx", filters);
        Assert.Contains("*.*", filters);
    }

    [Fact]
    public void AllAssetFilters_StartsWithAllSupportedFilter()
    {
        var filters = FamilyAssetTypeExtensions.AllAssetFilters();

        Assert.StartsWith("All supported files", filters);
    }
}
