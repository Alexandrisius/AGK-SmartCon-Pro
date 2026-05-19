using Moq;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

public sealed class DbAccessControlServiceTests
{
    private static (DbAccessControlService service, Mock<IDbUserRepository> repoMock, Mock<IUserIdentityService> identityMock) MakeService()
    {
        var repoMock = new Mock<IDbUserRepository>();
        var identityMock = new Mock<IUserIdentityService>();
        identityMock.Setup(s => s.GetCurrentUser()).Returns(new UserIdentity("user1", "Test User", "PC", "user1"));
        var service = new DbAccessControlService(repoMock.Object, identityMock.Object);
        return (service, repoMock, identityMock);
    }

    private static DbUser MakeUser(DbUserRole role, DbUserStatus status = DbUserStatus.Active)
    {
        return new DbUser("user1", "Test User", role, status, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    private static async Task RefreshWithUser(DbAccessControlService service, Mock<IDbUserRepository> repoMock, DbUser user)
    {
        repoMock.Setup(r => r.GetOrCreateUserAsync(It.IsAny<UserIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        await service.RefreshCurrentUserAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CanImport_Owner_ReturnsTrue()
    {
        var (service, repoMock, _) = MakeService();
        await RefreshWithUser(service, repoMock, MakeUser(DbUserRole.Owner));

        Assert.True(service.CanImport);
    }

    [Fact]
    public async Task CanImport_BimMaster_ReturnsTrue()
    {
        var (service, repoMock, _) = MakeService();
        await RefreshWithUser(service, repoMock, MakeUser(DbUserRole.BimMaster));

        Assert.True(service.CanImport);
    }

    [Fact]
    public async Task CanImport_Engineer_ReturnsFalse()
    {
        var (service, repoMock, _) = MakeService();
        await RefreshWithUser(service, repoMock, MakeUser(DbUserRole.Engineer));

        Assert.False(service.CanImport);
    }

    [Fact]
    public async Task CanImport_BannedUser_ReturnsFalse()
    {
        var (service, repoMock, _) = MakeService();
        await RefreshWithUser(service, repoMock, MakeUser(DbUserRole.Owner, DbUserStatus.Banned));

        Assert.False(service.CanImport);
    }

    [Fact]
    public async Task CanEdit_Owner_ReturnsTrue()
    {
        var (service, repoMock, _) = MakeService();
        await RefreshWithUser(service, repoMock, MakeUser(DbUserRole.Owner));

        Assert.True(service.CanEdit);
    }

    [Fact]
    public async Task CanEdit_Engineer_ReturnsFalse()
    {
        var (service, repoMock, _) = MakeService();
        await RefreshWithUser(service, repoMock, MakeUser(DbUserRole.Engineer));

        Assert.False(service.CanEdit);
    }

    [Fact]
    public async Task CanManageUsers_Owner_ReturnsTrue()
    {
        var (service, repoMock, _) = MakeService();
        await RefreshWithUser(service, repoMock, MakeUser(DbUserRole.Owner));

        Assert.True(service.CanManageUsers);
    }

    [Fact]
    public async Task CanManageUsers_BimMaster_ReturnsFalse()
    {
        var (service, repoMock, _) = MakeService();
        await RefreshWithUser(service, repoMock, MakeUser(DbUserRole.BimMaster));

        Assert.False(service.CanManageUsers);
    }

    [Fact]
    public async Task IsOwner_Owner_ReturnsTrue()
    {
        var (service, repoMock, _) = MakeService();
        await RefreshWithUser(service, repoMock, MakeUser(DbUserRole.Owner));

        Assert.True(service.IsOwner);
    }

    [Fact]
    public async Task IsBanned_BannedUser_ReturnsTrue()
    {
        var (service, repoMock, _) = MakeService();
        await RefreshWithUser(service, repoMock, MakeUser(DbUserRole.Engineer, DbUserStatus.Banned));

        Assert.True(service.IsBanned);
    }

    [Fact]
    public async Task RefreshCurrentUserAsync_UpdatesCachedUser()
    {
        var (service, repoMock, _) = MakeService();
        var user = MakeUser(DbUserRole.BimMaster, DbUserStatus.Active);
        repoMock.Setup(r => r.GetOrCreateUserAsync(It.IsAny<UserIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        await service.RefreshCurrentUserAsync(CancellationToken.None);

        var current = await service.GetCurrentUserAsync(CancellationToken.None);
        Assert.Equal("user1", current.UserId);
        Assert.Equal(DbUserRole.BimMaster, current.Role);
        Assert.Equal(DbUserStatus.Active, current.Status);
    }

    [Fact]
    public async Task CanLoadToProject_BannedUser_ReturnsFalse()
    {
        var (service, repoMock, _) = MakeService();
        await RefreshWithUser(service, repoMock, MakeUser(DbUserRole.Engineer, DbUserStatus.Banned));

        Assert.False(service.CanLoadToProject);
    }

    [Fact]
    public async Task CanLoadToProject_ActiveUser_ReturnsTrue()
    {
        var (service, repoMock, _) = MakeService();
        await RefreshWithUser(service, repoMock, MakeUser(DbUserRole.Engineer, DbUserStatus.Active));

        Assert.True(service.CanLoadToProject);
    }
}
