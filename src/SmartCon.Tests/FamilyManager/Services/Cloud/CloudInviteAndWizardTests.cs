using SmartCon.FamilyManager.Services.Cloud;
using SmartCon.FamilyManager.ViewModels;
using SmartCon.FamilyManager.ViewModels.Cloud;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services.Cloud;

public sealed class CloudInviteTests
{
    [Fact]
    public void Build_ThenParse_RoundTrips()
    {
        var invite = CloudInvite.Build("http://127.0.0.1:8787", "otvody-co");

        var parsed = CloudInvite.TryParse(invite);

        Assert.NotNull(parsed);
        Assert.Equal("http://127.0.0.1:8787", parsed.Endpoint);
        Assert.Equal("otvody-co", parsed.Slug);
        Assert.Null(parsed.Key);
    }

    [Fact]
    public void TryParse_ValidStringWithoutPadding_Parses()
    {
        // base64url без padding, с '-"/_' в алфавите — собираем вручную.
        var invite = CloudInvite.Build("https://cloud.example.com", "slug_42-x");
        Assert.DoesNotContain('+', invite);
        Assert.DoesNotContain('/', invite);
        Assert.DoesNotContain('=', invite);

        var parsed = CloudInvite.TryParse(invite);

        Assert.NotNull(parsed);
        Assert.Equal("slug_42-x", parsed!.Slug);
        Assert.Equal("https://cloud.example.com", parsed.Endpoint);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("smartcon-cloud:subscribe:")]            // пустой payload
    [InlineData("smartcon-cloud:subscribe:!!!!")]        // не base64
    [InlineData("smartcon-cloud:subscribe:YWJjZA")]      // не JSON
    [InlineData("smartcon-cloud:subscribe:eyJ4IjoxfQ")]  // JSON без slug
    [InlineData("https://example.com/invite")]           // чужой формат
    public void TryParse_Invalid_ReturnsNull(string? text)
    {
        Assert.Null(CloudInvite.TryParse(text));
    }

    [Fact]
    public void TryParse_TrimsWhitespace_AndIgnoresCaseOfPrefix()
    {
        var invite = CloudInvite.Build("http://srv", "demo");
        // Регистр важен только в base64-payload; префикс — нет.
        var mixed = "SMARTCON-CLOUD:SUBSCRIBE:" + invite[CloudInvite.Prefix.Length..];
        var parsed = CloudInvite.TryParse("  " + mixed + "\r\n");
        Assert.NotNull(parsed);
        Assert.Equal("demo", parsed!.Slug);
    }
}

/// <summary>
/// Мастер «Создать облачную базу» — только создание (подключение по
/// приглашению переехало в «Подключить базу», решение владельца 2026-09-11).
/// </summary>
public sealed class CloudDatabaseWizardViewModelTests
{
    [Fact]
    public void EmptyName_OkDisabled()
    {
        var vm = new CloudDatabaseWizardViewModel();

        Assert.False(vm.OkCommand.CanExecute(null));
    }

    [Fact]
    public void WithName_OkEnabled()
    {
        var vm = new CloudDatabaseWizardViewModel
        {
            DatabaseName = "Отводы компании",
        };

        Assert.True(vm.OkCommand.CanExecute(null));
    }

    [Fact]
    public void WhitespaceName_OkDisabled()
    {
        var vm = new CloudDatabaseWizardViewModel
        {
            DatabaseName = "   ",
        };

        Assert.False(vm.OkCommand.CanExecute(null));
    }

    [Fact]
    public void Ok_SetsAccepted_AndRaisesRequestClose()
    {
        var vm = new CloudDatabaseWizardViewModel { DatabaseName = "X" };
        bool? closedWith = null;
        vm.RequestClose += result => closedWith = result;

        vm.OkCommand.Execute(null);

        Assert.True(vm.Accepted);
        Assert.True(closedWith);
    }

    [Fact]
    public void Cancel_NotAccepted_ClosesWithNull()
    {
        var vm = new CloudDatabaseWizardViewModel();
        bool? closedWith = true;
        vm.RequestClose += result => closedWith = result;

        vm.CancelCommand.Execute(null);

        Assert.False(vm.Accepted);
        Assert.Null(closedWith);
    }
}

