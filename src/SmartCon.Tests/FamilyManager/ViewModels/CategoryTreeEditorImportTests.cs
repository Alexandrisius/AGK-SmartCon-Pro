using System.Collections.ObjectModel;
using Moq;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Models.Metadata;
using SmartCon.FamilyManager.Services;
using SmartCon.FamilyManager.Services.LocalCatalog;
using SmartCon.FamilyManager.ViewModels;
using SmartCon.Tests.FamilyManager.Repository;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

public sealed class CategoryTreeEditorImportTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly LocalCategoryRepository _categoryRepository;
    private readonly LocalAttributeDefinitionRepository _attributeRepository;
    private readonly LocalCategoryAttributeBindingService _bindingService;
    private readonly Mock<IFamilyManagerDialogService> _dialogMock;
    private readonly Mock<IFamilyManagerViewModelFactory> _factoryMock;
    private readonly FamilyManagerMetadataMediator _mediator;

    public CategoryTreeEditorImportTests()
    {
        _fixture = new TempCatalogFixture();
        _fixture.MigrateAsync().GetAwaiter().GetResult();

        _categoryRepository = new LocalCategoryRepository(_fixture.GetDatabase());
        _attributeRepository = new LocalAttributeDefinitionRepository(_fixture.GetDatabase());
        _bindingService = new LocalCategoryAttributeBindingService(
            _fixture.GetDatabase(), _categoryRepository, _fixture.GetMigrator());

        _dialogMock = new Mock<IFamilyManagerDialogService>();
        _factoryMock = new Mock<IFamilyManagerViewModelFactory>();
        _mediator = new FamilyManagerMetadataMediator();
    }

    public void Dispose() => _fixture.Dispose();

    private CategoryTreeEditorViewModel CreateVm() =>
        new(_categoryRepository, _dialogMock.Object, _attributeRepository, _bindingService, _mediator, _factoryMock.Object,
            new LocalValidationRuleRepository(_fixture.GetDatabase(), _fixture.GetMigrator()));

    [Fact]
    public async Task OkAsync_ReimportExistingCategory_DoesNotCreateDuplicate()
    {
        var existing = await _categoryRepository.AddAsync("HVAC", null, 0);
        var allBefore = await _categoryRepository.GetAllAsync();
        Assert.Single(allBefore);

        var vm = CreateVm();
        var importedNode = new CategoryNodeViewModel(Guid.NewGuid().ToString(), "HVAC", null, "HVAC")
        {
            SortOrder = 0,
            OriginalSortOrder = 0,
            IsNew = true,
            IsDirty = true
        };
        vm.RootNodes = new ObservableCollection<CategoryNodeViewModel> { importedNode };

        var savedCount = 0;
        vm.Saved += () => savedCount++;
        await vm.OkCommand.ExecuteAsync(null);

        var allAfter = await _categoryRepository.GetAllAsync();
        Assert.Single(allAfter);
        Assert.Equal(existing.Id, allAfter[0].Id);
        Assert.Equal(1, savedCount);
    }

    [Fact]
    public async Task OkAsync_ReimportNestedCategory_PreservesParentId()
    {
        var parent = await _categoryRepository.AddAsync("Pipes", null, 0);
        await _categoryRepository.AddAsync("Steel", parent.Id, 0);

        var vm = CreateVm();
        var parentVm = new CategoryNodeViewModel(Guid.NewGuid().ToString(), "Pipes", null, "Pipes")
        {
            SortOrder = 0,
            OriginalSortOrder = 0,
            IsNew = true,
            IsDirty = true
        };
        var childVm = new CategoryNodeViewModel(Guid.NewGuid().ToString(), "Steel", parentVm.CategoryId, "Pipes > Steel")
        {
            SortOrder = 0,
            OriginalSortOrder = 0,
            IsNew = true,
            IsDirty = true
        };
        parentVm.Children.Add(childVm);
        vm.RootNodes = new ObservableCollection<CategoryNodeViewModel> { parentVm };

        await vm.OkCommand.ExecuteAsync(null);

        var all = await _categoryRepository.GetAllAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal(parent.Id, all.First(c => c.Name == "Pipes").Id);
        Assert.Equal(parent.Id, all.First(c => c.Name == "Steel").ParentId);
    }

    [Fact]
    public async Task OkAsync_NewCategory_CreatesNewRecord()
    {
        var vm = CreateVm();
        var newNode = new CategoryNodeViewModel(Guid.NewGuid().ToString(), "Plumbing", null, "Plumbing")
        {
            SortOrder = 0,
            OriginalSortOrder = 0,
            IsNew = true,
            IsDirty = true
        };
        vm.RootNodes = new ObservableCollection<CategoryNodeViewModel> { newNode };

        await vm.OkCommand.ExecuteAsync(null);

        var all = await _categoryRepository.GetAllAsync();
        Assert.Single(all);
        Assert.Equal("Plumbing", all[0].Name);
    }

    [Fact]
    public async Task OkAsync_PendingBindingImports_CreatesBindingsOnOk()
    {
        var hvac = await _categoryRepository.AddAsync("HVAC", null, 0);
        await _attributeRepository.CreateAsync("Width", "Dimensions");

        var vm = CreateVm();
        SetPendingBindings(vm, new List<MetadataExportBinding>
        {
            new() { CategoryPath = "HVAC", AttributeName = "Width", SortOrder = 0, IsEnabled = true }
        });

        var newNode = new CategoryNodeViewModel(Guid.NewGuid().ToString(), "HVAC", null, "HVAC")
        {
            SortOrder = 0,
            OriginalSortOrder = 0,
            IsNew = true,
            IsDirty = true
        };
        vm.RootNodes = new ObservableCollection<CategoryNodeViewModel> { newNode };

        await vm.OkCommand.ExecuteAsync(null);

        var bindings = await _bindingService.GetDirectBindingsAsync(hvac.Id);
        Assert.Single(bindings);
        Assert.Null(GetPendingBindings(vm));
    }

    [Fact]
    public async Task OkAsync_PendingBindingImports_MissingCategory_SkipsAndContinues()
    {
        var hvac = await _categoryRepository.AddAsync("HVAC", null, 0);
        await _attributeRepository.CreateAsync("Width", null);
        await _attributeRepository.CreateAsync("Height", null);

        var vm = CreateVm();
        SetPendingBindings(vm, new List<MetadataExportBinding>
        {
            new() { CategoryPath = "NonExistent", AttributeName = "Width", SortOrder = 0, IsEnabled = true },
            new() { CategoryPath = "HVAC", AttributeName = "Height", SortOrder = 0, IsEnabled = true }
        });

        var newNode = new CategoryNodeViewModel(Guid.NewGuid().ToString(), "HVAC", null, "HVAC")
        {
            SortOrder = 0,
            OriginalSortOrder = 0,
            IsNew = true,
            IsDirty = true
        };
        vm.RootNodes = new ObservableCollection<CategoryNodeViewModel> { newNode };

        await vm.OkCommand.ExecuteAsync(null);

        var bindings = await _bindingService.GetDirectBindingsAsync(hvac.Id);
        Assert.Single(bindings);
    }

    [Fact]
    public async Task OkAsync_RaisesMetadataChanged_WhenPendingBindingsExist()
    {
        var hvac = await _categoryRepository.AddAsync("HVAC", null, 0);
        await _attributeRepository.CreateAsync("Width", null);

        var vm = CreateVm();
        SetPendingBindings(vm, new List<MetadataExportBinding>
        {
            new() { CategoryPath = "HVAC", AttributeName = "Width", SortOrder = 0, IsEnabled = true }
        });

        var newNode = new CategoryNodeViewModel(Guid.NewGuid().ToString(), "HVAC", null, "HVAC")
        {
            SortOrder = 0,
            OriginalSortOrder = 0,
            IsNew = true,
            IsDirty = true
        };
        vm.RootNodes = new ObservableCollection<CategoryNodeViewModel> { newNode };

        var fired = 0;
        _mediator.MetadataChanged += () => fired++;

        await vm.OkCommand.ExecuteAsync(null);

        Assert.Equal(1, fired);
    }

    [Fact]
    public async Task ImportFromJsonAsync_NewAttributes_AreSavedToDatabaseImmediately()
    {
        var vm = CreateVm();

        var jsonPath = CreateTempJson(new MetadataExportPackage
        {
            Categories = { new() { Name = "HVAC" } },
            Attributes =
            {
                new() { Name = "Width", Group = "Dimensions" },
                new() { Name = "Height", Group = "Dimensions" }
            }
        });

        _dialogMock.Setup(s => s.ShowOpenJsonDialog(It.IsAny<string>(), It.IsAny<string?>())).Returns(jsonPath);

        await vm.ImportFromJsonCommand.ExecuteAsync(null);

        var allAttrs = await _attributeRepository.GetAllAsync();
        Assert.Equal(2, allAttrs.Count);
        Assert.Contains(allAttrs, a => a.Name == "Width");
        Assert.Contains(allAttrs, a => a.Name == "Height");
    }

    [Fact]
    public async Task ImportFromJsonAsync_DuplicateAttributes_AreSkipped()
    {
        await _attributeRepository.CreateAsync("Width", "Dimensions");

        var vm = CreateVm();
        var jsonPath = CreateTempJson(new MetadataExportPackage
        {
            Attributes = { new() { Name = "Width", Group = "OtherGroup" } }
        });

        _dialogMock.Setup(s => s.ShowOpenJsonDialog(It.IsAny<string>(), It.IsAny<string?>())).Returns(jsonPath);

        await vm.ImportFromJsonCommand.ExecuteAsync(null);

        var allAttrs = await _attributeRepository.GetAllAsync();
        Assert.Single(allAttrs);
    }

    [Fact]
    public async Task ImportFromJsonAsync_EmptyAttributeName_AreSkipped()
    {
        var vm = CreateVm();
        var jsonPath = CreateTempJson(new MetadataExportPackage
        {
            Attributes =
            {
                new() { Name = "   " },
                new() { Name = string.Empty }
            }
        });

        _dialogMock.Setup(s => s.ShowOpenJsonDialog(It.IsAny<string>(), It.IsAny<string?>())).Returns(jsonPath);

        await vm.ImportFromJsonCommand.ExecuteAsync(null);

        var allAttrs = await _attributeRepository.GetAllAsync();
        Assert.Empty(allAttrs);
    }

    [Fact]
    public async Task ImportFromJsonAsync_PendingBindings_MarksUnsavedChanges()
    {
        var vm = CreateVm();
        var jsonPath = CreateTempJson(new MetadataExportPackage
        {
            Categories = { new() { Name = "HVAC" } },
            Attributes = { new() { Name = "Width", Group = null } },
            Bindings = { new() { CategoryPath = "HVAC", AttributeName = "Width", SortOrder = 0, IsEnabled = true } }
        });

        _dialogMock.Setup(s => s.ShowOpenJsonDialog(It.IsAny<string>(), It.IsAny<string?>())).Returns(jsonPath);

        await vm.ImportFromJsonCommand.ExecuteAsync(null);

        Assert.True(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task ImportFromJsonAsync_RaisesMediator_WhenAttributesImported()
    {
        var vm = CreateVm();
        var jsonPath = CreateTempJson(new MetadataExportPackage
        {
            Attributes = { new() { Name = "Width" } }
        });

        _dialogMock.Setup(s => s.ShowOpenJsonDialog(It.IsAny<string>(), It.IsAny<string?>())).Returns(jsonPath);

        var fired = 0;
        _mediator.MetadataChanged += () => fired++;

        await vm.ImportFromJsonCommand.ExecuteAsync(null);

        Assert.Equal(1, fired);
    }

    [Fact]
    public async Task ImportFromJsonAsync_DoesNotRaiseMediator_WhenNoAttributesImported()
    {
        var vm = CreateVm();
        await _attributeRepository.CreateAsync("Width", null);

        var jsonPath = CreateTempJson(new MetadataExportPackage
        {
            Attributes = { new() { Name = "Width" } }
        });

        _dialogMock.Setup(s => s.ShowOpenJsonDialog(It.IsAny<string>(), It.IsAny<string?>())).Returns(jsonPath);

        var fired = 0;
        _mediator.MetadataChanged += () => fired++;

        await vm.ImportFromJsonCommand.ExecuteAsync(null);

        Assert.Equal(0, fired);
    }

    [Fact]
    public async Task ImportFromJsonAsync_CategoriesVisible_AttributesInDb_BindingsPending()
    {
        var vm = CreateVm();
        var jsonPath = CreateTempJson(new MetadataExportPackage
        {
            Categories =
            {
                new() { Name = "HVAC" },
                new() { Name = "Plumbing" }
            },
            Attributes =
            {
                new() { Name = "Width" },
                new() { Name = "Height" }
            },
            Bindings =
            {
                new() { CategoryPath = "HVAC", AttributeName = "Width", SortOrder = 0, IsEnabled = true }
            }
        });

        _dialogMock.Setup(s => s.ShowOpenJsonDialog(It.IsAny<string>(), It.IsAny<string?>())).Returns(jsonPath);

        await vm.ImportFromJsonCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.RootNodes.Count);
        Assert.Equal(2, (await _attributeRepository.GetAllAsync()).Count);
        Assert.NotNull(GetPendingBindings(vm));
        Assert.Single(GetPendingBindings(vm)!);
    }

    private static void SetPendingBindings(CategoryTreeEditorViewModel vm, List<MetadataExportBinding> bindings)
    {
        var field = typeof(CategoryTreeEditorViewModel).GetField(
            "_pendingBindingImports",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field!.SetValue(vm, bindings);
        var update = typeof(CategoryTreeEditorViewModel).GetMethod(
            "UpdateHasUnsavedChanges",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        update!.Invoke(vm, null);
    }

    private static List<MetadataExportBinding>? GetPendingBindings(CategoryTreeEditorViewModel vm)
    {
        var field = typeof(CategoryTreeEditorViewModel).GetField(
            "_pendingBindingImports",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (List<MetadataExportBinding>?)field!.GetValue(vm);
    }

    [Fact]
    public async Task OkAsync_BindingWithValidationRules_RulesImported()
    {
        var vm = CreateVm();
        var jsonPath = CreateTempJson(new MetadataExportPackage
        {
            Categories = { new() { Name = "HVAC" } },
            Attributes = { new() { Name = "Pressure" } },
            Bindings =
            {
                new()
                {
                    CategoryPath = "HVAC",
                    AttributeName = "Pressure",
                    SortOrder = 0,
                    IsEnabled = true,
                    ValidationRules =
                    {
                        new() { Operator = "HasValue", IsEnabled = true },
                        new() { Operator = "Between", MinValue = 15.0, MaxValue = 100.0, IsEnabled = true },
                    }
                }
            }
        });

        _dialogMock.Setup(s => s.ShowOpenJsonDialog(It.IsAny<string>(), It.IsAny<string?>())).Returns(jsonPath);
        await vm.ImportFromJsonCommand.ExecuteAsync(null);
        await vm.OkCommand.ExecuteAsync(null);

        var cats = await _categoryRepository.GetAllAsync();
        var cat = Assert.Single(cats);
        var bindings = await _bindingService.GetDirectBindingsAsync(cat.Id);
        var binding = Assert.Single(bindings);

        var ruleRepo = new LocalValidationRuleRepository(_fixture.GetDatabase(), _fixture.GetMigrator());
        var rules = await ruleRepo.GetRulesForBindingAsync(binding.Id);
        Assert.Equal(2, rules.Count);
        Assert.Contains(rules, r => r.Operator == SmartCon.Core.Models.FamilyManager.ValidationRuleOperator.HasValue);
        Assert.Contains(rules, r =>
            r.Operator == SmartCon.Core.Models.FamilyManager.ValidationRuleOperator.Between
            && r.MinValue == 15.0 && r.MaxValue == 100.0);
    }

    [Fact]
    public async Task OkAsync_ExistingBinding_RulesNotOverwritten()
    {
        var cat = await _categoryRepository.AddAsync("HVAC", null, 0);
        var attr = await _attributeRepository.CreateAsync("Pressure", null);
        var existing = await _bindingService.CreateBindingAsync(cat.Id, attr.Id, 0);
        var ruleRepo = new LocalValidationRuleRepository(_fixture.GetDatabase(), _fixture.GetMigrator());
        await ruleRepo.CreateRuleAsync(new SmartCon.Core.Models.FamilyManager.ValidationRule(
            string.Empty, existing.Id, SmartCon.Core.Models.FamilyManager.ValidationRuleOperator.IsEmpty,
            null, null, null, null, null, 0, true));

        var vm = CreateVm();
        var jsonPath = CreateTempJson(new MetadataExportPackage
        {
            Categories = { new() { Name = "HVAC" } },
            Attributes = { new() { Name = "Pressure" } },
            Bindings =
            {
                new()
                {
                    CategoryPath = "HVAC",
                    AttributeName = "Pressure",
                    ValidationRules = { new() { Operator = "HasValue" } }
                }
            }
        });

        _dialogMock.Setup(s => s.ShowOpenJsonDialog(It.IsAny<string>(), It.IsAny<string?>())).Returns(jsonPath);
        await vm.ImportFromJsonCommand.ExecuteAsync(null);
        await vm.OkCommand.ExecuteAsync(null);

        var rules = await ruleRepo.GetRulesForBindingAsync(existing.Id);
        var rule = Assert.Single(rules);
        Assert.Equal(SmartCon.Core.Models.FamilyManager.ValidationRuleOperator.IsEmpty, rule.Operator);
    }

    [Fact]
    public async Task OkAsync_UnknownRuleOperator_RuleSkipped_BindingImported()
    {
        var vm = CreateVm();
        var jsonPath = CreateTempJson(new MetadataExportPackage
        {
            Categories = { new() { Name = "HVAC" } },
            Attributes = { new() { Name = "Pressure" } },
            Bindings =
            {
                new()
                {
                    CategoryPath = "HVAC",
                    AttributeName = "Pressure",
                    ValidationRules =
                    {
                        new() { Operator = "FutureOperatorV99" },
                        new() { Operator = "HasValue" },
                    }
                }
            }
        });

        _dialogMock.Setup(s => s.ShowOpenJsonDialog(It.IsAny<string>(), It.IsAny<string?>())).Returns(jsonPath);
        await vm.ImportFromJsonCommand.ExecuteAsync(null);
        await vm.OkCommand.ExecuteAsync(null);

        var cats = await _categoryRepository.GetAllAsync();
        var bindings = await _bindingService.GetDirectBindingsAsync(cats[0].Id);
        var ruleRepo = new LocalValidationRuleRepository(_fixture.GetDatabase(), _fixture.GetMigrator());
        var rules = await ruleRepo.GetRulesForBindingAsync(bindings[0].Id);

        var rule = Assert.Single(rules);
        Assert.Equal(SmartCon.Core.Models.FamilyManager.ValidationRuleOperator.HasValue, rule.Operator);
    }

    private static string CreateTempJson(MetadataExportPackage package)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"test_{Guid.NewGuid():N}.json");
        var json = System.Text.Json.JsonSerializer.Serialize(package, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            WriteIndented = false
        });
        System.IO.File.WriteAllText(path, json);
        return path;
    }
}
