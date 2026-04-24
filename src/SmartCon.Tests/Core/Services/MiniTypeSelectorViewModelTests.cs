using SmartCon.Core.Models;
using SmartCon.PipeConnect.ViewModels;
using Xunit;

namespace SmartCon.Tests.Core.Services;

public sealed class MiniTypeSelectorViewModelTests
{
    private static IReadOnlyList<ConnectorTypeDefinition> MakeTypes() =>
    [
        new() { Code = 1, Name = "Сварка" },
        new() { Code = 2, Name = "Резьба" },
        new() { Code = 3, Name = "Раструб" },
    ];

    [Fact]
    public void Constructor_SetsAvailableTypes()
    {
        var vm = new MiniTypeSelectorViewModel(MakeTypes());
        Assert.Equal(3, vm.AvailableTypes.Count);
    }

    [Fact]
    public void SelectedType_InitiallyNull()
    {
        var vm = new MiniTypeSelectorViewModel(MakeTypes());
        Assert.Null(vm.SelectedType);
    }

    [Fact]
    public void SelectCommand_RaisesRequestClose()
    {
        var vm = new MiniTypeSelectorViewModel(MakeTypes());
        vm.SelectedType = vm.AvailableTypes[0];

        var closed = false;
        vm.RequestClose += _ => closed = true;

        vm.SelectCommand.Execute(null);

        Assert.True(closed);
    }

    [Fact]
    public void SelectCommand_KeepsSelectedType()
    {
        var vm = new MiniTypeSelectorViewModel(MakeTypes());
        vm.SelectedType = vm.AvailableTypes[1];

        vm.SelectCommand.Execute(null);

        Assert.NotNull(vm.SelectedType);
        Assert.Equal(2, vm.SelectedType.Code);
    }

    [Fact]
    public void CancelCommand_ClearsSelectedType()
    {
        var vm = new MiniTypeSelectorViewModel(MakeTypes());
        vm.SelectedType = vm.AvailableTypes[0];

        vm.CancelCommand.Execute(null);

        Assert.Null(vm.SelectedType);
    }

    [Fact]
    public void CancelCommand_RaisesRequestClose()
    {
        var vm = new MiniTypeSelectorViewModel(MakeTypes());
        var closed = false;
        vm.RequestClose += _ => closed = true;

        vm.CancelCommand.Execute(null);

        Assert.True(closed);
    }

    [Fact]
    public void SelectedType_PropertyChangeNotified()
    {
        var vm = new MiniTypeSelectorViewModel(MakeTypes());
        var changed = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.SelectedType)) changed = true;
        };

        vm.SelectedType = vm.AvailableTypes[0];

        Assert.True(changed);
    }

    [Fact]
    public void AvailableTypes_EmptyList_NoCrash()
    {
        var vm = new MiniTypeSelectorViewModel([]);
        Assert.Empty(vm.AvailableTypes);
        vm.SelectCommand.Execute(null);
        vm.CancelCommand.Execute(null);
    }
}
