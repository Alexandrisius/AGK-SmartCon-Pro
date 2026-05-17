using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed class LocalDbUserRepository : IDbUserRepository
{
    private readonly LocalCatalogDatabase _database;

    public LocalDbUserRepository(LocalCatalogDatabase database)
    {
        _database = database;
    }

    public async Task<DbUser?> GetUserAsync(string userId, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT user_id, display_name, role, status, joined_at_utc, last_seen_at_utc FROM db_users WHERE user_id = @userId";
        cmd.Parameters.Add(new SqliteParameter("@userId", userId));
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
            return ReadDbUser(reader);
        return null;
    }

    public async Task<IReadOnlyList<DbUser>> GetAllUsersAsync(CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT user_id, display_name, role, status, joined_at_utc, last_seen_at_utc FROM db_users ORDER BY joined_at_utc";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var users = new List<DbUser>();
        while (await reader.ReadAsync(ct))
            users.Add(ReadDbUser(reader));
        return users.AsReadOnly();
    }

    public async Task<DbUser> GetOrCreateUserAsync(UserIdentity identity, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);

        using var tx = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT user_id, display_name, role, status, joined_at_utc, last_seen_at_utc FROM db_users WHERE user_id = @userId";
        cmd.Parameters.Add(new SqliteParameter("@userId", identity.UserId));
        using var reader = await cmd.ExecuteReaderAsync(ct);

        if (await reader.ReadAsync(ct))
        {
            var existing = ReadDbUser(reader);
            reader.Close();

            if (existing.Status == DbUserStatus.Banned)
            {
                throw new DbAccessDeniedException(await GetDbNameAsync(connection, ct), await GetOwnerDisplayNameAsync(connection, ct));
            }

            using var updateCmd = connection.CreateCommand();
            updateCmd.CommandText = "UPDATE db_users SET last_seen_at_utc = @now, display_name = @displayName WHERE user_id = @userId";
            updateCmd.Parameters.Add(new SqliteParameter("@now", DateTimeOffset.UtcNow.ToString("o")));
            updateCmd.Parameters.Add(new SqliteParameter("@displayName", identity.DisplayName));
            updateCmd.Parameters.Add(new SqliteParameter("@userId", identity.UserId));
            await updateCmd.ExecuteNonQueryAsync(ct);

            tx.Commit();
            return existing with { LastSeenAtUtc = DateTimeOffset.UtcNow, DisplayName = identity.DisplayName };
        }

        reader.Close();

        var now = DateTimeOffset.UtcNow.ToString("o");
        try
        {
            using var insertCmd = connection.CreateCommand();
            insertCmd.CommandText = "INSERT INTO db_users (user_id, display_name, role, status, joined_at_utc, last_seen_at_utc) VALUES (@userId, @displayName, 'Engineer', 'Active', @now, @now)";
            insertCmd.Parameters.Add(new SqliteParameter("@userId", identity.UserId));
            insertCmd.Parameters.Add(new SqliteParameter("@displayName", identity.DisplayName));
            insertCmd.Parameters.Add(new SqliteParameter("@now", now));
            await insertCmd.ExecuteNonQueryAsync(ct);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return await GetUserAsync(identity.UserId, ct)
                ?? throw new InvalidOperationException($"User {identity.UserId} not found after constraint violation.");
        }

        tx.Commit();
        return new DbUser(identity.UserId, identity.DisplayName, DbUserRole.Engineer, DbUserStatus.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    public async Task<bool> UpdateUserRoleAsync(string userId, DbUserRole role, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE db_users SET role = @role WHERE user_id = @userId";
        cmd.Parameters.Add(new SqliteParameter("@role", role.ToString()));
        cmd.Parameters.Add(new SqliteParameter("@userId", userId));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> UpdateUserStatusAsync(string userId, DbUserStatus status, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE db_users SET status = @status WHERE user_id = @userId";
        cmd.Parameters.Add(new SqliteParameter("@status", status.ToString()));
        cmd.Parameters.Add(new SqliteParameter("@userId", userId));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> RemoveUserAsync(string userId, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);

        using var tx = connection.BeginTransaction();
        using var checkCmd = connection.CreateCommand();
        checkCmd.CommandText = "SELECT role FROM db_users WHERE user_id = @userId";
        checkCmd.Parameters.Add(new SqliteParameter("@userId", userId));
        var role = (await checkCmd.ExecuteScalarAsync(ct))?.ToString();
        if (role == "Owner")
        {
            return false;
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM db_users WHERE user_id = @userId";
        cmd.Parameters.Add(new SqliteParameter("@userId", userId));
        var deleted = await cmd.ExecuteNonQueryAsync(ct) > 0;
        tx.Commit();
        return deleted;
    }

    public async Task<int> GetUserCountAsync(CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM db_users";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long l ? (int)l : 0;
    }

    public async Task<bool> TransferOwnershipAsync(string currentOwnerUserId, string newOwnerUserId, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);

        using var tx = connection.BeginTransaction();
        using var demoteCmd = connection.CreateCommand();
        demoteCmd.CommandText = "UPDATE db_users SET role = 'BimMaster' WHERE user_id = @userId AND role = 'Owner'";
        demoteCmd.Parameters.Add(new SqliteParameter("@userId", currentOwnerUserId));
        var demoted = await demoteCmd.ExecuteNonQueryAsync(ct);
        if (demoted == 0)
        {
            return false;
        }

        using var promoteCmd = connection.CreateCommand();
        promoteCmd.CommandText = "UPDATE db_users SET role = 'Owner' WHERE user_id = @userId";
        promoteCmd.Parameters.Add(new SqliteParameter("@userId", newOwnerUserId));
        var promoted = await promoteCmd.ExecuteNonQueryAsync(ct);
        if (promoted == 0)
        {
            return false;
        }

        using var statusCmd = connection.CreateCommand();
        statusCmd.CommandText = "SELECT status FROM db_users WHERE user_id = @userId AND role = 'Owner'";
        statusCmd.Parameters.Add(new SqliteParameter("@userId", newOwnerUserId));
        var newOwnerStatus = (await statusCmd.ExecuteScalarAsync(ct))?.ToString();
        if (newOwnerStatus != "Active")
        {
            return false;
        }

        using var metaCmd = connection.CreateCommand();
        metaCmd.CommandText = "UPDATE database_meta SET owner_identity = @ownerIdentity";
        metaCmd.Parameters.Add(new SqliteParameter("@ownerIdentity", newOwnerUserId));
        await metaCmd.ExecuteNonQueryAsync(ct);

        tx.Commit();
        return true;
    }

    public async Task<string?> GetOwnerIdentityAsync(CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT owner_identity FROM database_meta LIMIT 1";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result?.ToString();
    }

    private static async Task<string> GetDbNameAsync(SqliteConnection connection, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM database_meta LIMIT 1";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result?.ToString() ?? "Unknown";
    }

    private static async Task<string> GetOwnerDisplayNameAsync(SqliteConnection connection, CancellationToken ct)
    {
        using var metaCmd = connection.CreateCommand();
        metaCmd.CommandText = "SELECT owner_identity FROM database_meta LIMIT 1";
        var ownerId = (await metaCmd.ExecuteScalarAsync(ct))?.ToString();

        if (!string.IsNullOrEmpty(ownerId))
        {
            using var userCmd = connection.CreateCommand();
            userCmd.CommandText = "SELECT display_name FROM db_users WHERE user_id = @userId";
            userCmd.Parameters.Add(new SqliteParameter("@userId", ownerId));
            var displayName = (await userCmd.ExecuteScalarAsync(ct))?.ToString() ?? string.Empty;
            if (!string.IsNullOrEmpty(displayName))
                return displayName;
        }

        return "Unknown";
    }

    private static DbUser ReadDbUser(System.Data.Common.DbDataReader reader)
    {
        return new DbUser(
            reader.GetString(0),
            reader.GetString(1),
            (DbUserRole)Enum.Parse(typeof(DbUserRole), reader.GetString(2)),
            (DbUserStatus)Enum.Parse(typeof(DbUserStatus), reader.GetString(3)),
            DateTimeOffset.Parse(reader.GetString(4)),
            DateTimeOffset.Parse(reader.GetString(5))
        );
    }
}
