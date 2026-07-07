using Moq;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using SmartCon.FamilyManager.ViewModels;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// Unit tests for FamilyManagerDialogService.ShowSharedFamiliesLoadModeDialog.
/// Issue #67 — dialog has no Cancel button. Closing via X (Alt+F4) returns Skip.
/// </summary>
public sealed class FamilyManagerDialogServiceSharedNestedTests
{
    private readonly Mock<IDialogPresenter> _presenterMock = new();

    private void SetupPresenter(Action<SharedFamiliesLoadModeDialogViewModel> action, bool result)
    {
        _presenterMock
            .Setup(p => p.ShowDialog(It.IsAny<object>()))
            .Callback<object>(vm =>
            {
                var dlg = vm as SharedFamiliesLoadModeDialogViewModel;
                if (dlg is not null) action(dlg);
            })
            .Returns(result);
        _presenterMock
            .Setup(p => p.ShowDialog<SharedFamiliesLoadModeDialogViewModel>(It.IsAny<SharedFamiliesLoadModeDialogViewModel>()))
            .Callback<SharedFamiliesLoadModeDialogViewModel>(vm => action(vm))
            .Returns(result);
    }

    [Fact]
    public void ShowSharedFamiliesLoadModeDialog_WhenClosedViaX_ReturnsSkip()
    {
        SetupPresenter(_ => { }, false);

        var service = new FamilyManagerDialogService(_presenterMock.Object);

        var request = new SharedFamilyDecisionRequest("M_Flange", false, "M_Pipe");
        var result = service.ShowSharedFamiliesLoadModeDialog(request);

        Assert.Equal(SharedFamiliesLoadChoice.Skip, result);
    }

    [Fact]
    public void ShowSharedFamiliesLoadModeDialog_WhenUserChoosesOverwrite_ReturnsChoice()
    {
        SetupPresenter(vm =>
        {
            vm.SelectedChoice = SharedFamiliesLoadChoice.OverwriteAll;
            vm.ApplyCommand.Execute(null);
        }, true);

        var service = new FamilyManagerDialogService(_presenterMock.Object);

        var request = new SharedFamilyDecisionRequest("M_Flange", false, "M_Pipe");
        var result = service.ShowSharedFamiliesLoadModeDialog(request);

        Assert.Equal(SharedFamiliesLoadChoice.OverwriteAll, result);
    }

    [Fact]
    public void ShowSharedFamiliesLoadModeDialog_PassesRequestToViewModel()
    {
        SharedFamiliesLoadModeDialogViewModel? capturedVm = null;
        SetupPresenter(vm =>
        {
            capturedVm = vm;
            vm.SelectedChoice = SharedFamiliesLoadChoice.OverwriteParameters;
            vm.ApplyCommand.Execute(null);
        }, true);

        var service = new FamilyManagerDialogService(_presenterMock.Object);

        var request = new SharedFamilyDecisionRequest(
            SharedFamilyName: "M_Special_Flange",
            IsFamilyInUse: true,
            ParentFamilyName: "M_Pipe");

        service.ShowSharedFamiliesLoadModeDialog(request);

        Assert.NotNull(capturedVm);
        Assert.Equal("M_Special_Flange", capturedVm!.SharedFamilyName);
        Assert.True(capturedVm.IsFamilyInUse);
    }
}
