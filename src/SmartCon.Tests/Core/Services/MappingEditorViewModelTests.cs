using Moq;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;
using SmartCon.PipeConnect.ViewModels;
using Xunit;

namespace SmartCon.Tests.Core.Services;

public sealed class MappingEditorViewModelTests
{
    private static IReadOnlyList<ConnectorTypeDefinition> SomeTypes() =>
    [
        new() { Code = 1, Name = "Сварка",  Description = "Сварное" },
        new() { Code = 2, Name = "Резьба",  Description = "Резьбовое" },
    ];

    private static IReadOnlyList<FittingMappingRule> SomeRules() =>
    [
        new() { FromType = new ConnectionTypeCode(1), ToType = new ConnectionTypeCode(2) },
    ];

    private static MappingEditorViewModel MakeVm(
        IReadOnlyList<ConnectorTypeDefinition>? types = null,
        IReadOnlyList<FittingMappingRule>? rules = null,
        IReadOnlyList<string>? families = null,
        IDialogService? dialogService = null)
    {
        var repoMock = new Mock<IFittingMappingRepository>();
        repoMock.Setup(r => r.GetConnectorTypes()).Returns(types ?? []);
        repoMock.Setup(r => r.GetMappingRules()).Returns(rules ?? []);
        var dialogMock = new Mock<IDialogService>();
        return new MappingEditorViewModel(repoMock.Object, dialogService ?? dialogMock.Object, families ?? []);
    }

    // ── Constructor ───────────────────────────────────────────────────────

    [Fact]
    public void Constructor_LoadsTypesFromRepository()
    {
        var vm = MakeVm(types: SomeTypes());
        Assert.Equal(2, vm.ConnectorTypes.Count);
        Assert.Equal(1, vm.ConnectorTypes[0].Code);
        Assert.Equal("Сварка", vm.ConnectorTypes[0].Name);
    }

    [Fact]
    public void Constructor_LoadsRulesFromRepository()
    {
        var vm = MakeVm(rules: SomeRules());
        Assert.Single(vm.MappingRules);
        Assert.Equal(1, vm.MappingRules[0].FromTypeCode);
        Assert.Equal(2, vm.MappingRules[0].ToTypeCode);
    }

    [Fact]
    public void Constructor_EmptyRepository_CollectionsEmpty()
    {
        var vm = MakeVm();
        Assert.Empty(vm.ConnectorTypes);
        Assert.Empty(vm.MappingRules);
    }

    [Fact]
    public void Constructor_SetsAvailableFamilyNames()
    {
        var vm = MakeVm(families: ["FamilyA", "FamilyB"]);
        Assert.Equal(2, vm.AvailableFamilyNames.Count);
        Assert.Equal("FamilyA", vm.AvailableFamilyNames[0]);
    }

    // ── AddType ───────────────────────────────────────────────────────────

    [Fact]
    public void AddTypeCommand_AddsItemToCollection()
    {
        var vm = MakeVm();
        vm.AddTypeCommand.Execute(null);
        Assert.Single(vm.ConnectorTypes);
    }

    [Fact]
    public void AddTypeCommand_FirstType_CodeIsOne()
    {
        var vm = MakeVm();
        vm.AddTypeCommand.Execute(null);
        Assert.Equal(1, vm.ConnectorTypes[0].Code);
    }

    [Fact]
    public void AddTypeCommand_SecondType_CodeIsMaxPlusOne()
    {
        var vm = MakeVm(types: SomeTypes());
        vm.AddTypeCommand.Execute(null);
        Assert.Equal(3, vm.ConnectorTypes.Last().Code);
    }

    [Fact]
    public void AddTypeCommand_SetsSelectedType()
    {
        var vm = MakeVm();
        vm.AddTypeCommand.Execute(null);
        Assert.NotNull(vm.SelectedType);
        Assert.Same(vm.ConnectorTypes[0], vm.SelectedType);
    }

    // ── DeleteType ────────────────────────────────────────────────────────

