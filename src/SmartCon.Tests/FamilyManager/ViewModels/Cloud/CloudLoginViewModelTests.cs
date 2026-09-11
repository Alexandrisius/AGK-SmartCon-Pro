using System.IO;
using System.Net;
using System.Net.Http;
using Moq;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.Cloud;
using SmartCon.FamilyManager.ViewModels.Cloud;
using SmartCon.Tests.FamilyManager.Services.Cloud;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels.Cloud;

public sealed class CloudLoginViewModelTests
{
    private static (CloudLoginViewModel vm, FakeHttpMessageHandler http) MakeVm(
        HttpStatusCode status = HttpStatusCode.Unauthorized, string body = "{\"code\":\"invalid\",\"error\":\"нет\"}")
    {
        var http = new FakeHttpMessageHandler();
        http.Enqueue(new HttpResponseMessage(status) { Content = new StringContent(body) });
        var credentials = new Mock<ICloudCredentialStore>();
        var clock = new Mock<IClock>();
        var auth = new CloudAuthService(new HttpClient(http), credentials.Object, clock.Object,
            Path.Combine(Path.GetTempPath(), $"cloud-account-{Guid.NewGuid():N}.json"));
        return (new CloudLoginViewModel(auth), http);
    }

    [Fact]
    public void EmptyFields_SubmitDisabled()
    {
        var (vm, _) = MakeVm();
        vm.Endpoint = "http://srv";
        vm.Email = "user@example.com";
        vm.Password = "";

        Assert.False(vm.SubmitCommand.CanExecute(null));
    }

    [Fact]
    public void LoginMode_DisplayNameNotRequired()
    {
        var (vm, _) = MakeVm();
        vm.Endpoint = "http://srv";
        vm.Email = "user@example.com";
        vm.Password = "secret";

        Assert.True(vm.SubmitCommand.CanExecute(null));
    }

    [Fact]
    public void RegisterMode_DisplayNameRequired()
    {
        var (vm, _) = MakeVm();
        vm.Endpoint = "http://srv";
        vm.Email = "user@example.com";
        vm.Password = "secret";
        vm.IsRegisterMode = true;
        Assert.False(vm.SubmitCommand.CanExecute(null));

        vm.DisplayName = "Иван";
        Assert.True(vm.SubmitCommand.CanExecute(null));
        Assert.False(vm.IsLoginMode);
    }

    [Fact]
    public async Task Submit_Failure_ShowsError_KeepsDialogOpen()
    {
        var (vm, _) = MakeVm(HttpStatusCode.Unauthorized);
        vm.Endpoint = "http://srv";
        vm.Email = "user@example.com";
        vm.Password = "wrong";
        bool? closed = null;
        vm.RequestClose += r => closed = r;

        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.False(vm.Success);
        Assert.False(closed.HasValue);
        Assert.True(vm.HasError);
        Assert.Contains("не удалось", vm.ErrorText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ctor_UsesAccountEndpoint_ThenDefault()
    {
        var (vm, _) = MakeVm();
        // Аккаунта нет → dev-endpoint среза v1.
        Assert.Equal(CloudLoginViewModel.DefaultEndpoint, vm.Endpoint);
    }
}
