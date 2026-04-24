using SmartCon.Core.Models;
using SmartCon.PipeConnect.ViewModels;
using Xunit;

namespace SmartCon.Tests.Core.Services;

public sealed class FamilySelectorViewModelTests
{
    private static IReadOnlyList<string> SomeFamilies() =>
        ["Муфта DN50", "Переходник DN50-40", "Угольник DN50"];

    private static FamilySelectorViewModel MakeVm(
        IReadOnlyList<string>? available = null,
        IReadOnlyList<FittingMapping>? current = null)
        => new(available ?? SomeFamilies(), current ?? []);

    // ── Constructor ───────────────────────────────────────────────────────

    [Fact]
    public void Constructor_AllFamiliesAvailable_WhenNoCurrentSelection()
    {
        var vm = MakeVm(["А", "Б", "В"], []);
        Assert.Equal(3, vm.AvailableFamilies.Count);
        Assert.Empty(vm.SelectedFamilies);
    }

    [Fact]
    public void Constructor_AvailableExcludesCurrentSelection()
    {
        var vm = MakeVm(
            ["А", "Б", "В"],
            [new FittingMapping { FamilyName = "А", Priority = 1 }]);
        Assert.Equal(2, vm.AvailableFamilies.Count);
        Assert.DoesNotContain("А", vm.AvailableFamilies);
    }

    [Fact]
    public void Constructor_SelectedOrderedByPriority()
    {
        var vm = MakeVm(
            ["А", "Б"],
            [
                new FittingMapping { FamilyName = "Б", Priority = 2 },
                new FittingMapping { FamilyName = "А", Priority = 1 },
            ]);
        Assert.Equal(2, vm.SelectedFamilies.Count);
        Assert.Equal("А", vm.SelectedFamilies[0]);
        Assert.Equal("Б", vm.SelectedFamilies[1]);
    }

    [Fact]
    public void Constructor_AvailableSortedAlphabetically()
    {
        var vm = MakeVm(["В", "А", "Б"], []);
        Assert.Equal("А", vm.AvailableFamilies[0]);
        Assert.Equal("Б", vm.AvailableFamilies[1]);
        Assert.Equal("В", vm.AvailableFamilies[2]);
    }

    [Fact]
    public void Constructor_ConfirmedIsFalse()
    {
        var vm = MakeVm();
        Assert.False(vm.Confirmed);
    }

    // ── Add command ───────────────────────────────────────────────────────

    [Fact]
    public void AddCommand_MovesSelectedToRight()
    {
        var vm = MakeVm(["А", "Б"], []);
        vm.SelectedAvailable = "А";
        vm.AddCommand.Execute(null);
        Assert.Contains("А", vm.SelectedFamilies);
        Assert.DoesNotContain("А", vm.AvailableFamilies);
    }

    [Fact]
    public void AddCommand_ClearsSelectedAvailable()
    {
        var vm = MakeVm(["А"], []);
        vm.SelectedAvailable = "А";
        vm.AddCommand.Execute(null);
        Assert.Null(vm.SelectedAvailable);
    }

    [Fact]
    public void AddCommand_CannotExecute_WhenNothingSelected()
    {
        var vm = MakeVm(["А"], []);
        vm.SelectedAvailable = null;
        Assert.False(vm.AddCommand.CanExecute(null));
    }

    [Fact]
    public void AddCommand_CanExecute_WhenFamilySelected()
    {
        var vm = MakeVm(["А"], []);
        vm.SelectedAvailable = "А";
        Assert.True(vm.AddCommand.CanExecute(null));
    }

    // ── Remove command ────────────────────────────────────────────────────

    [Fact]
    public void RemoveCommand_MovesBackToAvailable()
    {
        var vm = MakeVm(
            ["А", "Б"],
            [new FittingMapping { FamilyName = "А", Priority = 1 }]);
        vm.SelectedMapping = "А";
        vm.RemoveCommand.Execute(null);
        Assert.Empty(vm.SelectedFamilies);
        Assert.Contains("А", vm.AvailableFamilies);
    }

    [Fact]
    public void RemoveCommand_ClearsSelectedMapping()
    {
        var vm = MakeVm(
            ["А"],
            [new FittingMapping { FamilyName = "А", Priority = 1 }]);
        vm.SelectedMapping = "А";
        vm.RemoveCommand.Execute(null);
        Assert.Null(vm.SelectedMapping);
    }

    [Fact]
    public void RemoveCommand_RestoredToAvailableInSortedPosition()
    {
        var vm = MakeVm(
            ["А", "В"],
            [new FittingMapping { FamilyName = "Б", Priority = 1 }]);
        vm.SelectedMapping = "Б";
        vm.RemoveCommand.Execute(null);
        Assert.Equal(3, vm.AvailableFamilies.Count);
        Assert.Equal("А", vm.AvailableFamilies[0]);
        Assert.Equal("Б", vm.AvailableFamilies[1]);
        Assert.Equal("В", vm.AvailableFamilies[2]);
    }

