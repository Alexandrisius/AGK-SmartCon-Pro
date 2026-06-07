using Moq;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using SmartCon.FamilyManager.Services.LocalCatalog;
using SmartCon.FamilyManager.ViewModels;
using SmartCon.Tests.FamilyManager.Repository;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

public sealed class CategoryTreeEditorDeleteTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly LocalCategoryRepository _categoryRepository;
    private readonly LocalAttributeDefinitionRepository _attributeRepository;
    private readonly LocalCategoryAttributeBindingService _bindingService;
    private readonly Mock<IFamilyManagerDialogService> _dialogMock;
    private readonly Mock<IFamilyManagerViewModelFactory> _factoryMock;
    private readonly FamilyManagerMetadataMediator _mediator;

    public CategoryTreeEditorDeleteTests()
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
        new(_categoryRepository, _dialogMock.Object, _attributeRepository, _bindingService, _mediator, _factoryMock.Object);

    private static CategoryNodeViewModel MakeNode(string id, string name, CategoryNodeViewModel? parent = null)
    {
        var fullPath = parent is null ? name : $"{parent.FullPath}/{name}";
        return new CategoryNodeViewModel(id, name, parent?.CategoryId, fullPath) { SortOrder = 0, OriginalSortOrder = 0 };
    }

    [Fact]
    public async Task Delete_AfterFirstDelete_SelectsNextSibling_NotNull()
    {
        var vm = CreateVm();
        var a = MakeNode("a", "A");
        var b = MakeNode("b", "B");
        var c = MakeNode("c", "C");
        vm.RootNodes.Add(a);
        vm.RootNodes.Add(b);
        vm.RootNodes.Add(c);

        vm.SelectedNode = a;
        _dialogMock.Setup(s => s.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        await Task.Yield();
        vm.DeleteCommand.Execute(null);

        Assert.Equal(2, vm.RootNodes.Count);
        Assert.DoesNotContain(vm.RootNodes, n => n.CategoryId == "a");
        Assert.NotNull(vm.SelectedNode);
        Assert.Equal("b", vm.SelectedNode!.CategoryId);
    }

    [Fact]
    public async Task Delete_MiddleRoot_SelectsNextSibling()
    {
        var vm = CreateVm();
        var a = MakeNode("a", "A");
        var b = MakeNode("b", "B");
        var c = MakeNode("c", "C");
        vm.RootNodes.Add(a);
        vm.RootNodes.Add(b);
        vm.RootNodes.Add(c);

        vm.SelectedNode = b;
        _dialogMock.Setup(s => s.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        await Task.Yield();
        vm.DeleteCommand.Execute(null);

        Assert.Equal(2, vm.RootNodes.Count);
        Assert.NotNull(vm.SelectedNode);
        Assert.Equal("c", vm.SelectedNode!.CategoryId);
    }

    [Fact]
    public async Task Delete_LastRoot_SelectsPreviousSibling()
    {
        var vm = CreateVm();
        var a = MakeNode("a", "A");
        var b = MakeNode("b", "B");
        vm.RootNodes.Add(a);
        vm.RootNodes.Add(b);

        vm.SelectedNode = b;
        _dialogMock.Setup(s => s.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        await Task.Yield();
        vm.DeleteCommand.Execute(null);

        Assert.Single(vm.RootNodes);
        Assert.NotNull(vm.SelectedNode);
        Assert.Equal("a", vm.SelectedNode!.CategoryId);
    }

    [Fact]
    public async Task Delete_OnlyRoot_SelectsNull()
    {
        var vm = CreateVm();
        var a = MakeNode("a", "A");
        vm.RootNodes.Add(a);

        vm.SelectedNode = a;
        _dialogMock.Setup(s => s.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        await Task.Yield();
        vm.DeleteCommand.Execute(null);

        Assert.Empty(vm.RootNodes);
        Assert.Null(vm.SelectedNode);
    }

    [Fact]
    public async Task Delete_ChildWithSiblings_SelectsNextChild()
    {
        var vm = CreateVm();
        var root = MakeNode("root", "Root");
        var childA = MakeNode("ca", "ChildA", root);
        var childB = MakeNode("cb", "ChildB", root);
        var childC = MakeNode("cc", "ChildC", root);
        root.Children.Add(childA);
        root.Children.Add(childB);
        root.Children.Add(childC);
        vm.RootNodes.Add(root);

        vm.SelectedNode = childA;
        _dialogMock.Setup(s => s.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        await Task.Yield();
        vm.DeleteCommand.Execute(null);

        Assert.Equal(2, root.Children.Count);
        Assert.NotNull(vm.SelectedNode);
        Assert.Equal("cb", vm.SelectedNode!.CategoryId);
    }

    [Fact]
    public async Task Delete_UserSaysNoInConfirmation_DoesNotDelete()
    {
        var vm = CreateVm();
        var a = MakeNode("a", "A");
        var b = MakeNode("b", "B");
        vm.RootNodes.Add(a);
        vm.RootNodes.Add(b);

        vm.SelectedNode = a;
        _dialogMock.Setup(s => s.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>())).Returns(false);

        await Task.Yield();
        vm.DeleteCommand.Execute(null);

        Assert.Equal(2, vm.RootNodes.Count);
        Assert.Equal("a", vm.SelectedNode!.CategoryId);
    }

    [Fact]
    public async Task ContextMenuDelete_AfterFirstDelete_CanDeleteAgain()
    {
        var vm = CreateVm();
        var a = MakeNode("a", "A");
        var b = MakeNode("b", "B");
        vm.RootNodes.Add(a);
        vm.RootNodes.Add(b);

        vm.SelectedNode = a;
        _dialogMock.Setup(s => s.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        await Task.Yield();
        vm.DeleteCommand.Execute(null);
        Assert.Equal("b", vm.SelectedNode!.CategoryId);

        vm.ContextMenuDeleteCommand.Execute(null);

        Assert.Empty(vm.RootNodes);
        Assert.Null(vm.SelectedNode);
    }

    [Fact]
    public async Task Delete_AfterFirstDelete_RemovedNodeIsMarkedDeleted()
    {
        var vm = CreateVm();
        var a = MakeNode("a", "A");
        var b = MakeNode("b", "B");
        vm.RootNodes.Add(a);
        vm.RootNodes.Add(b);

        vm.SelectedNode = a;
        _dialogMock.Setup(s => s.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        await Task.Yield();
        vm.DeleteCommand.Execute(null);

        Assert.True(a.IsDeleted);
    }
}
