using Moq;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using SmartCon.FamilyManager.Services.Cloud;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// ADR-075 §7: Subscribed-гейт — read-only независимо от роли, auto-register
/// db_users отключён, InvalidateCache не включает запись.
/// </summary>
public sealed class DbAccessControlServiceCloudGateTests
{
    private static (DbAccessControlService service, Mock<IDbUserRepository> repoMock, LocalCatalogDatabase database, CloudDatabaseGate gate) MakeService(CloudLinkRole? role = null)
    {
        var repoMock = new Mock<IDbUserRepository>();
        var identityMock = new Mock<IUserIdentityService>();
        identityMock.Setup(s => s.GetCurrentUser()).Returns(new UserIdentity("user1", "Test User", "PC", "user1"));
        var database = new LocalCatalogDatabase();
        var compatMock = new Mock<IDatabaseCompatibilityService>();
        compatMock.SetupGet(c => c.IsDatabaseNewerThanPlugin).Returns(false);
        var gate = new CloudDatabaseGate();
        if (role is not null)
            gate.Update(new DatabaseConnection("id", "Cloud", "C:\\cloud", DateTimeOffset.UtcNow, CloudLink: new CloudLink(role.Value, "http://127.0.0.1:8787", "cat", "slug", null)));
        var service = new DbAccessControlService(repoMock.Object, identityMock.Object, database, compatMock.Object, gate);
        return (service, repoMock, database, gate);
    }

    [Fact]
    public async Task Subscribed_OwnerUser_AllWritesBlocked()
    {
        var (service, repoMock, _, _) = MakeService(CloudLinkRole.Subscribed);
        repoMock.Setup(r => r.GetOrCreateUserAsync(It.IsAny<UserIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DbUser("user1", "Test User", DbUserRole.Owner, DbUserStatus.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        await service.RefreshCurrentUserAsync(CancellationToken.None);

        Assert.True(service.IsCloudReadOnly);
        Assert.False(service.CanImport);
        Assert.False(service.CanEdit);
        Assert.False(service.CanManageUsers);
        // Read side stays open (loads from a subscribed copy are the point).
        Assert.True(service.CanLoadToProject);
    }

    [Fact]
    public async Task Subscribed_GetCurrentUser_SynthesizesEngineer_WithoutDbUsersWrite()
    {
        var (service, repoMock, _, _) = MakeService(CloudLinkRole.Subscribed);
        repoMock.Setup(r => r.GetOrCreateUserAsync(It.IsAny<UserIdentity>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("auto-register must not run on a Subscribed copy"));

        var user = await service.GetCurrentUserAsync(CancellationToken.None);

        Assert.Equal(DbUserRole.Engineer, user.Role);
        Assert.Equal(DbUserStatus.Active, user.Status);
        repoMock.Verify(r => r.GetOrCreateUserAsync(It.IsAny<UserIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Subscribed_ConnectionsStayReadOnly_AfterInvalidateCache()
    {
        var (service, _, database, _) = MakeService(CloudLinkRole.Subscribed);

        await service.RefreshCurrentUserAsync(CancellationToken.None);
        using (var connection = database.CreateConnection())
        {
            Assert.Contains("Mode=ReadOnly", connection.ConnectionString);
        }

        // ADR-075 §7: InvalidateCache used to blanket-re-enable writes —
        // on a Subscribed copy it must keep the read-only mode.
        service.InvalidateCache();
        using (var connection = database.CreateConnection())
        {
            Assert.Contains("Mode=ReadOnly", connection.ConnectionString);
        }
    }

    [Fact]
    public async Task Published_DoesNotGate()
    {
        var (service, repoMock, _, _) = MakeService(CloudLinkRole.Published);
        repoMock.Setup(r => r.GetOrCreateUserAsync(It.IsAny<UserIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DbUser("user1", "Test User", DbUserRole.Owner, DbUserStatus.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        await service.RefreshCurrentUserAsync(CancellationToken.None);

        Assert.False(service.IsCloudReadOnly);
        Assert.True(service.CanImport);
        Assert.True(service.CanEdit);
        Assert.True(service.CanManageUsers);
    }

    [Fact]
    public async Task NoCloudLink_DoesNotGate()
    {
        var (service, repoMock, _, _) = MakeService();
        repoMock.Setup(r => r.GetOrCreateUserAsync(It.IsAny<UserIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DbUser("user1", "Test User", DbUserRole.Owner, DbUserStatus.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        await service.RefreshCurrentUserAsync(CancellationToken.None);

        Assert.False(service.IsCloudReadOnly);
        Assert.True(service.CanImport);
    }
}