    [Fact]
    public void RemoveCommand_CannotExecute_WhenNothingSelected()
    {
        var vm = MakeVm([], [new FittingMapping { FamilyName = "А", Priority = 1 }]);
        vm.SelectedMapping = null;
        Assert.False(vm.RemoveCommand.CanExecute(null));
    }

    // ── MoveUp / MoveDown ─────────────────────────────────────────────────

    [Fact]
    public void MoveUpCommand_ShiftsItemUp()
    {
        var vm = MakeVm(
            [],
            [
                new FittingMapping { FamilyName = "А", Priority = 1 },
                new FittingMapping { FamilyName = "Б", Priority = 2 },
            ]);
        vm.SelectedMapping = "Б";
        vm.MoveUpCommand.Execute(null);
        Assert.Equal("Б", vm.SelectedFamilies[0]);
        Assert.Equal("А", vm.SelectedFamilies[1]);
    }

    [Fact]
    public void MoveDownCommand_ShiftsItemDown()
    {
        var vm = MakeVm(
            [],
            [
                new FittingMapping { FamilyName = "А", Priority = 1 },
                new FittingMapping { FamilyName = "Б", Priority = 2 },
            ]);
        vm.SelectedMapping = "А";
        vm.MoveDownCommand.Execute(null);
        Assert.Equal("Б", vm.SelectedFamilies[0]);
        Assert.Equal("А", vm.SelectedFamilies[1]);
    }

    [Fact]
    public void MoveUpCommand_CannotExecute_WhenFirstItem()
    {
        var vm = MakeVm(
            [],
            [
                new FittingMapping { FamilyName = "А", Priority = 1 },
                new FittingMapping { FamilyName = "Б", Priority = 2 },
            ]);
        vm.SelectedMapping = "А";
        Assert.False(vm.MoveUpCommand.CanExecute(null));
    }

    [Fact]
    public void MoveDownCommand_CannotExecute_WhenLastItem()
    {
        var vm = MakeVm(
            [],
            [
                new FittingMapping { FamilyName = "А", Priority = 1 },
                new FittingMapping { FamilyName = "Б", Priority = 2 },
            ]);
        vm.SelectedMapping = "Б";
        Assert.False(vm.MoveDownCommand.CanExecute(null));
    }

    // ── Confirm / Cancel ──────────────────────────────────────────────────

    [Fact]
    public void ConfirmCommand_SetsConfirmedTrue()
    {
        var vm = MakeVm();
        vm.ConfirmCommand.Execute(null);
        Assert.True(vm.Confirmed);
    }

    [Fact]
    public void ConfirmCommand_RaisesRequestClose()
    {
        var vm = MakeVm();
        var raised = false;
        vm.RequestClose += _ => raised = true;
        vm.ConfirmCommand.Execute(null);
        Assert.True(raised);
    }

    [Fact]
    public void CancelCommand_SetsConfirmedFalse()
    {
        var vm = MakeVm();
        vm.ConfirmCommand.Execute(null);
        vm.CancelCommand.Execute(null);
        Assert.False(vm.Confirmed);
    }

    [Fact]
    public void CancelCommand_RaisesRequestClose()
    {
        var vm = MakeVm();
        var raised = false;
        vm.RequestClose += _ => raised = true;
        vm.CancelCommand.Execute(null);
        Assert.True(raised);
    }

    // ── GetResult ─────────────────────────────────────────────────────────

    [Fact]
    public void GetResult_ReturnsNull_WhenCancelled()
    {
        var vm = MakeVm([], [new FittingMapping { FamilyName = "А", Priority = 1 }]);
        vm.CancelCommand.Execute(null);
        Assert.Null(vm.GetResult());
    }

    [Fact]
    public void GetResult_ReturnsOrderedMappings_WhenConfirmed()
    {
        var vm = MakeVm(
            [],
            [
                new FittingMapping { FamilyName = "А", Priority = 1 },
                new FittingMapping { FamilyName = "Б", Priority = 2 },
            ]);
        vm.ConfirmCommand.Execute(null);
        var result = vm.GetResult();
        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
        Assert.Equal("А", result[0].FamilyName);
        Assert.Equal(1, result[0].Priority);
        Assert.Equal("Б", result[1].FamilyName);
        Assert.Equal(2, result[1].Priority);
    }

    [Fact]
    public void GetResult_PriorityReflectsPositionAfterMove()
    {
        var vm = MakeVm(
            [],
            [
                new FittingMapping { FamilyName = "А", Priority = 1 },
                new FittingMapping { FamilyName = "Б", Priority = 2 },
            ]);
        vm.SelectedMapping = "Б";
        vm.MoveUpCommand.Execute(null);
        vm.ConfirmCommand.Execute(null);
        var result = vm.GetResult()!;
        Assert.Equal("Б", result[0].FamilyName);
        Assert.Equal(1, result[0].Priority);
        Assert.Equal("А", result[1].FamilyName);
        Assert.Equal(2, result[1].Priority);
    }

    [Fact]
    public void GetResult_EmptySelectedFamilies_ReturnsEmptyList()
    {
        var vm = MakeVm();
        vm.ConfirmCommand.Execute(null);
        var result = vm.GetResult();
        Assert.NotNull(result);
        Assert.Empty(result!);
    }
}
