using System.Text.Json;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Json;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

public sealed class LocalMetadataImportFlowIntegrationTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly LocalFamilyMetadataPackageService _service;

    public LocalMetadataImportFlowIntegrationTests()
    {
        _fixture = new TempCatalogFixture();
        var categoryRepo = new LocalCategoryRepository(_fixture.GetDatabase());
        var attrDefRepo = new LocalAttributeDefinitionRepository(_fixture.GetDatabase());
        var bindingService = new LocalCategoryAttributeBindingService(_fixture.GetDatabase(), categoryRepo, _fixture.GetMigrator());
        _service = new LocalFamilyMetadataPackageService(categoryRepo, attrDefRepo, bindingService, _fixture.GetDatabase(), _fixture.GetMigrator());
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task ImportAsync_PackageWithNullAttributes_DoesNotThrow()
    {
        var package = new FamilyMetadataPackage
        {
            Sections = new FamilyMetadataPackageSections { Categories = true, Attributes = true, Bindings = true },
            Categories = [new FamilyMetadataCategoryNode { Name = "Pipes" }],
            Attributes = null!,
            Bindings = null!
        };

        var result = await _service.ImportAsync(package);

        Assert.Equal(0, result.AttributesImported);
        Assert.Equal(0, result.BindingsImported);
    }

    [Fact]
    public async Task ImportAsync_PackageWithMixedNullAndPopulated_HandlesBoth()
    {
        await _service.ImportAsync(new FamilyMetadataPackage
        {
            Sections = new FamilyMetadataPackageSections { Categories = true, Attributes = true, Bindings = true },
            Categories = [new FamilyMetadataCategoryNode { Name = "Existing" }],
            Attributes = [new FamilyMetadataAttribute { Name = "Width" }],
            Bindings = null!
        });

        var result = await _service.ImportAsync(new FamilyMetadataPackage
        {
            Sections = new FamilyMetadataPackageSections { Categories = false, Attributes = true, Bindings = false },
            Attributes = [new FamilyMetadataAttribute { Name = "Width" }, new FamilyMetadataAttribute { Name = "Height" }],
            Bindings = null!
        });

        Assert.Equal(1, result.AttributesImported);
        Assert.Contains(result.Warnings, w => w.Contains("Width"));
    }

    [Fact]
    public async Task RoundTrip_ExportFullThenDeserializeThenImport_PreservesAllData()
    {
        var catRepo = new LocalCategoryRepository(_fixture.GetDatabase());
        var attrRepo = new LocalAttributeDefinitionRepository(_fixture.GetDatabase());
        var binding = new LocalCategoryAttributeBindingService(_fixture.GetDatabase(), catRepo, _fixture.GetMigrator());

        var root = await catRepo.AddAsync("Pipes", null, 0);
        var fitting = await catRepo.AddAsync("Fittings", null, 1);
        var diam = await attrRepo.CreateAsync("Diameter", "Dimensions");
        var len = await attrRepo.CreateAsync("Length", "Dimensions");
        await binding.CreateBindingAsync(root.Id, diam.Id, 0);
        await binding.CreateBindingAsync(root.Id, len.Id, 1);
        await binding.CreateBindingAsync(fitting.Id, diam.Id, 0);

        var exported = await _service.ExportFullAsync();
        var json = JsonSerializer.Serialize(exported, JsonOptions.RelaxedWriteIndented);

        var deserialized = JsonSerializer.Deserialize<FamilyMetadataPackage>(json, JsonOptions.Default);

        var normalized = deserialized!.WithNonNullCollections();

        using var targetFixture = new TempCatalogFixture();
        await targetFixture.MigrateAsync();
        var targetCatRepo = new LocalCategoryRepository(targetFixture.GetDatabase());
        var targetAttrRepo = new LocalAttributeDefinitionRepository(targetFixture.GetDatabase());
        var targetBinding = new LocalCategoryAttributeBindingService(targetFixture.GetDatabase(), targetCatRepo, targetFixture.GetMigrator());
        var targetService = new LocalFamilyMetadataPackageService(targetCatRepo, targetAttrRepo, targetBinding, targetFixture.GetDatabase(), targetFixture.GetMigrator());

        var importResult = await targetService.ImportAsync(normalized);

        Assert.Equal(2, importResult.CategoriesImported);
        Assert.Equal(2, importResult.AttributesImported);
        Assert.Equal(3, importResult.BindingsImported);
    }

    [Fact]
    public async Task RoundTrip_ExportFullWithCyrillicNames_SurvivesUnicodeEscapes()
    {
        var catRepo = new LocalCategoryRepository(_fixture.GetDatabase());
        var attrRepo = new LocalAttributeDefinitionRepository(_fixture.GetDatabase());
        var binding = new LocalCategoryAttributeBindingService(_fixture.GetDatabase(), catRepo, _fixture.GetMigrator());

        var root = await catRepo.AddAsync("Трубы", null, 0);
        var attr = await attrRepo.CreateAsync("Диаметр", "Размеры");
        await binding.CreateBindingAsync(root.Id, attr.Id, 0);

        var exported = await _service.ExportFullAsync();
        var json = JsonSerializer.Serialize(exported, JsonOptions.RelaxedWriteIndented);

        Assert.Contains("Трубы", json);
        Assert.Contains("Диаметр", json);

        var deserialized = JsonSerializer.Deserialize<FamilyMetadataPackage>(json, JsonOptions.Default);

        Assert.NotNull(deserialized);
        Assert.Single(deserialized!.Categories);
        Assert.Equal("Трубы", deserialized.Categories[0].Name);
    }
}
