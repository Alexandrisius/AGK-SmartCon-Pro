using System.IO;
using System.Text;
using SmartCon.FamilyManager.Services.LocalCatalog;
using SmartCon.Tests.FamilyManager.Repository;
using Xunit;

namespace SmartCon.Tests.FamilyManager;

public class TypeCatalogEncodingTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TempCatalogFixture _fixture;

    static TypeCatalogEncodingTests()
    {
        // Регистрируем CodePages encoding provider для Windows-1251/1252 и др.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public TypeCatalogEncodingTests()
    {
        _fixture = new TempCatalogFixture();
        _tempDir = _fixture.TempDir;
    }

    [Theory]
    [InlineData("Windows-1251", "\u0412\u0435\u043D\u0442\u0438\u043B\u044F\u0442\u043E\u0440")] // "Вентилятор"
    [InlineData("Windows-1252", "H\u00E9licopt\u00E8re")] // "Hélicoptère"
    [InlineData("UTF-8", "\u0412\u0435\u043D\u0442\u0438\u043B\u044F\u0442\u043E\u0440")] // UTF-8 Cyrillic
    public void CharsetDetector_DetectsEncoding(string encodingName, string content)
    {
        // Arrange
        var encoding = Encoding.GetEncoding(encodingName);
        var bytes = encoding.GetBytes(content);

        // Act
        var result = UtfUnknown.CharsetDetector.DetectFromBytes(bytes);

        // Assert
        Assert.NotNull(result.Detected);
        Assert.True(result.Detected.Confidence > 0.5f, 
            $"Low confidence for {encodingName}: {result.Detected.Confidence}");
    }

    [Fact]
    public async Task TypeCatalog_WithCyrillicInWindows1251_ReadsCorrectly()
    {
        // Arrange: create a Type Catalog in Windows-1251
        var txtPath = Path.Combine(_tempDir, "test_types.txt");
        var rfaPath = Path.Combine(_tempDir, "test_types.rfa");

        // Write Type Catalog with Cyrillic (Ф used as diameter symbol in Russian Type Catalogs)
        var encoding = Encoding.GetEncoding("Windows-1251");
        var catalogContent = ",\u0414\u043B\u0438\u043D\u0430##length##millimeters\n" +
                            "\u0412\u0435\u043D\u0442\u0438\u043B\u044F\u0442\u043E\u0440_\u0424100,100\n" +
                            "\u0412\u0435\u043D\u0442\u0438\u043B\u044F\u0442\u043E\u0440_\u0424125,125";
        File.WriteAllText(txtPath, catalogContent, encoding);

        // Write fake RFA
        File.WriteAllText(rfaPath, "FAKE_RFA");

        // Act: import via LocalFamilyImportService
        var importService = new LocalFamilyImportService(
            _fixture.GetDatabase(),
            _fixture.GetMigrator(),
            _fixture.GetProvider(),
            _fixture.GetPathResolver(),
            new FileNameOnlyMetadataExtractionService(new Sha256FileHasher()),
            _fixture.GetTypeRepository(),
            _fixture.GetValueRepository(),
            _fixture.GetRunRepository());

        var result = await importService.ImportFileAsync(
            new SmartCon.Core.Models.FamilyManager.FamilyImportRequest(rfaPath, 2025, null, null, null),
            CancellationToken.None);

        // Assert
        Assert.True(result.Success);

        var types = await _fixture.GetTypeRepository()
            .GetTypesForItemAsync(result.CatalogItemId!);

        Assert.Equal(2, types.Count);
        Assert.Contains(types, t => t.Name.Contains("\u0424100")); // Ф used as diameter symbol in Russian Type Catalogs
        Assert.Contains(types, t => t.Name.Contains("\u0412\u0435\u043D\u0442\u0438\u043B\u044F\u0442\u043E\u0440")); // Cyrillic
    }

    public void Dispose()
    {
        _fixture.Dispose();
        GC.SuppressFinalize(this);
    }
}
