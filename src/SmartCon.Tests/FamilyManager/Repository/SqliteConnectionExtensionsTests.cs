using Microsoft.Data.Sqlite;
using SmartCon.Core.Data;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

public sealed class SqliteConnectionExtensionsTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public SqliteConnectionExtensionsTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.OpenAsync().GetAwaiter().GetResult();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task ExecuteAsync_InsertAndCount_Works()
    {
        await _connection.ExecuteAsync("CREATE TABLE t (id INTEGER, name TEXT)");

        var rows = await _connection.ExecuteAsync(
            "INSERT INTO t (id, name) VALUES (@id, @name)",
            new SqliteParameter[]
            {
                new("@id", 1),
                new("@name", "first")
            });
        rows += await _connection.ExecuteAsync(
            "INSERT INTO t (id, name) VALUES (@id, @name)",
            new SqliteParameter[]
            {
                new("@id", 2),
                new("@name", "second")
            });

        Assert.Equal(2, rows);

        var count = await _connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM t");
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task ExecuteScalarAsync_NullValue_ReturnsDefault()
    {
        await _connection.ExecuteAsync("CREATE TABLE t (id INTEGER PRIMARY KEY, val TEXT)");

        var result = await _connection.ExecuteScalarAsync<string>("SELECT val FROM t WHERE id = 999");

        Assert.Null(result);
    }

    [Fact]
    public async Task QueryAsync_ReadsAllRows()
    {
        await _connection.ExecuteAsync("CREATE TABLE t (id INTEGER, name TEXT)");
        for (var i = 0; i < 3; i++)
        {
            await _connection.ExecuteAsync(
                "INSERT INTO t (id, name) VALUES (@id, @name)",
                new SqliteParameter[]
                {
                    new("@id", i),
                    new("@name", $"name-{i}")
                });
        }

        var rows = await _connection.QueryAsync<(int Id, string Name)>(
            "SELECT id, name FROM t ORDER BY id",
            r => (r.GetInt32(0), r.GetString(1)));

        Assert.Equal(3, rows.Count);
        Assert.Equal(("name-0", "name-1", "name-2"), (rows[0].Name, rows[1].Name, rows[2].Name));
    }

    [Fact]
    public async Task QuerySingleOrDefaultAsync_NoRows_ReturnsDefault()
    {
        await _connection.ExecuteAsync("CREATE TABLE t (id INTEGER PRIMARY KEY)");

        var result = await _connection.QuerySingleOrDefaultAsync<int>(
            "SELECT id FROM t WHERE id = 999",
            r => r.GetInt32(0));

        Assert.Equal(0, result);
    }

    [Fact]
    public async Task QuerySingleOrDefaultAsync_OneRow_ReturnsValue()
    {
        await _connection.ExecuteAsync("CREATE TABLE t (id INTEGER, name TEXT)");
        await _connection.ExecuteAsync(
            "INSERT INTO t (id, name) VALUES (@id, @name)",
            new SqliteParameter[]
            {
                new("@id", 1),
                new("@name", "the one")
            });

        var result = await _connection.QuerySingleOrDefaultAsync<string>(
            "SELECT name FROM t WHERE id = 1",
            r => r.GetString(0));

        Assert.Equal("the one", result);
    }

    [Fact]
    public void ExecuteAsync_NullConnection_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => SqliteConnectionExtensions.ExecuteAsync(null!, "SELECT 1").GetAwaiter().GetResult());
    }

    [Fact]
    public void ExecuteAsync_EmptySql_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => SqliteConnectionExtensions.ExecuteAsync(_connection, "").GetAwaiter().GetResult());
    }
}
