using Moq;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using SmartCon.FamilyManager.Services.LocalCatalog;
using SmartCon.FamilyManager.ViewModels;
using SmartCon.Tests.FamilyManager.Repository;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

public sealed class AttributeLibraryViewModelTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly LocalAttributeDefinitionRepository _attributeRepository;
    private readonly LocalCategoryAttributeBindingService _bindingService;
    private readonly LocalCategoryRepository _categoryRepository;
    private readonly Mock<IFamilyManagerDialogService> _dialogMock;
    private readonly FamilyManagerMetadataMediator _mediator;

    public AttributeLibraryViewModelTests()
    {
        _fixture = new TempCatalogFixture();
        _fixture.MigrateAsync().GetAwaiter().GetResult();

        _attributeRepository = new LocalAttributeDefinitionRepository(_fixture.GetDatabase());
        _categoryRepository = new LocalCategoryRepository(_fixture.GetDatabase());
        _bindingService = new LocalCategoryAttributeBindingService(
            _fixture.GetDatabase(), _categoryRepository, _fixture.GetMigrator());
        _dialogMock = new Mock<IFamilyManagerDialogService>();
        _mediator = new FamilyManagerMetadataMediator();
    }

    public void Dispose() => _fixture.Dispose();

    private AttributeLibraryViewModel CreateVm() =>
        new(_attributeRepository, _bindingService, _dialogMock.Object, _categoryRepository, _mediator);

    [Fact]
    public async Task InitializeAsync_EmptyDb_LoadsZeroItems()
    {
        var vm = CreateVm();

        await vm.InitializeAsync();

        Assert.Empty(vm.Items);
    }

    [Fact]
    public async Task InitializeAsync_WithExistingAttributes_LoadsAllItems()
    {
        await _attributeRepository.CreateAsync("Width", "Dimensions");
        await _attributeRepository.CreateAsync("Height", "Dimensions");
        await _attributeRepository.CreateAsync("Material", null);

        var vm = CreateVm();
        await vm.InitializeAsync();

        Assert.Equal(3, vm.Items.Count);
        Assert.Contains(vm.Items, i => i.Name == "Width" && i.Group == "Dimensions");
        Assert.Contains(vm.Items, i => i.Name == "Height" && i.Group == "Dimensions");
        Assert.Contains(vm.Items, i => i.Name == "Material" && i.Group == null);
    }

    [Fact]
    public async Task InitializeAsync_SubscribesToMediator()
    {
        var vm = CreateVm();
        await vm.InitializeAsync();

        await _attributeRepository.CreateAsync("Width", null);
        _mediator.RaiseMetadataChanged();

        for (int i = 0; i < 10 && vm.Items.Count == 0; i++)
        {
            await Task.Delay(20);
        }

        Assert.Single(vm.Items);
    }

    [Fact]
    public async Task RefreshAsync_NoUnsavedChanges_ReloadsFromDb()
    {
        var vm = CreateVm();
        await vm.InitializeAsync();
        Assert.Empty(vm.Items);

        await _attributeRepository.CreateAsync("NewAttr", "Group1");

        await vm.RefreshAsync();

        Assert.Single(vm.Items);
        Assert.Equal("NewAttr", vm.Items[0].Name);
    }

    [Fact]
    public async Task RefreshAsync_WithUnsavedChanges_DoesNotOverwrite()
    {
        var vm = CreateVm();
        await vm.InitializeAsync();

        vm.AddAttributeCommand.Execute(null);
        var addedItem = vm.Items.First();
        addedItem.Name = "Pending";

        Assert.True(vm.HasUnsavedChanges);

        await _attributeRepository.CreateAsync("Background", null);
        await vm.RefreshAsync();

        Assert.Single(vm.Items);
        Assert.Equal("Pending", vm.Items[0].Name);
    }

    [Fact]
    public async Task Detach_UnsubscribesFromMediator()
    {
        var vm = CreateVm();
        await vm.InitializeAsync();
        var itemsBefore = vm.Items.Count;

        vm.Detach();
        await _attributeRepository.CreateAsync("AfterDetach", null);
        _mediator.RaiseMetadataChanged();

        await Task.Delay(50);

        Assert.Equal(itemsBefore, vm.Items.Count);
    }

    [Fact]
    public async Task Detach_CalledTwice_DoesNotThrow()
    {
        var vm = CreateVm();
        await vm.InitializeAsync();

        vm.Detach();
        vm.Detach();
    }
}
