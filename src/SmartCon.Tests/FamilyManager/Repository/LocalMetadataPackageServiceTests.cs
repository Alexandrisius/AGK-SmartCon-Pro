using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

public sealed class LocalMetadataPackageServiceTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly LocalFamilyMetadataPackageService _service;
    private readonly LocalCategoryRepository _categoryRepo;
    private readonly LocalAttributeDefinitionRepository _attrDefRepo;
    private readonly LocalCategoryAttributeBindingService _bindingService;

    public LocalMetadataPackageServiceTests()
    {
        _fixture = new TempCatalogFixture();

        _categoryRepo = new LocalCategoryRepository(_fixture.GetDatabase());
        _attrDefRepo = new LocalAttributeDefinitionRepository(_fixture.GetDatabase());
        _bindingService = new LocalCategoryAttributeBindingService(_fixture.GetDatabase(), _categoryRepo, _fixture.GetMigrator());
        _service = new LocalFamilyMetadataPackageService(_categoryRepo, _attrDefRepo, _bindingService, _fixture.GetDatabase(), _fixture.GetMigrator());
    }

    [Fact]
    public async Task ExportCategoriesAsync_EmptyDb_ReturnsEmptyCategories()
    {
        var pkg = await _service.ExportCategoriesAsync();

        Assert.True(pkg.Sections.Categories);
        Assert.False(pkg.Sections.Attributes);
        Assert.False(pkg.Sections.Bindings);
        Assert.Empty(pkg.Categories);
    }

    [Fact]
    public async Task ExportCategoriesAsync_WithHierarchy_ExportCorrectTree()
    {
        var root = await _categoryRepo.AddAsync("Root", null, 0);
        var child = await _categoryRepo.AddAsync("Child", root.Id, 0);
        await _categoryRepo.AddAsync("Leaf", child.Id, 0);

        var pkg = await _service.ExportCategoriesAsync();

        Assert.Single(pkg.Categories);
        var rootNode = pkg.Categories[0];
        Assert.Equal("Root", rootNode.Name);
        Assert.Single(rootNode.Children);
        Assert.Equal("Child", rootNode.Children[0].Name);
        Assert.Single(rootNode.Children[0].Children);
        Assert.Equal("Leaf", rootNode.Children[0].Children[0].Name);
    }

    [Fact]
    public async Task ExportAttributesAsync_EmptyDb_ReturnsEmptyAttributes()
    {
        var pkg = await _service.ExportAttributesAsync();

        Assert.False(pkg.Sections.Categories);
        Assert.True(pkg.Sections.Attributes);
        Assert.False(pkg.Sections.Bindings);
        Assert.Empty(pkg.Attributes);
    }

    [Fact]
    public async Task ExportAttributesAsync_WithDefinitions_ReturnsAll()
    {
        await _attrDefRepo.CreateAsync("Width", "Dimensions");
        await _attrDefRepo.CreateAsync("Height", "Dimensions");
        await _attrDefRepo.CreateAsync("Material", null);

        var pkg = await _service.ExportAttributesAsync();

        Assert.Equal(3, pkg.Attributes.Count);
        Assert.NotNull(pkg.Attributes.FirstOrDefault(a => a.Name == "Width" && a.Group == "Dimensions"));
        Assert.NotNull(pkg.Attributes.FirstOrDefault(a => a.Name == "Height" && a.Group == "Dimensions"));
        Assert.NotNull(pkg.Attributes.FirstOrDefault(a => a.Name == "Material" && a.Group == null));
    }

    [Fact]
    public async Task ExportFullAsync_IncludesAllSections()
    {
        var cat = await _categoryRepo.AddAsync("Pipes", null, 0);
        var attr = await _attrDefRepo.CreateAsync("Diameter", null);
        await _bindingService.CreateBindingAsync(cat.Id, attr.Id, 0);

        var pkg = await _service.ExportFullAsync();

        Assert.True(pkg.Sections.Categories);
        Assert.True(pkg.Sections.Attributes);
        Assert.True(pkg.Sections.Bindings);
        Assert.NotEmpty(pkg.Categories);
        Assert.NotEmpty(pkg.Attributes);
        Assert.NotEmpty(pkg.Bindings);
    }

    [Fact]
    public async Task ImportAsync_Categories_CreatesInDatabase()
    {
        var package = new FamilyMetadataPackage
        {
            Sections = new FamilyMetadataPackageSections { Categories = true, Attributes = false, Bindings = false },
            Categories =
            [
                new FamilyMetadataCategoryNode { Name = "HVAC" },
                new FamilyMetadataCategoryNode { Name = "Plumbing" }
            ]
        };

        var result = await _service.ImportAsync(package);

        Assert.Equal(2, result.CategoriesImported);
        var cats = await _categoryRepo.GetAllAsync();
        Assert.Equal(2, cats.Count);
    }

    [Fact]
    public async Task ImportAsync_NestedCategories_CreatesHierarchy()
    {
        var package = new FamilyMetadataPackage
        {
            Sections = new FamilyMetadataPackageSections { Categories = true, Attributes = false, Bindings = false },
            Categories =
            [
                new FamilyMetadataCategoryNode
                {
                    Name = "Root",
                    Children =
                    [
                        new FamilyMetadataCategoryNode { Name = "Child" }
                    ]
                }
            ]
        };

        var result = await _service.ImportAsync(package);

        Assert.Equal(2, result.CategoriesImported);
        var cats = await _categoryRepo.GetAllAsync();
        Assert.Equal(2, cats.Count);
        var child = cats.FirstOrDefault(c => c.Name == "Child");
        Assert.NotNull(child);
        Assert.NotNull(child.ParentId);
    }

    [Fact]
    public async Task ImportAsync_Attributes_CreatesDefinitions()
    {
        var package = new FamilyMetadataPackage
        {
            Sections = new FamilyMetadataPackageSections { Categories = false, Attributes = true, Bindings = false },
            Attributes =
            [
                new FamilyMetadataAttribute { Name = "Width", Group = "Dimensions" },
                new FamilyMetadataAttribute { Name = "Height", Group = "Dimensions" },
                new FamilyMetadataAttribute { Name = "Material", Group = null }
            ]
        };

        var result = await _service.ImportAsync(package);

        Assert.Equal(3, result.AttributesImported);
        var attrs = await _attrDefRepo.GetAllAsync();
        Assert.Equal(3, attrs.Count);
    }

    [Fact]
    public async Task ImportAsync_DuplicateAttribute_SkipsExisting()
    {
        await _attrDefRepo.CreateAsync("Width", null);

        var package1 = new FamilyMetadataPackage
        {
            Sections = new FamilyMetadataPackageSections { Categories = false, Attributes = true, Bindings = false },
            Attributes = [new FamilyMetadataAttribute { Name = "Width" }]
        };

        var result = await _service.ImportAsync(package1);

        Assert.Equal(0, result.AttributesImported);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public async Task ImportAsync_Bindings_CreatesBindings()
    {
        var package = new FamilyMetadataPackage
        {
            Sections = new FamilyMetadataPackageSections { Categories = true, Attributes = true, Bindings = true },
            Categories = [new FamilyMetadataCategoryNode { Name = "Pipes" }],
            Attributes = [new FamilyMetadataAttribute { Name = "Diameter" }],
            Bindings = [new FamilyMetadataBinding { CategoryPath = "Pipes", AttributeName = "Diameter", SortOrder = 0, IsEnabled = true }]
        };

        var result = await _service.ImportAsync(package);

        Assert.Equal(1, result.BindingsImported);
        var cats = await _categoryRepo.GetAllAsync();
        var pipesCat = cats.First(c => c.Name == "Pipes");
        var bindings = await _bindingService.GetDirectBindingsAsync(pipesCat.Id);
        Assert.Single(bindings);
    }

    [Fact]
    public async Task ImportAsync_BindingForMissingCategory_Skips()
    {
        var package = new FamilyMetadataPackage
        {
            Sections = new FamilyMetadataPackageSections { Categories = true, Attributes = true, Bindings = true },
            Categories = [new FamilyMetadataCategoryNode { Name = "Existing" }],
            Attributes = [new FamilyMetadataAttribute { Name = "Width" }],
            Bindings = [new FamilyMetadataBinding { CategoryPath = "NonExistent", AttributeName = "Width", SortOrder = 0, IsEnabled = true }]
        };

        var result = await _service.ImportAsync(package);

        Assert.Equal(0, result.BindingsImported);
        Assert.Equal(1, result.BindingsSkipped);
    }

    [Fact]
    public async Task ExportFullAsync_ThenImport_RoundTrip()
    {
        var cat1 = await _categoryRepo.AddAsync("Pipes", null, 0);
        var cat2 = await _categoryRepo.AddAsync("Fittings", null, 1);
        var attr1 = await _attrDefRepo.CreateAsync("Diameter", "Dimensions");
        var attr2 = await _attrDefRepo.CreateAsync("Length", "Dimensions");
        await _bindingService.CreateBindingAsync(cat1.Id, attr1.Id, 0);
        await _bindingService.CreateBindingAsync(cat1.Id, attr2.Id, 1);
        await _bindingService.CreateBindingAsync(cat2.Id, attr1.Id, 0);

        var exported = await _service.ExportFullAsync();

        var targetFixture = new TempCatalogFixture();
        try
        {
            await targetFixture.MigrateAsync();
            var targetCatRepo = new LocalCategoryRepository(targetFixture.GetDatabase());
            var targetAttrRepo = new LocalAttributeDefinitionRepository(targetFixture.GetDatabase());
            var targetBinding = new LocalCategoryAttributeBindingService(targetFixture.GetDatabase(), targetCatRepo, targetFixture.GetMigrator());
            var targetService = new LocalFamilyMetadataPackageService(targetCatRepo, targetAttrRepo, targetBinding, targetFixture.GetDatabase(), targetFixture.GetMigrator());

            var importResult = await targetService.ImportAsync(exported);

            Assert.Equal(2, importResult.CategoriesImported);
            Assert.Equal(2, importResult.AttributesImported);
            Assert.Equal(3, importResult.BindingsImported);

            var importedCats = await targetCatRepo.GetAllAsync();
            Assert.Equal(2, importedCats.Count);

            var importedAttrs = await targetAttrRepo.GetAllAsync();
            Assert.Equal(2, importedAttrs.Count);

            var pipesCat = importedCats.FirstOrDefault(c => c.Name == "Pipes");
            Assert.NotNull(pipesCat);
            var pipesBindings = await targetBinding.GetDirectBindingsAsync(pipesCat.Id);
            Assert.Equal(2, pipesBindings.Count);

            var fittingsCat = importedCats.FirstOrDefault(c => c.Name == "Fittings");
            Assert.NotNull(fittingsCat);
            var fittingsBindings = await targetBinding.GetDirectBindingsAsync(fittingsCat.Id);
            Assert.Single(fittingsBindings);
        }
        finally
        {
            targetFixture.Dispose();
        }
    }

    public void Dispose() => _fixture.Dispose();
}
