using System.Windows.Threading;
using Moq;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using SmartCon.FamilyManager.Services.LocalCatalog;
using SmartCon.FamilyManager.ViewModels;
using SmartCon.Tests.FamilyManager.Repository;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

public sealed class CategoryTreeEditorGroupFilterTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly LocalCategoryRepository _categoryRepository;
    private readonly LocalAttributeDefinitionRepository _attributeRepository;
    private readonly LocalCategoryAttributeBindingService _bindingService;
    private readonly Mock<IFamilyManagerDialogService> _dialogMock;
    private readonly Mock<IFamilyManagerViewModelFactory> _factoryMock;
    private readonly FamilyManagerMetadataMediator _mediator;

    public CategoryTreeEditorGroupFilterTests()
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

    private static async Task WaitForAttributesLoaded(CategoryTreeEditorViewModel vm)
    {
        // OnSelectedNodeChanged queues LoadAttributesForCategoryAsync through
        // Dispatcher.InvokeAsync — in tests nobody pumps the dispatcher queue,
        // so push frames manually until the async load completes.
        var dispatcher = Dispatcher.CurrentDispatcher;
        for (var i = 0; i < 200 && vm.AttributeItems.Count == 0; i++)
        {
            var frame = new DispatcherFrame();
            _ = dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task CategoryWithGroupedAttributes_AvailableGroupsContainsAllLabelAndGroup()
    {
        var category = await _categoryRepository.AddAsync("Pipes", null, 0);
        await _attributeRepository.CreateAsync("Width", "Dimensions");
        await _attributeRepository.CreateAsync("Note", null);

        var vm = CreateVm();
        await vm.InitializeAsync();
        var node = FindNode(vm, category.Id);
        Assert.NotNull(node);

        vm.SelectedNode = node;
        await WaitForAttributesLoaded(vm);

        Assert.Equal(2, vm.AvailableGroups.Count);
        Assert.Contains("Dimensions", vm.AvailableGroups);
    }

    [Fact]
    public async Task CategoryWithOnlyUngroupedAttributes_AvailableGroupsContainsOnlyAllLabel()
    {
        var category = await _categoryRepository.AddAsync("Pipes", null, 0);
        await _attributeRepository.CreateAsync("Note", null);

        var vm = CreateVm();
        await vm.InitializeAsync();
        var node = FindNode(vm, category.Id);
        Assert.NotNull(node);

        vm.SelectedNode = node;
        await WaitForAttributesLoaded(vm);

        Assert.Single(vm.AvailableGroups);
        Assert.Equal(vm.SelectedGroupFilter, vm.AvailableGroups[0]);
    }

    [Fact]
    public void NoCategorySelected_AvailableGroupsEmpty()
    {
        var vm = CreateVm();

        Assert.Empty(vm.AvailableGroups);
    }

    private static CategoryNodeViewModel? FindNode(CategoryTreeEditorViewModel vm, string categoryId)
    {
        var stack = new Stack<CatalogTreeNodeViewModel>(vm.RootNodes);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is CategoryNodeViewModel categoryNode && categoryNode.CategoryId == categoryId)
                return categoryNode;
            foreach (var child in node.Children)
                stack.Push(child);
        }

        return null;
    }
}
