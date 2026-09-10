using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

public sealed class LocalCatalogDatabaseWriteAccessTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"SmartConTest_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task ReadOnlyMode_AllowsReadsAndBlocksWrites()
    {
        var database = new LocalCatalogDatabase();
        database.SwitchToPath(_tempDir);
        await new LocalCatalogMigrator(database).MigrateAsync();

        database.SetWriteAccess(false);
        try
        {
            using (var connection = database.CreateConnection())
            {
                await connection.OpenAsync();
                using var readCmd = connection.CreateCommand();
                readCmd.CommandText = "SELECT COUNT(*) FROM schema_info";
                var count = await readCmd.ExecuteScalarAsync();
                Assert.NotNull(count);

                using var writeCmd = connection.CreateCommand();
                writeCmd.CommandText = "INSERT INTO categories (id, name, sort_order, created_at_utc) VALUES ('x', 'x', 0, '2026-01-01')";
                var ex = await Assert.ThrowsAsync<SqliteException>(() => writeCmd.ExecuteNonQueryAsync());
                Assert.Equal(8, ex.SqliteErrorCode);
            }

            using (var writable = database.CreateWritableConnection())
            {
                await writable.OpenAsync();
                using var cmd = writable.CreateCommand();
                cmd.CommandText = "INSERT INTO categories (id, name, sort_order, created_at_utc) VALUES ('x', 'x', 0, '2026-01-01')";
                await cmd.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            database.SetWriteAccess(true);
        }
    }

    [Fact]
    public void SwitchToPath_ResetsWriteAccessToWritable()
    {
        var database = new LocalCatalogDatabase();
        database.SwitchToPath(_tempDir);
        database.SetWriteAccess(false);

        database.SwitchToPath(_tempDir);

        using var connection = database.CreateConnection();
        Assert.DoesNotContain("Mode=ReadOnly", connection.ConnectionString);
    }
}
