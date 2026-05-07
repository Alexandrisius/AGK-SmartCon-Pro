using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

public sealed class FamilyDataImportServiceTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly FamilyDataImportService _service;
    private readonly LocalFamilyDataImportRunRepository _runRepo;
    private readonly LocalAttributeValueRepository _valueRepo;
    private readonly LocalCategoryAttributeBindingService _bindingService;
    private readonly LocalFamilyTypeRepository _typeRepo;
    private readonly LocalAttributeDefinitionRepository _attrDefRepo;
    private readonly LocalCatalogProvider _catalogProvider;
    private readonly LocalFamilyFileResolver _fileResolver;
    private readonly LocalCategoryRepository _categoryRepo;

    public FamilyDataImportServiceTests()
    {
        _fixture = new TempCatalogFixture();
        _fixture.MigrateAsync().GetAwaiter().GetResult();

        _runRepo = new LocalFamilyDataImportRunRepository(_fixture.GetDatabase());
        _valueRepo = new LocalAttributeValueRepository(_fixture.GetDatabase());
        _categoryRepo = new LocalCategoryRepository(_fixture.GetDatabase());
        _bindingService = new LocalCategoryAttributeBindingService(_fixture.GetDatabase(), _categoryRepo, _fixture.GetMigrator());
        _typeRepo = new LocalFamilyTypeRepository(_fixture.GetDatabase());
        _attrDefRepo = new LocalAttributeDefinitionRepository(_fixture.GetDatabase());
        _catalogProvider = new LocalCatalogProvider(_fixture.GetDatabase());
        _fileResolver = new LocalFamilyFileResolver(_fixture.GetDatabase());

        _service = new FamilyDataImportService(
            _runRepo, _valueRepo, _bindingService, _typeRepo, _attrDefRepo, _catalogProvider, _fileResolver);
    }

    [Fact]
    public async Task PrepareExtractionAsync_ItemNotFound_ReturnsFailure()
    {
        var result = await _service.PrepareExtractionAsync("nonexistent_id", 2025);

        Assert.False(result.Success);
        Assert.Equal("Catalog item not found", result.ErrorMessage);
    }

    [Fact]
    public async Task PrepareExtractionAsync_FileNotFound_ReturnsFailure()
    {
        var itemId = await SeedCatalogItemAsync("NoFileFamily");

        var result = await _service.PrepareExtractionAsync(itemId, 2025);

        Assert.False(result.Success);
        Assert.Equal("Family file not found", result.ErrorMessage);
    }

    [Fact]
    public async Task PrepareExtractionAsync_Success_ReturnsParameterNames()
    {
        var catId = await SeedCategoryAsync("Pipes");
        await SeedAttributeAndBindAsync(catId, "Width", 0);
        await SeedAttributeAndBindAsync(catId, "Height", 1);
        var itemId = await SeedCatalogItemAsync("PipeFamily", catId);
        await SeedFileAsync(itemId, "PipeFamily.rfa");

        var result = await _service.PrepareExtractionAsync(itemId, 2025);

        Assert.True(result.Success);
        Assert.Null(result.ErrorMessage);
        Assert.NotNull(result.ResolvedFilePath);
        Assert.Equal(2, result.ParameterNames.Count);
        Assert.Contains("Width", result.ParameterNames);
        Assert.Contains("Height", result.ParameterNames);
    }

    [Fact]
    public async Task SaveExtractionResultAsync_CreatesRunAndValues()
    {
        var itemId = await SeedCatalogItemAsync("SaveTestFamily");
        await SeedAttributeDefAsync("Param1");
        await SeedAttributeDefAsync("Param2");

        var extractionResult = new FamilyExtractionResult(
            true,
            [new FamilyExtractionTypeValues("TypeA", 0,
            [
                new FamilyExtractionValueResult("Param1", AttributeScope.Type, "String", "val1", null, null, null, AttributeValueStatus.Found, null),
                new FamilyExtractionValueResult("Param2", AttributeScope.Instance, "Double", "3.14", null, 3.14, null, AttributeValueStatus.Found, null)
            ])],
            null,
            2025);

        var result = await _service.SaveExtractionResultAsync(itemId, extractionResult, null, null);

        Assert.True(result.Success);
        Assert.NotNull(result.RunId);
        Assert.Equal(1, result.TypesCount);
        Assert.Equal(2, result.AttributesFoundCount);
        Assert.Equal(0, result.AttributesMissingCount);

        var run = await _runRepo.GetLatestRunAsync(itemId);
        Assert.NotNull(run);
        Assert.Equal(result.RunId, run.Id);

        var types = await _typeRepo.GetTypesForItemAsync(itemId);
        Assert.Single(types);
        Assert.Equal("TypeA", types[0].Name);

        var values = await _valueRepo.GetValuesForItemAsync(itemId, null);
        Assert.Equal(2, values.Count);

        var foundCount = await _valueRepo.GetFoundCountAsync(itemId, null);
        var missingCount = await _valueRepo.GetMissingCountAsync(itemId, null);
        Assert.Equal(2, foundCount);
        Assert.Equal(0, missingCount);
    }

    [Fact]
    public async Task SaveExtractionResultAsync_PartialStatus_WhenMissingValues()
    {
        var itemId = await SeedCatalogItemAsync("PartialFamily");
        await SeedAttributeDefAsync("Found");
        await SeedAttributeDefAsync("Missing");

        var extractionResult = new FamilyExtractionResult(
            true,
            [new FamilyExtractionTypeValues("TypeA", 0,
            [
                new FamilyExtractionValueResult("Found", AttributeScope.Type, "String", "val", null, null, null, AttributeValueStatus.Found, null),
                new FamilyExtractionValueResult("Missing", AttributeScope.Type, null, null, null, null, null, AttributeValueStatus.MissingParameter, "Not found")
            ])],
            null,
            2025);

        var result = await _service.SaveExtractionResultAsync(itemId, extractionResult, null, null);

        Assert.True(result.Success);
        Assert.Equal(1, result.AttributesFoundCount);
        Assert.Equal(1, result.AttributesMissingCount);
    }

    [Fact]
    public async Task SaveExtractionResultAsync_MapsParameterNameToAttributeId()
    {
        var itemId = await SeedCatalogItemAsync("MapFamily");
        var widthDef = await _attrDefRepo.CreateAsync("Width", "Dimensions");

        var extractionResult = new FamilyExtractionResult(
            true,
            [new FamilyExtractionTypeValues("TypeA", 0,
            [
                new FamilyExtractionValueResult("Width", AttributeScope.Type, "Double", "100", null, 100.0, null, AttributeValueStatus.Found, null)
            ])],
            null,
            2025);

        await _service.SaveExtractionResultAsync(itemId, extractionResult, null, null);

        var values = await _valueRepo.GetValuesForItemAsync(itemId, null);
        Assert.Single(values);
        Assert.Equal(widthDef.Id, values[0].AttributeId);
        Assert.NotEqual("Width", values[0].AttributeId);
    }

    [Fact]
    public async Task SaveExtractionResultAsync_CaseInsensitiveNameLookup()
    {
        var itemId = await SeedCatalogItemAsync("CaseFamily");
        var def = await _attrDefRepo.CreateAsync("Width", null);

        var extractionResult = new FamilyExtractionResult(
            true,
            [new FamilyExtractionTypeValues("TypeA", 0,
            [
                new FamilyExtractionValueResult("WIDTH", AttributeScope.Type, "Double", "50", null, 50.0, null, AttributeValueStatus.Found, null)
            ])],
            null,
            2025);

        await _service.SaveExtractionResultAsync(itemId, extractionResult, null, null);

        var values = await _valueRepo.GetValuesForItemAsync(itemId, null);
        Assert.Single(values);
        Assert.Equal(def.Id, values[0].AttributeId);
        Assert.Equal("WIDTH", values[0].ParameterName);
    }

    [Fact]
    public async Task SaveExtractionResultAsync_WithVersionId_SavesCorrectly()
    {
        var itemId = await SeedCatalogItemAsync("VersionedFamily");
        await SeedAttributeDefAsync("P1");

        var extractionResult = new FamilyExtractionResult(
            true,
            [new FamilyExtractionTypeValues("TypeA", 0,
            [
                new FamilyExtractionValueResult("P1", AttributeScope.Type, "String", "v", null, null, null, AttributeValueStatus.Found, null)
            ])],
            null,
            2025);

        var result = await _service.SaveExtractionResultAsync(itemId, extractionResult, "v1", "file1");

        Assert.True(result.Success);

        var types = await _typeRepo.GetTypesForItemVersionAsync(itemId, "v1");
        Assert.Single(types);
        Assert.Equal("v1", types[0].VersionId);
        Assert.Equal("file1", types[0].FileId);

        var values = await _valueRepo.GetValuesForItemAsync(itemId, "v1");
        Assert.Single(values);
        Assert.Equal("v1", values[0].VersionId);
        Assert.Equal("file1", values[0].FileId);
    }

    private async Task SeedAttributeDefAsync(string name)
    {
        await _attrDefRepo.CreateAsync(name, null);
    }

    private async Task<string> SeedCatalogItemAsync(string name, string? categoryId = null)
    {
        var id = Guid.NewGuid().ToString();
        using var connection = _fixture.GetDatabase().CreateConnection();
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO catalog_items (id, name, normalized_name, category_id, content_status, created_at_utc, updated_at_utc) VALUES (@id, @name, @normName, @catId, 'Active', @createdAt, @updatedAt)";
        cmd.Parameters.Add(new SqliteParameter("@id", id));
        cmd.Parameters.Add(new SqliteParameter("@name", name));
        cmd.Parameters.Add(new SqliteParameter("@normName", name.ToLowerInvariant()));
        cmd.Parameters.Add(new SqliteParameter("@catId", (object?)categoryId ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@createdAt", DateTimeOffset.UtcNow.ToString("o")));
        cmd.Parameters.Add(new SqliteParameter("@updatedAt", DateTimeOffset.UtcNow.ToString("o")));
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private async Task SeedFileAsync(string catalogItemId, string fileName)
    {
        var filePath = _fixture.CreateFakeRfaFile(fileName);
        var relativePath = fileName;
        var versionId = Guid.NewGuid().ToString();
        var fileId = Guid.NewGuid().ToString();
        var versionLabel = "v1";

        using var connection = _fixture.GetDatabase().CreateConnection();
        await connection.OpenAsync();

        using var fCmd = connection.CreateCommand();
        fCmd.CommandText = "INSERT INTO family_files (id, relative_path, file_name, size_bytes, sha256, revit_major_version, imported_at_utc) VALUES (@id, @relPath, @fileName, @size, @sha, 2025, @importedAt)";
        fCmd.Parameters.Add(new SqliteParameter("@id", fileId));
        fCmd.Parameters.Add(new SqliteParameter("@relPath", relativePath));
        fCmd.Parameters.Add(new SqliteParameter("@fileName", fileName));
        fCmd.Parameters.Add(new SqliteParameter("@size", new FileInfo(filePath).Length));
        fCmd.Parameters.Add(new SqliteParameter("@sha", "fake_hash"));
        fCmd.Parameters.Add(new SqliteParameter("@importedAt", DateTimeOffset.UtcNow.ToString("o")));
        await fCmd.ExecuteNonQueryAsync();

        using var vCmd = connection.CreateCommand();
        vCmd.CommandText = "INSERT INTO catalog_versions (id, catalog_item_id, file_id, version_label, sha256, revit_major_version, published_at_utc) VALUES (@id, @itemId, @fileId, @label, @sha, 2025, @publishedAt)";
        vCmd.Parameters.Add(new SqliteParameter("@id", versionId));
        vCmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
        vCmd.Parameters.Add(new SqliteParameter("@fileId", fileId));
        vCmd.Parameters.Add(new SqliteParameter("@label", versionLabel));
        vCmd.Parameters.Add(new SqliteParameter("@sha", "fake_hash"));
        vCmd.Parameters.Add(new SqliteParameter("@publishedAt", DateTimeOffset.UtcNow.ToString("o")));
        await vCmd.ExecuteNonQueryAsync();

        using var uCmd = connection.CreateCommand();
        uCmd.CommandText = "UPDATE catalog_items SET current_version_label = @label WHERE id = @id";
        uCmd.Parameters.Add(new SqliteParameter("@label", versionLabel));
        uCmd.Parameters.Add(new SqliteParameter("@id", catalogItemId));
        await uCmd.ExecuteNonQueryAsync();
    }

    private async Task<string> SeedCategoryAsync(string name, string? parentId = null)
    {
        var node = await _categoryRepo.AddAsync(name, parentId, 0);
        return node.Id;
    }

    private async Task<string> SeedAttributeAndBindAsync(string categoryId, string attrName, int sortOrder)
    {
        var def = await _attrDefRepo.CreateAsync(attrName, null);
        await _bindingService.CreateBindingAsync(categoryId, def.Id, sortOrder);
        return def.Id;
    }

    public void Dispose() => _fixture.Dispose();
}
