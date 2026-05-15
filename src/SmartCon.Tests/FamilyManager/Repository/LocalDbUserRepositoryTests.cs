using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

public sealed class LocalDbUserRepositoryTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly LocalDbUserRepository _repo;

    public LocalDbUserRepositoryTests()
    {
        _fixture = new TempCatalogFixture();
        _repo = new LocalDbUserRepository(_fixture.GetDatabase());
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task GetOrCreateUserAsync_NewUser_CreatesAsEngineer()
    {
        var identity = new UserIdentity("user1", "Alice", "PC1", "user1");

        var user = await _repo.GetOrCreateUserAsync(identity, CancellationToken.None);

        Assert.Equal("user1", user.UserId);
        Assert.Equal("Alice", user.DisplayName);
        Assert.Equal(DbUserRole.Engineer, user.Role);
        Assert.Equal(DbUserStatus.Active, user.Status);
    }

    [Fact]
    public async Task GetOrCreateUserAsync_ExistingUser_UpdatesLastSeen()
    {
        var identity = new UserIdentity("user1", "Alice", "PC1", "user1");
        var first = await _repo.GetOrCreateUserAsync(identity, CancellationToken.None);

        var second = await _repo.GetOrCreateUserAsync(identity, CancellationToken.None);

        Assert.Equal("user1", second.UserId);
        Assert.True(second.LastSeenAtUtc >= first.LastSeenAtUtc);
    }

    [Fact]
    public async Task GetOrCreateUserAsync_BannedUser_ThrowsDbAccessDeniedException()
    {
        using var conn = new SqliteConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO db_users (user_id, display_name, role, status, joined_at_utc, last_seen_at_utc) VALUES (@id, @name, 'Engineer', 'Banned', @now, @now)";
        cmd.Parameters.Add(new SqliteParameter("@id", "banned1"));
        cmd.Parameters.Add(new SqliteParameter("@name", "Banned User"));
        cmd.Parameters.Add(new SqliteParameter("@now", DateTimeOffset.UtcNow.ToString("o")));
        await cmd.ExecuteNonQueryAsync();

        var identity = new UserIdentity("banned1", "Banned User", "PC", "banned1");

        await Assert.ThrowsAsync<DbAccessDeniedException>(() => _repo.GetOrCreateUserAsync(identity, CancellationToken.None));
    }

    [Fact]
    public async Task GetAllUsersAsync_ReturnsAllUsers()
    {
        await InsertUserAsync("u1", "Alice");
        await InsertUserAsync("u2", "Bob");
        await InsertUserAsync("u3", "Charlie");

        var users = await _repo.GetAllUsersAsync(CancellationToken.None);

        Assert.Equal(3, users.Count);
    }

    [Fact]
    public async Task GetUserAsync_ExistingUser_ReturnsUser()
    {
        await InsertUserAsync("u1", "Alice", "Engineer", "Active");

        var user = await _repo.GetUserAsync("u1", CancellationToken.None);

        Assert.NotNull(user);
        Assert.Equal("u1", user.UserId);
        Assert.Equal("Alice", user.DisplayName);
    }

    [Fact]
    public async Task GetUserAsync_NonexistentUser_ReturnsNull()
    {
        var user = await _repo.GetUserAsync("nonexistent", CancellationToken.None);

        Assert.Null(user);
    }

    [Fact]
    public async Task UpdateUserRoleAsync_UpdatesRole()
    {
        await InsertUserAsync("u1", "Alice", "Engineer", "Active");

        var result = await _repo.UpdateUserRoleAsync("u1", DbUserRole.BimMaster, CancellationToken.None);

        Assert.True(result);
        var user = await _repo.GetUserAsync("u1", CancellationToken.None);
        Assert.Equal(DbUserRole.BimMaster, user!.Role);
    }

    [Fact]
    public async Task UpdateUserStatusAsync_UpdatesStatus()
    {
        await InsertUserAsync("u1", "Alice", "Engineer", "Active");

        var result = await _repo.UpdateUserStatusAsync("u1", DbUserStatus.Banned, CancellationToken.None);

        Assert.True(result);
        var user = await _repo.GetUserAsync("u1", CancellationToken.None);
        Assert.Equal(DbUserStatus.Banned, user!.Status);
    }

    [Fact]
    public async Task RemoveUserAsync_NonOwner_Deletes()
    {
        await InsertUserAsync("u1", "Alice", "Engineer", "Active");

        var result = await _repo.RemoveUserAsync("u1", CancellationToken.None);

        Assert.True(result);
        var user = await _repo.GetUserAsync("u1", CancellationToken.None);
        Assert.Null(user);
    }

    [Fact]
    public async Task RemoveUserAsync_Owner_ReturnsFalse()
    {
        await InsertUserAsync("owner1", "Owner", "Owner", "Active");
        await SetOwnerIdentityAsync("owner1");

        var result = await _repo.RemoveUserAsync("owner1", CancellationToken.None);

        Assert.False(result);
        var user = await _repo.GetUserAsync("owner1", CancellationToken.None);
        Assert.NotNull(user);
    }

    [Fact]
    public async Task TransferOwnershipAsync_TransfersRole()
    {
        await InsertUserAsync("owner1", "Owner", "Owner", "Active");
        await InsertUserAsync("bm1", "BimMaster", "BimMaster", "Active");
        await SetOwnerIdentityAsync("owner1");

        var result = await _repo.TransferOwnershipAsync("owner1", "bm1", CancellationToken.None);

        Assert.True(result);

        var formerOwner = await _repo.GetUserAsync("owner1", CancellationToken.None);
        var newOwner = await _repo.GetUserAsync("bm1", CancellationToken.None);

        Assert.Equal(DbUserRole.BimMaster, formerOwner!.Role);
        Assert.Equal(DbUserRole.Owner, newOwner!.Role);

        var ownerIdentity = await _repo.GetOwnerIdentityAsync(CancellationToken.None);
        Assert.Equal("bm1", ownerIdentity);
    }

    [Fact]
    public async Task TransferOwnershipAsync_InvalidNewOwner_ReturnsFalse()
    {
        await InsertUserAsync("owner1", "Owner", "Owner", "Active");
        await SetOwnerIdentityAsync("owner1");

        var result = await _repo.TransferOwnershipAsync("owner1", "nonexistent", CancellationToken.None);

        Assert.False(result);

        var owner = await _repo.GetUserAsync("owner1", CancellationToken.None);
        Assert.Equal(DbUserRole.Owner, owner!.Role);
    }

    [Fact]
    public async Task GetOwnerIdentityAsync_ReturnsOwnerId()
    {
        await InsertUserAsync("owner1", "Owner", "Owner", "Active");
        await SetOwnerIdentityAsync("owner1");

        var identity = await _repo.GetOwnerIdentityAsync(CancellationToken.None);

        Assert.Equal("owner1", identity);
    }

    [Fact]
    public async Task GetUserCountAsync_ReturnsCorrectCount()
    {
        Assert.Equal(0, await _repo.GetUserCountAsync(CancellationToken.None));

        await InsertUserAsync("u1", "Alice");
        Assert.Equal(1, await _repo.GetUserCountAsync(CancellationToken.None));

        await InsertUserAsync("u2", "Bob");
        Assert.Equal(2, await _repo.GetUserCountAsync(CancellationToken.None));
    }

    private async Task InsertUserAsync(string userId, string displayName, string role = "Engineer", string status = "Active")
    {
        using var conn = new SqliteConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO db_users (user_id, display_name, role, status, joined_at_utc, last_seen_at_utc) VALUES (@id, @name, @role, @status, @now, @now)";
        cmd.Parameters.Add(new SqliteParameter("@id", userId));
        cmd.Parameters.Add(new SqliteParameter("@name", displayName));
        cmd.Parameters.Add(new SqliteParameter("@role", role));
        cmd.Parameters.Add(new SqliteParameter("@status", status));
        cmd.Parameters.Add(new SqliteParameter("@now", DateTimeOffset.UtcNow.ToString("o")));
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task EnsureMetaRowAsync()
    {
        using var conn = new SqliteConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        using var countCmd = conn.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM database_meta";
        var count = (long)(await countCmd.ExecuteScalarAsync())!;
        if (count == 0)
        {
            using var insertCmd = conn.CreateCommand();
            insertCmd.CommandText = "INSERT INTO database_meta (id, name, description, created_at_utc, schema_version) VALUES ('test', 'Test DB', NULL, @now, 7)";
            insertCmd.Parameters.Add(new SqliteParameter("@now", DateTimeOffset.UtcNow.ToString("o")));
            await insertCmd.ExecuteNonQueryAsync();
        }
    }

    private async Task SetOwnerIdentityAsync(string userId)
    {
        await EnsureMetaRowAsync();
        using var conn = new SqliteConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        using var metaCmd = conn.CreateCommand();
        metaCmd.CommandText = "UPDATE database_meta SET owner_identity = @id";
        metaCmd.Parameters.Add(new SqliteParameter("@id", userId));
        await metaCmd.ExecuteNonQueryAsync();
    }
}