/// <summary>
/// Диалог «Подключить базу»: радио локальная папка / облачная по строке
/// приглашения, папка через колбэк браузера, валидация приглашения.
/// </summary>
public sealed class ConnectDatabaseViewModelTests
{
    [Fact]
    public void Ctor_LocalModeSelectedByDefault()
    {
        // UX: «Локальную базу из папки» предвыбран — RadioButtons биндятся на эти флаги.
        var vm = new ConnectDatabaseViewModel();

        Assert.True(vm.IsLocalMode);
        Assert.False(vm.IsCloudMode);
        Assert.Equal(ConnectDatabaseViewModel.ConnectMode.LocalFolder, vm.Mode);
    }

    [Fact]
    public void RadioSetters_SwitchMode()
    {
        var vm = new ConnectDatabaseViewModel();

        vm.IsCloudMode = true;
        Assert.Equal(ConnectDatabaseViewModel.ConnectMode.CloudInvite, vm.Mode);
        Assert.False(vm.IsLocalMode);

        vm.IsLocalMode = true;
        Assert.Equal(ConnectDatabaseViewModel.ConnectMode.LocalFolder, vm.Mode);
        Assert.False(vm.IsCloudMode);

        // Снятие галки группой не должно сбрасывать Mode (сеттер игнорирует false).
        vm.IsLocalMode = false;
        Assert.Equal(ConnectDatabaseViewModel.ConnectMode.LocalFolder, vm.Mode);
    }

    [Fact]
    public void LocalMode_EmptyFolder_OkDisabled()
    {
        var vm = new ConnectDatabaseViewModel();

        Assert.False(vm.OkCommand.CanExecute(null));
    }

    [Fact]
    public void LocalMode_WithFolder_OkEnabled()
    {
        var vm = new ConnectDatabaseViewModel { FolderPath = @"C:\bases\otvody" };

        Assert.True(vm.OkCommand.CanExecute(null));
    }

    [Fact]
    public void CloudMode_EmptyInvite_OkDisabled_NoError()
    {
        var vm = new ConnectDatabaseViewModel { IsCloudMode = true };

        Assert.False(vm.OkCommand.CanExecute(null));
        Assert.False(vm.HasInviteError);
    }

    [Fact]
    public void CloudMode_GarbageInvite_ShowsError_OkDisabled()
    {
        var vm = new ConnectDatabaseViewModel
        {
            IsCloudMode = true,
            InviteString = "мусор",
        };

        Assert.True(vm.HasInviteError);
        Assert.False(vm.OkCommand.CanExecute(null));
    }

    [Fact]
    public void CloudMode_ValidInvite_OkEnabled_InviteParsed()
    {
        var vm = new ConnectDatabaseViewModel
        {
            IsCloudMode = true,
            InviteString = CloudInvite.Build("http://srv", "demo"),
        };

        Assert.False(vm.HasInviteError);
        Assert.True(vm.OkCommand.CanExecute(null));
        Assert.Equal("demo", vm.Invite!.Slug);
    }

    [Fact]
    public void Browse_SetsFolderFromCallback()
    {
        var vm = new ConnectDatabaseViewModel(() => @"C:\bases\picked");

        vm.BrowseCommand.Execute(null);

        Assert.Equal(@"C:\bases\picked", vm.FolderPath);
        Assert.True(vm.OkCommand.CanExecute(null));
    }

    [Fact]
    public void Browse_Cancelled_KeepsFolder()
    {
        var vm = new ConnectDatabaseViewModel(() => null) { FolderPath = @"C:\keep" };

        vm.BrowseCommand.Execute(null);

        Assert.Equal(@"C:\keep", vm.FolderPath);
    }

    [Fact]
    public void Ok_SetsAccepted_AndRaisesRequestClose()
    {
        var vm = new ConnectDatabaseViewModel { FolderPath = @"C:\bases" };
        bool? closedWith = null;
        vm.RequestClose += result => closedWith = result;

        vm.OkCommand.Execute(null);

        Assert.True(vm.Accepted);
        Assert.True(closedWith);
    }

    [Fact]
    public void Cancel_NotAccepted_ClosesWithNull()
    {
        var vm = new ConnectDatabaseViewModel();
        bool? closedWith = true;
        vm.RequestClose += result => closedWith = result;

        vm.CancelCommand.Execute(null);

        Assert.False(vm.Accepted);
        Assert.Null(closedWith);
    }
}