    [Fact]
    public void DeleteTypeCommand_RemovesSelectedItem()
    {
        var vm = MakeVm(types: SomeTypes());
        vm.SelectedType = vm.ConnectorTypes[0];
        vm.DeleteTypeCommand.Execute(null);
        Assert.Single(vm.ConnectorTypes);
        Assert.Equal(2, vm.ConnectorTypes[0].Code);
    }

    [Fact]
    public void DeleteTypeCommand_NothingSelected_DoesNotThrow()
    {
        var vm = MakeVm(types: SomeTypes());
        vm.SelectedType = null;
        var ex = Record.Exception(() => vm.DeleteTypeCommand.Execute(null));
        Assert.Null(ex);
        Assert.Equal(2, vm.ConnectorTypes.Count);
    }

    // ── SaveTypes ─────────────────────────────────────────────────────────

    [Fact]
    public void SaveTypesCommand_CallsRepositorySave()
    {
        var repoMock = new Mock<IFittingMappingRepository>();
        repoMock.Setup(r => r.GetConnectorTypes()).Returns(SomeTypes());
        repoMock.Setup(r => r.GetMappingRules()).Returns([]);
        var dialogMock = new Mock<IDialogService>();

        var vm = new MappingEditorViewModel(repoMock.Object, dialogMock.Object, []);
        vm.SaveTypesCommand.Execute(null);

        repoMock.Verify(r => r.SaveConnectorTypes(It.Is<IReadOnlyList<ConnectorTypeDefinition>>(
            list => list.Count == 2 && list[0].Code == 1)), Times.Once);
    }

    [Fact]
    public void SaveTypesCommand_EmptyList_CallsRepositoryWithEmpty()
    {
        var repoMock = new Mock<IFittingMappingRepository>();
        repoMock.Setup(r => r.GetConnectorTypes()).Returns([]);
        repoMock.Setup(r => r.GetMappingRules()).Returns([]);
        var dialogMock = new Mock<IDialogService>();

        var vm = new MappingEditorViewModel(repoMock.Object, dialogMock.Object, []);
        vm.SaveTypesCommand.Execute(null);

        repoMock.Verify(r => r.SaveConnectorTypes(It.Is<IReadOnlyList<ConnectorTypeDefinition>>(
            list => list.Count == 0)), Times.Once);
    }

    // ── AddRule ───────────────────────────────────────────────────────────

    [Fact]
    public void AddRuleCommand_AddsItemToCollection()
    {
        var vm = MakeVm();
        vm.AddRuleCommand.Execute(null);
        Assert.Single(vm.MappingRules);
    }

    [Fact]
    public void AddRuleCommand_SetsSelectedRule()
    {
        var vm = MakeVm();
        vm.AddRuleCommand.Execute(null);
        Assert.NotNull(vm.SelectedRule);
    }

    // ── DeleteRule ────────────────────────────────────────────────────────

    [Fact]
    public void DeleteRuleCommand_RemovesSelectedRule()
    {
        var vm = MakeVm(rules: SomeRules());
        vm.SelectedRule = vm.MappingRules[0];
        vm.DeleteRuleCommand.Execute(null);
        Assert.Empty(vm.MappingRules);
    }

    // ── SaveRules ─────────────────────────────────────────────────────────

    [Fact]
    public void SaveRulesCommand_CallsRepositorySave()
    {
        var repoMock = new Mock<IFittingMappingRepository>();
        repoMock.Setup(r => r.GetConnectorTypes()).Returns([]);
        repoMock.Setup(r => r.GetMappingRules()).Returns(SomeRules());
        var dialogMock = new Mock<IDialogService>();

        var vm = new MappingEditorViewModel(repoMock.Object, dialogMock.Object, []);
        vm.SaveRulesCommand.Execute(null);

        repoMock.Verify(r => r.SaveMappingRules(It.Is<IReadOnlyList<FittingMappingRule>>(
            list => list.Count == 1 && list[0].FromType.Value == 1)), Times.Once);
    }
}
