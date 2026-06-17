using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.ViewModels;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

/// <summary>
/// Unit tests for the shared-nested-family load-mode dialog VM.
/// Issue #67 — dialog has only an Apply button. The user must make
/// an explicit choice; closing via the X button is handled by the
/// service layer and returns SharedFamiliesLoadChoice.Skip.
/// </summary>
public sealed class SharedFamiliesLoadModeDialogViewModelTests
{
    [Fact]
    public void Create_DefaultChoice_IsUseProject()
    {
        var request = new SharedFamilyDecisionRequest(
            SharedFamilyName: "M_Flange",
            IsFamilyInUse: false,
            ParentFamilyName: "M_Pipe_Fitting");

        var vm = SharedFamiliesLoadModeDialogViewModel.Create(request);

        Assert.Equal(SharedFamiliesLoadChoice.UseProject, vm.SelectedChoice);
        Assert.Equal(SharedFamiliesLoadChoice.UseProject, vm.Result);
        Assert.True(vm.IsUseProjectSelected);
        Assert.False(vm.IsOverwriteParamsSelected);
        Assert.False(vm.IsOverwriteAllSelected);
    }

    [Fact]
    public void Create_PopulatesSharedFamilyName()
    {
        var request = new SharedFamilyDecisionRequest(
            SharedFamilyName: "M_Flange",
            IsFamilyInUse: false,
            ParentFamilyName: "M_Pipe_Fitting");

        var vm = SharedFamiliesLoadModeDialogViewModel.Create(request);

        Assert.Equal("M_Flange", vm.SharedFamilyName);
    }

    [Fact]
    public void Create_WhenInUse_FlagsIsFamilyInUse()
    {
        var request = new SharedFamilyDecisionRequest(
            SharedFamilyName: "M_Flange",
            IsFamilyInUse: true,
            ParentFamilyName: "M_Pipe_Fitting");

        var vm = SharedFamiliesLoadModeDialogViewModel.Create(request);

        Assert.True(vm.IsFamilyInUse);
    }

    [Fact]
    public void Create_WhenNotInUse_DoesNotFlagIsFamilyInUse()
    {
        var request = new SharedFamilyDecisionRequest(
            SharedFamilyName: "M_Flange",
            IsFamilyInUse: false,
            ParentFamilyName: "M_Pipe_Fitting");

        var vm = SharedFamiliesLoadModeDialogViewModel.Create(request);

        Assert.False(vm.IsFamilyInUse);
    }

    [Fact]
    public void SelectedChoice_Setter_UpdatesRadioFlags()
    {
        var request = new SharedFamilyDecisionRequest("M_Flange", false, "");
        var vm = SharedFamiliesLoadModeDialogViewModel.Create(request);

        vm.SelectedChoice = SharedFamiliesLoadChoice.OverwriteParameters;

        Assert.True(vm.IsOverwriteParamsSelected);
        Assert.False(vm.IsUseProjectSelected);
        Assert.False(vm.IsOverwriteAllSelected);
    }

    [Fact]
    public void SelectedChoice_SetToOverwriteAll_FlagsOverwriteAll()
    {
        var request = new SharedFamilyDecisionRequest("M_Flange", false, "");
        var vm = SharedFamiliesLoadModeDialogViewModel.Create(request);

        vm.SelectedChoice = SharedFamiliesLoadChoice.OverwriteAll;

        Assert.True(vm.IsOverwriteAllSelected);
        Assert.False(vm.IsUseProjectSelected);
        Assert.False(vm.IsOverwriteParamsSelected);
    }

    [Fact]
    public void ApplyCommand_SetsResultAndRaisesRequestCloseTrue()
    {
        var request = new SharedFamilyDecisionRequest("M_Flange", false, "");
        var vm = SharedFamiliesLoadModeDialogViewModel.Create(request);
        vm.SelectedChoice = SharedFamiliesLoadChoice.OverwriteParameters;

        bool? closeResult = null;
        vm.RequestClose += r => closeResult = r;

        vm.ApplyCommand.Execute(null);

        Assert.True(closeResult);
        Assert.Equal(SharedFamiliesLoadChoice.OverwriteParameters, vm.Result);
    }

    [Fact]
    public void ApplyCommand_UseProject_SetsResult()
    {
        var request = new SharedFamilyDecisionRequest("M_Flange", false, "");
        var vm = SharedFamiliesLoadModeDialogViewModel.Create(request);

        bool? closeResult = null;
        vm.RequestClose += r => closeResult = r;

        vm.ApplyCommand.Execute(null);

        Assert.True(closeResult);
        Assert.Equal(SharedFamiliesLoadChoice.UseProject, vm.Result);
    }

    [Fact]
    public void ApplyCommand_OverwriteAll_SetsResult()
    {
        var request = new SharedFamilyDecisionRequest("M_Flange", false, "");
        var vm = SharedFamiliesLoadModeDialogViewModel.Create(request);
        vm.SelectedChoice = SharedFamiliesLoadChoice.OverwriteAll;

        bool? closeResult = null;
        vm.RequestClose += r => closeResult = r;

        vm.ApplyCommand.Execute(null);

        Assert.True(closeResult);
        Assert.Equal(SharedFamiliesLoadChoice.OverwriteAll, vm.Result);
    }
}
