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
    public async Task AddRootCommand_CreatesCategoryImmediately()
    {
        var vm = CreateVm();
        _dialogMock.Setup(s => s.ShowInputDialog(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns("Plumbing");

        await vm.AddRootCommand.ExecuteAsync(null);

        var all = await _categoryRepository.GetAllAsync();
        var cat = Assert.Single(all);
        Assert.Equal("Plumbing", cat.Name);
        Assert.Single(vm.RootNodes);
        Assert.Equal(cat.Id, vm.RootNodes[0].CategoryId);
    }

    [Fact]
    public async Task AddChildCommand_CreatesChildImmediately()
    {
        var parent = await _categoryRepository.AddAsync("Pipes", null, 0);
        var vm = CreateVm();
        await vm.InitializeAsync();
        vm.SelectedNode = vm.RootNodes[0];

        _dialogMock.Setup(s => s.ShowInputDialog(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns("Steel");

        await vm.AddChildCommand.ExecuteAsync(null);

        var all = await _categoryRepository.GetAllAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal(parent.Id, all.First(c => c.Name == "Steel").ParentId);
        Assert.Single(vm.RootNodes[0].Children);
    }

    [Fact]
    public async Task RenameCommand_RenamesImmediately()
    {
        var cat = await _categoryRepository.AddAsync("Pipes", null, 0);
        var vm = CreateVm();
        await vm.InitializeAsync();
        vm.SelectedNode = vm.RootNodes[0];

        _dialogMock.Setup(s => s.ShowInputDialog(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns("Tubes");

        await vm.RenameCommand.ExecuteAsync(null);

        Assert.Equal("Tubes", (await _categoryRepository.GetAllAsync())[0].Name);
        Assert.Equal("Tubes", vm.RootNodes[0].DisplayName);
    }

    [Fact]
    public async Task ImportFromJsonAsync_Bindings_CreatedImmediately()
    {
        var vm = CreateVm();
        var jsonPath = CreateTempJson(new MetadataExportPackage
        {
            Categories = { new() { Name = "HVAC" } },
            Attributes = { new() { Name = "Width", Group = "Dimensions" } },
            Bindings = { new() { CategoryPath = "HVAC", AttributeName = "Width", SortOrder = 0, IsEnabled = true } }
        });

        _dialogMock.Setup(s => s.ShowOpenJsonDialog(It.IsAny<string>(), It.IsAny<string?>())).Returns(jsonPath);

        await vm.ImportFromJsonCommand.ExecuteAsync(null);

        var cat = Assert.Single(await _categoryRepository.GetAllAsync());
        var bindings = await _bindingService.GetDirectBindingsAsync(cat.Id);
        Assert.Single(bindings);
    }

    [Fact]
    public async Task ImportFromJsonAsync_MissingBindingCategory_SkipsAndContinues()
    {
        var hvac = await _categoryRepository.AddAsync("HVAC", null, 0);
        await _attributeRepository.CreateAsync("Width", null);
        await _attributeRepository.CreateAsync("Height", null);

        var vm = CreateVm();
        var jsonPath = CreateTempJson(new MetadataExportPackage
        {
            Bindings =
            {
                new() { CategoryPath = "NonExistent", AttributeName = "Width", SortOrder = 0, IsEnabled = true },
                new() { CategoryPath = "HVAC", AttributeName = "Height", SortOrder = 0, IsEnabled = true }
            }
        });

        _dialogMock.Setup(s => s.ShowOpenJsonDialog(It.IsAny<string>(), It.IsAny<string?>())).Returns(jsonPath);

        await vm.ImportFromJsonCommand.ExecuteAsync(null);

        var bindings = await _bindingService.GetDirectBindingsAsync(hvac.Id);
        Assert.Single(bindings);
        Assert.Equal("Height", (await _attributeRepository.GetAllAsync())
            .First(a => a.Id == bindings[0].AttributeId).Name);
    }

    [Fact]
    public async Task ImportFromJsonAsync_RaisesMetadataChanged_Always()
    {
        var vm = CreateVm();
        var jsonPath = CreateTempJson(new MetadataExportPackage
        {
            Categories = { new() { Name = "HVAC" } },
            Attributes = { new() { Name = "Width" } },
            Bindings = { new() { CategoryPath = "HVAC", AttributeName = "Width", SortOrder = 0, IsEnabled = true } }
        });

        _dialogMock.Setup(s => s.ShowOpenJsonDialog(It.IsAny<string>(), It.IsAny<string?>())).Returns(jsonPath);

        var fired = 0;
        _mediator.MetadataChanged += () => fired++;

        await vm.ImportFromJsonCommand.ExecuteAsync(null);

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
    public async Task ImportFromJsonAsync_AtomicImport_CommitsEverything()
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

        var cat = Assert.Single(await _categoryRepository.GetAllAsync());
        Assert.Single(await _bindingService.GetDirectBindingsAsync(cat.Id));
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
    public async Task ImportFromJsonAsync_NothingNew_StillReloadsAndRaisesMediator()
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

        Assert.Equal(1, fired);
    }

    [Fact]
    public async Task ImportFromJsonAsync_AllSections_CommittedAndTreeReloaded()
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

        var hvac = (await _categoryRepository.GetAllAsync()).First(c => c.Name == "HVAC");
        var bindings = await _bindingService.GetDirectBindingsAsync(hvac.Id);
        Assert.Single(bindings);
    }

    [Fact]
    public async Task ImportFromJsonAsync_BindingWithValidationRules_RulesImported()
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
    public async Task ImportFromJsonAsync_ExistingBinding_RulesNotOverwritten()
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

        var rules = await ruleRepo.GetRulesForBindingAsync(existing.Id);
        var rule = Assert.Single(rules);
        Assert.Equal(SmartCon.Core.Models.FamilyManager.ValidationRuleOperator.IsEmpty, rule.Operator);
    }

    [Fact]
    public async Task ImportFromJsonAsync_UnknownRuleOperator_RuleSkipped_BindingImported()
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
                        new() { Operator = "999" },
                        new() { Operator = "HasValue" },
                    }
                }
            }
        });

        _dialogMock.Setup(s => s.ShowOpenJsonDialog(It.IsAny<string>(), It.IsAny<string?>())).Returns(jsonPath);
        await vm.ImportFromJsonCommand.ExecuteAsync(null);

        var cats = await _categoryRepository.GetAllAsync();
        var bindings = await _bindingService.GetDirectBindingsAsync(cats[0].Id);
        var ruleRepo = new LocalValidationRuleRepository(_fixture.GetDatabase(), _fixture.GetMigrator());
        var rules = await ruleRepo.GetRulesForBindingAsync(bindings[0].Id);

        var rule = Assert.Single(rules);
        Assert.Equal(SmartCon.Core.Models.FamilyManager.ValidationRuleOperator.HasValue, rule.Operator);
    }

    [Fact]
    public async Task ImportFromJsonAsync_ExistingCategoryPath_ReusedNotDuplicated()
    {
        var parent = await _categoryRepository.AddAsync("Pipes", null, 0);
        var steel = await _categoryRepository.AddAsync("Steel", parent.Id, 0);

        var vm = CreateVm();
        var jsonPath = CreateTempJson(new MetadataExportPackage
        {
            Categories =
            {
                new()
                {
                    Name = "Pipes",
                    Children = { new() { Name = "Steel" }, new() { Name = "Copper" } }
                }
            }
        });

        _dialogMock.Setup(s => s.ShowOpenJsonDialog(It.IsAny<string>(), It.IsAny<string?>())).Returns(jsonPath);

        await vm.ImportFromJsonCommand.ExecuteAsync(null);

        var all = await _categoryRepository.GetAllAsync();
        Assert.Equal(3, all.Count);
        Assert.Equal(parent.Id, all.First(c => c.Name == "Pipes").Id);
        Assert.Equal(steel.Id, all.First(c => c.Name == "Steel").Id);
        Assert.Equal(parent.Id, all.First(c => c.Name == "Copper").ParentId);
    }

    [Fact]
    public async Task MoveNodeCommand_MovesCategoryImmediately()
    {
        var parent = await _categoryRepository.AddAsync("Pipes", null, 0);
        var steel = await _categoryRepository.AddAsync("Steel", parent.Id, 0);
        var copper = await _categoryRepository.AddAsync("Copper", parent.Id, 1);

        var vm = CreateVm();
        await vm.InitializeAsync();
        var parentVm = vm.RootNodes[0];
        var copperVm = (CategoryNodeViewModel)parentVm.Children[1];

        await vm.MoveNodeCommand.ExecuteAsync((copperVm, (CategoryNodeViewModel?)null, 1));

        var all = await _categoryRepository.GetAllAsync();
        Assert.Null(all.First(c => c.Name == "Copper").ParentId);
        Assert.Equal(parent.Id, all.First(c => c.Name == "Steel").ParentId);
        var movedVm = vm.RootNodes.FirstOrDefault(n => n.CategoryId == copper.Id);
        Assert.NotNull(movedVm);
        Assert.True(movedVm!.IsSelected);
    }

    [Fact]
    public async Task HandleBindingToggle_Bind_CreatesBindingImmediately_AndActivatesShield()
    {
        var cat = await _categoryRepository.AddAsync("HVAC", null, 0);
        var attr = await _attributeRepository.CreateAsync("Width", null);

        var vm = CreateVm();
        await vm.InitializeAsync();
        vm.SelectedNode = vm.RootNodes[0];

        var item = new AttributeListItemViewModel
        {
            AttributeId = attr.Id,
            Name = attr.Name,
            Parent = vm,
        };

        await vm.HandleBindingToggleAsync(item, true);

        var bindings = await _bindingService.GetDirectBindingsAsync(cat.Id);
        var binding = Assert.Single(bindings);
        Assert.True(item.IsBound);
        Assert.Equal(binding.Id, item.BindingId);
        Assert.True(item.CanEditRules);

        await vm.HandleBindingToggleAsync(item, false);

        Assert.Empty(await _bindingService.GetDirectBindingsAsync(cat.Id));
        Assert.False(item.IsBound);
        Assert.Null(item.BindingId);
        Assert.False(item.CanEditRules);
    }

    [Fact]
    public async Task HandleBindingToggle_UnbindWithRules_UserDeclines_KeepsBinding()
    {
        var cat = await _categoryRepository.AddAsync("HVAC", null, 0);
        var attr = await _attributeRepository.CreateAsync("Width", null);
        var binding = await _bindingService.CreateBindingAsync(cat.Id, attr.Id, 0);
        var ruleRepo = new LocalValidationRuleRepository(_fixture.GetDatabase(), _fixture.GetMigrator());
        await ruleRepo.CreateRuleAsync(new SmartCon.Core.Models.FamilyManager.ValidationRule(
            string.Empty, binding.Id, SmartCon.Core.Models.FamilyManager.ValidationRuleOperator.HasValue,
            null, null, null, null, null, 0, true));

        var vm = CreateVm();
        await vm.InitializeAsync();
        vm.SelectedNode = vm.RootNodes[0];

        var item = new AttributeListItemViewModel
        {
            AttributeId = attr.Id,
            Name = attr.Name,
            BindingId = binding.Id,
            IsBound = true,
            RuleCount = 1,
            Parent = vm,
        };

        _dialogMock.Setup(s => s.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>())).Returns(false);

        await vm.HandleBindingToggleAsync(item, false);

        Assert.Single(await _bindingService.GetDirectBindingsAsync(cat.Id));
        Assert.Single(await ruleRepo.GetRulesForBindingAsync(binding.Id));
        Assert.True(item.IsBound);
    }

    [Fact]
    public async Task HandleBindingToggle_InheritedBinding_NeverTouched()
    {
        var cat = await _categoryRepository.AddAsync("HVAC", null, 0);
        var attr = await _attributeRepository.CreateAsync("Width", null);
        var binding = await _bindingService.CreateBindingAsync(cat.Id, attr.Id, 0);

        var vm = CreateVm();
        await vm.InitializeAsync();
        vm.SelectedNode = vm.RootNodes[0];

        var item = new AttributeListItemViewModel
        {
            AttributeId = attr.Id,
            Name = attr.Name,
            BindingId = binding.Id,
            IsBound = true,
            IsInherited = true,
            Parent = vm,
        };

        await vm.HandleBindingToggleAsync(item, false);

        Assert.Single(await _bindingService.GetDirectBindingsAsync(cat.Id));
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
