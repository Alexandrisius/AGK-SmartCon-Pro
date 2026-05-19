using Moq;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.ViewModels;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

public sealed class ProfileViewModelTests
{
    private static (ProfileViewModel vm, Mock<IDbUserRepository> repoMock, Mock<IDbAccessControlService> accessMock, Mock<IUserIdentityService> identityMock, Mock<IFamilyManagerDialogService> dialogMock) MakeVm()
    {
        var repoMock = new Mock<IDbUserRepository>();
        var accessMock = new Mock<IDbAccessControlService>();
        var identityMock = new Mock<IUserIdentityService>();
        var dialogMock = new Mock<IFamilyManagerDialogService>();

        identityMock.Setup(s => s.GetCurrentUser()).Returns(new UserIdentity("user1", "Test User", "PC", "user1"));
        accessMock.Setup(a => a.GetCurrentUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DbUser("user1", "Test User", DbUserRole.Owner, DbUserStatus.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        accessMock.Setup(a => a.IsOwner).Returns(true);

        var vm = new ProfileViewModel(repoMock.Object, accessMock.Object, identityMock.Object, dialogMock.Object);
        return (vm, repoMock, accessMock, identityMock, dialogMock);
    }

    private static DbUser MakeUser(string id, string name, DbUserRole role, DbUserStatus status = DbUserStatus.Active)
    {
        return new DbUser(id, name, role, status, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task InitializeAsync_AsOwner_SetsIsOwnerTrue()
    {
        var (vm, repoMock, accessMock, _, _) = MakeVm();
        repoMock.Setup(r => r.GetAllUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DbUser>().AsReadOnly());

        await vm.InitializeAsync(CancellationToken.None);

        Assert.True(vm.IsOwner);
    }

    [Fact]
    public async Task InitializeAsync_AsOwner_LoadsUsers()
    {
        var (vm, repoMock, _, _, _) = MakeVm();
        var users = new List<DbUser>
        {
            MakeUser("u1", "Alice", DbUserRole.Owner),
            MakeUser("u2", "Bob", DbUserRole.Engineer),
        }.AsReadOnly();
        repoMock.Setup(r => r.GetAllUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(users);

        await vm.InitializeAsync(CancellationToken.None);

        Assert.Equal(2, vm.Users.Count);
    }

    [Fact]
    public async Task InitializeAsync_AsOwner_OwnerRowCannotBeBanned()
    {
        var (vm, repoMock, _, _, _) = MakeVm();
        var users = new List<DbUser>
        {
            MakeUser("owner1", "Owner", DbUserRole.Owner),
            MakeUser("u2", "Engineer", DbUserRole.Engineer),
        }.AsReadOnly();
        repoMock.Setup(r => r.GetAllUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(users);

        await vm.InitializeAsync(CancellationToken.None);

        var ownerRow = vm.Users.First(u => u.Role == DbUserRole.Owner);
        Assert.False(ownerRow.CanToggleBan);
    }

    [Fact]
    public async Task InitializeAsync_AsOwner_NonOwnerCanBeBanned()
    {
        var (vm, repoMock, _, _, _) = MakeVm();
        var users = new List<DbUser>
        {
            MakeUser("owner1", "Owner", DbUserRole.Owner),
            MakeUser("u2", "Engineer", DbUserRole.Engineer),
        }.AsReadOnly();
        repoMock.Setup(r => r.GetAllUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(users);

        await vm.InitializeAsync(CancellationToken.None);

        var engineerRow = vm.Users.First(u => u.Role == DbUserRole.Engineer);
        Assert.True(engineerRow.CanToggleBan);
    }

    [Fact]
    public async Task InitializeAsync_AsOwner_OwnerRowCannotEditRole()
    {
        var (vm, repoMock, _, _, _) = MakeVm();
        var users = new List<DbUser>
        {
            MakeUser("owner1", "Owner", DbUserRole.Owner),
            MakeUser("u2", "Engineer", DbUserRole.Engineer),
        }.AsReadOnly();
        repoMock.Setup(r => r.GetAllUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(users);

        await vm.InitializeAsync(CancellationToken.None);

        var ownerRow = vm.Users.First(u => u.Role == DbUserRole.Owner);
        Assert.False(ownerRow.CanEditRole);
    }

    [Fact]
    public async Task InitializeAsync_AsEngineer_SetsIsOwnerFalse()
    {
        var (vm, repoMock, accessMock, identityMock, _) = MakeVm();
        accessMock.Setup(a => a.GetCurrentUserAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeUser("user1", "Engineer", DbUserRole.Engineer));
        accessMock.Setup(a => a.IsOwner).Returns(false);
        repoMock.Setup(r => r.GetAllUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DbUser>().AsReadOnly());

        await vm.InitializeAsync(CancellationToken.None);

        Assert.False(vm.IsOwner);
    }

    [Fact]
    public void CloseCommand_RaisesRequestClose()
    {
        var (vm, _, _, _, _) = MakeVm();
        bool? closeResult = null;
        vm.RequestClose += r => closeResult = r;

        vm.CloseCommand.Execute(null);

        Assert.True(closeResult);
    }

    [Fact]
    public async Task ToggleBanAsync_ConfirmsBan()
    {
        var (vm, repoMock, _, _, dialogMock) = MakeVm();
        var users = new List<DbUser>
        {
            MakeUser("owner1", "Owner", DbUserRole.Owner),
            MakeUser("u2", "Bob", DbUserRole.Engineer),
        }.AsReadOnly();
        repoMock.Setup(r => r.GetAllUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(users);
        dialogMock.Setup(d => d.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        await vm.InitializeAsync(CancellationToken.None);

        var engineerRow = vm.Users.First(u => u.UserId == "u2");
        await engineerRow.ToggleBanCommand.ExecuteAsync(null);

        repoMock.Verify(r => r.UpdateUserStatusAsync("u2", DbUserStatus.Banned, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ChangeRole_ToOwner_ConfirmsTransfer()
    {
        var (vm, repoMock, _, _, dialogMock) = MakeVm();
        var owner = MakeUser("user1", "Current Owner", DbUserRole.Owner);
        var bm = MakeUser("bm1", "BimMaster", DbUserRole.BimMaster);
        var users = new List<DbUser> { owner, bm }.AsReadOnly();
        repoMock.Setup(r => r.GetAllUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(users);
        dialogMock.Setup(d => d.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);
        repoMock.Setup(r => r.TransferOwnershipAsync("user1", "bm1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await vm.InitializeAsync(CancellationToken.None);

        var bmRow = vm.Users.First(u => u.UserId == "bm1");
        bmRow.Role = DbUserRole.Owner;
        await bmRow.ChangeRoleCommand.ExecuteAsync(null);

        repoMock.Verify(r => r.TransferOwnershipAsync("user1", "bm1", It.IsAny<CancellationToken>()), Times.Once);
    }
}
