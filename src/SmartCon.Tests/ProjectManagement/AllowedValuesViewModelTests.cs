using SmartCon.Core.Models;
using SmartCon.ProjectManagement.ViewModels;
using Xunit;

namespace SmartCon.Tests.ProjectManagement;

public sealed class AllowedValuesViewModelTests
{
    private static FieldDefinitionItem CreateItem(
        ValidationMode mode = ValidationMode.None,
        List<string>? allowedValues = null,
        int? minLength = null,
        int? maxLength = null)
    {
        var item = new FieldDefinitionItem
        {
            Name = "status",
            ValidationMode = mode,
            AllowedValues = allowedValues ?? [],
            MinLength = minLength,
            MaxLength = maxLength
        };
        item.UpdateValidationModeDisplay();
        return item;
    }

    [Fact]
    public void Constructor_LoadsValuesFromFieldItem()
    {
        var item = CreateItem(
            ValidationMode.AllowedValues,
            allowedValues: ["S0", "S1", "S2"]);

        var vm = new AllowedValuesViewModel(item);

        Assert.Equal(3, vm.Values.Count);
        Assert.Equal("S0", vm.Values[0]);
        Assert.Equal("S1", vm.Values[1]);
        Assert.Equal("S2", vm.Values[2]);
    }

    [Fact]
    public void Constructor_SetsValidationMode()
    {
        var item = CreateItem(ValidationMode.CharCount, minLength: 2, maxLength: 5);

        var vm = new AllowedValuesViewModel(item);

        Assert.Equal(ValidationMode.CharCount, vm.ValidationMode);
        Assert.Equal(2, vm.MinLength);
        Assert.Equal(5, vm.MaxLength);
    }

    [Fact]
    public void AddValueCommand_AddsTrimmedValue()
    {
        var item = CreateItem();
        var vm = new AllowedValuesViewModel(item);

        vm.NewValue = "  S0  ";
        vm.AddValueCommand.Execute(null);

        Assert.Single(vm.Values);
        Assert.Equal("S0", vm.Values[0]);
        Assert.Equal(string.Empty, vm.NewValue);
    }

    [Fact]
    public void AddValueCommand_IgnoresWhitespace()
    {
        var item = CreateItem();
        var vm = new AllowedValuesViewModel(item);

        vm.NewValue = "   ";
        vm.AddValueCommand.Execute(null);

        Assert.Empty(vm.Values);
    }

    [Fact]
    public void RemoveValueCommand_RemovesSelectedValue()
    {
        var item = CreateItem(ValidationMode.AllowedValues, allowedValues: ["S0", "S1", "S2"]);
        var vm = new AllowedValuesViewModel(item);

        vm.SelectedIndex = 1;
        vm.RemoveValueCommand.Execute(null);

        Assert.Equal(2, vm.Values.Count);
        Assert.Equal("S0", vm.Values[0]);
        Assert.Equal("S2", vm.Values[1]);
    }

    [Fact]
    public void RemoveValueCommand_NegativeIndex_DoesNothing()
    {
        var item = CreateItem(ValidationMode.AllowedValues, allowedValues: ["S0"]);
        var vm = new AllowedValuesViewModel(item);

        vm.SelectedIndex = -1;
        vm.RemoveValueCommand.Execute(null);

        Assert.Single(vm.Values);
    }

    [Fact]
    public void RemoveValueCommand_OutOfRange_DoesNothing()
    {
        var item = CreateItem(ValidationMode.AllowedValues, allowedValues: ["S0"]);
        var vm = new AllowedValuesViewModel(item);

        vm.SelectedIndex = 5;
        vm.RemoveValueCommand.Execute(null);

        Assert.Single(vm.Values);
    }

    [Fact]
    public void ShowValuesList_TrueForAllowedValues()
    {
        var item = CreateItem(ValidationMode.AllowedValues);
        var vm = new AllowedValuesViewModel(item);

        Assert.True(vm.ShowValuesList);
        Assert.False(vm.ShowLengthFields);
    }

    [Fact]
    public void ShowLengthFields_TrueForCharCount()
    {
        var item = CreateItem(ValidationMode.CharCount);
        var vm = new AllowedValuesViewModel(item);

        Assert.False(vm.ShowValuesList);
        Assert.True(vm.ShowLengthFields);
    }

    [Fact]
    public void ValidationModeChanged_UpdatesFlags()
    {
        var item = CreateItem(ValidationMode.None);
        var vm = new AllowedValuesViewModel(item);

        vm.ValidationMode = ValidationMode.AllowedValues;

        Assert.True(vm.ShowValuesList);
        Assert.False(vm.ShowLengthFields);
    }

    [Fact]
    public void ApplyTo_CopiesAllProperties()
    {
        var source = CreateItem(ValidationMode.AllowedValues, allowedValues: ["A", "B"]);
        var vm = new AllowedValuesViewModel(source);
        vm.MinLength = 1;
        vm.MaxLength = 3;

        var target = new FieldDefinitionItem();
        vm.ApplyTo(target);

        Assert.Equal(ValidationMode.AllowedValues, target.ValidationMode);
        Assert.Equal(1, target.MinLength);
        Assert.Equal(3, target.MaxLength);
        Assert.Equal(["A", "B"], target.AllowedValues);
    }

    [Fact]
    public void SaveCommand_InvokesRequestCloseWithTrue()
    {
        var item = CreateItem();
        var vm = new AllowedValuesViewModel(item);
        bool? result = null;
        vm.RequestClose += r => result = r;

        vm.SaveCommand.Execute(null);

        Assert.True(result);
    }

    [Fact]
    public void CancelCommand_InvokesRequestCloseWithFalse()
    {
        var item = CreateItem();
        var vm = new AllowedValuesViewModel(item);
        bool? result = null;
        vm.RequestClose += r => result = r;

        vm.CancelCommand.Execute(null);

        Assert.False(result);
    }
}
