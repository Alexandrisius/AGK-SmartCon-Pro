using System.IO;
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
    private readonly Mock<IFamilyManagerViewModelFactory> _factoryMock;
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
        _factoryMock = new Mock<IFamilyManagerViewModelFactory>();
        _mediator = new FamilyManagerMetadataMediator();
    }

    public void Dispose() => _fixture.Dispose();

    private AttributeLibraryViewModel CreateVm() =>
        new(_attributeRepository, _bindingService, _dialogMock.Object, _categoryRepository, _mediator, _factoryMock.Object);

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

    [Fact]
    public async Task ImportFromSharedParameters_AddsSelectedEntriesAsNewDraftsWithoutGroup()
    {
        var entries = new List<SmartCon.Core.Models.FamilyManager.SharedParameterEntry>
        {
            new(Guid.NewGuid(), "ADSK_Масса", "NUMBER", null, "01 Общие", null),
            new(Guid.NewGuid(), "ADSK_Длина", "LENGTH", null, null, null),
        };
        SetupPicker(entries, vm =>
        {
            vm.Items.First(i => i.Name == "ADSK_Масса").IsSelected = true;
            return true;
        });

        var vm = CreateVm();
        await vm.InitializeAsync();

        vm.ImportFromSharedParametersCommand.Execute(null);

        var draft = Assert.Single(vm.Items);
        Assert.Equal("ADSK_Масса", draft.Name);
        Assert.True(draft.IsNew);
        Assert.Null(draft.Group);
        Assert.True(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task ImportFromSharedParameters_Cancelled_AddsNothing()
    {
        var entries = new List<SmartCon.Core.Models.FamilyManager.SharedParameterEntry>
        {
            new(Guid.NewGuid(), "ADSK_Масса", "NUMBER", null, null, null),
        };
        SetupPicker(entries, _ => false);

        var vm = CreateVm();
        await vm.InitializeAsync();

        vm.ImportFromSharedParametersCommand.Execute(null);

        Assert.Empty(vm.Items);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task ImportFromSharedParameters_SkipsNamesAlreadyInList()
    {
        var entries = new List<SmartCon.Core.Models.FamilyManager.SharedParameterEntry>
        {
            new(Guid.NewGuid(), "Width", "LENGTH", null, null, null),
        };
        SetupPicker(entries, vm =>
        {
            foreach (var item in vm.Items)
                item.IsSelected = true;
            return true;
        });

        var vm = CreateVm();
        await vm.InitializeAsync();
        vm.AddAttributeCommand.Execute(null);
        vm.Items[0].Name = "Width";

        vm.ImportFromSharedParametersCommand.Execute(null);

        Assert.Single(vm.Items);
    }

    [Fact]
    public async Task AvailableGroups_PopulatedFromItems()
    {
        await _attributeRepository.CreateAsync("Width", "Dimensions");
        await _attributeRepository.CreateAsync("Height", "Dimensions");
        await _attributeRepository.CreateAsync("Material", "Props");
        await _attributeRepository.CreateAsync("Note", null);

        var vm = CreateVm();
        await vm.InitializeAsync();

        Assert.Equal(2, vm.AvailableGroups.Count);
        Assert.Contains("Dimensions", vm.AvailableGroups);
        Assert.Contains("Props", vm.AvailableGroups);
    }

    [Fact]
    public async Task AvailableGroups_UpdatesWhenDraftGroupChanges()
    {
        var vm = CreateVm();
        await vm.InitializeAsync();
        Assert.Empty(vm.AvailableGroups);

        vm.AddAttributeCommand.Execute(null);
        vm.Items[0].Name = "NewAttr";
        vm.Items[0].Group = "NewGroup";

        Assert.Single(vm.AvailableGroups);
        Assert.Equal("NewGroup", vm.AvailableGroups[0]);
    }

    private void SetupPicker(
        IReadOnlyList<SmartCon.Core.Models.FamilyManager.SharedParameterEntry> entries,
        Func<SharedParameterPickerViewModel, bool> userAction)
    {
        var parserMock = new Mock<ISharedParameterFileParser>();
        parserMock.Setup(p => p.ParseFile(It.IsAny<string>())).Returns(entries);
        var settingsMock = new Mock<IFamilyManagerUserSettingsRepository>();
        settingsMock.Setup(s => s.Load()).Returns(SmartCon.Core.Models.FamilyManager.FamilyManagerUserSettings.Default);

        _factoryMock
            .Setup(f => f.CreateSharedParameterPickerViewModel(It.IsAny<IEnumerable<string>>()))
            .Returns<IEnumerable<string>>(names =>
                new SharedParameterPickerViewModel(parserMock.Object, settingsMock.Object, _dialogMock.Object, names));

        _dialogMock
            .Setup(d => d.ShowSharedParameterPicker(It.IsAny<object>()))
            .Returns<object>(vmObj =>
            {
                var pickerVm = (SharedParameterPickerViewModel)vmObj;
                var tmpFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
                File.WriteAllText(tmpFile, "dummy");
                try
                {
                    _dialogMock
                        .Setup(d => d.ShowOpenTextFileDialog(It.IsAny<string>(), It.IsAny<string?>()))
                        .Returns(tmpFile);
                    pickerVm.BrowseCommand.Execute(null);
                    return userAction(pickerVm);
                }
                finally
                {
                    File.Delete(tmpFile);
                }
            });
    }
}
