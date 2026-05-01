using System.IO;
using System.Reflection;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

public sealed class DatabaseManagerTests
{
    private sealed class TempDbManagerFixture : IDisposable
    {
        public string TempDir { get; }
        public LocalCatalogDatabase Database { get; }
        public DatabaseManager Manager { get; }

        public TempDbManagerFixture()
        {
            TempDir = Path.Combine(Path.GetTempPath(), $"SmartConDbMgrTest_{Guid.NewGuid():N}");
            Directory.CreateDirectory(TempDir);

            Database = new LocalCatalogDatabase();
            Database.SwitchToPath(Path.Combine(TempDir, "workspace"));

            Manager = new DatabaseManager(Database);

            var field = typeof(DatabaseManager).GetField(
                "_registryPath",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            field.SetValue(Manager, Path.Combine(TempDir, "registry.json"));

            Database.SwitchToPath(Path.Combine(TempDir, "workspace"));
        }

        public async Task<string> CreateStandaloneDatabaseAsync(string name)
        {
            var dbRoot = Path.Combine(TempDir, name);
            var db = new LocalCatalogDatabase();
            db.SwitchToPath(dbRoot);
            var migrator = new LocalCatalogMigrator(db);
            await migrator.MigrateAsync();
            return dbRoot;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(TempDir))
                    Directory.Delete(TempDir, true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task CreateDatabaseAsync_CreatesNewDB()
    {
        using var fixture = new TempDbManagerFixture();
        var dbPath = Path.Combine(fixture.TempDir, "dbs");

        var conn = await fixture.Manager.CreateDatabaseAsync("TestDB", dbPath);

        Assert.NotNull(conn);
        Assert.Equal("TestDB", conn.Name);
        Assert.True(Directory.Exists(conn.Path));
        Assert.True(File.Exists(Path.Combine(conn.Path, "catalog.db")));
    }

    [Fact]
    public async Task CreateDatabaseAsync_SetsAsActive()
    {
        using var fixture = new TempDbManagerFixture();
        var dbPath = Path.Combine(fixture.TempDir, "dbs");

        var conn = await fixture.Manager.CreateDatabaseAsync("TestDB", dbPath);
        var active = fixture.Manager.GetActiveConnection();

        Assert.NotNull(active);
        Assert.Equal(conn.Id, active.Id);
    }

    [Fact]
    public async Task CreateDatabaseAsync_AppearsInListConnections()
    {
        using var fixture = new TempDbManagerFixture();
        var dbPath = Path.Combine(fixture.TempDir, "dbs");

        await fixture.Manager.CreateDatabaseAsync("DB1", dbPath);
        await fixture.Manager.CreateDatabaseAsync("DB2", dbPath);

        var connections = fixture.Manager.ListConnections();
        Assert.Equal(2, connections.Count);
        Assert.Contains(connections, c => c.Name == "DB1");
        Assert.Contains(connections, c => c.Name == "DB2");
    }

    [Fact]
    public async Task ConnectDatabaseAsync_ConnectsExistingDB()
    {
        using var fixture = new TempDbManagerFixture();

        var dbRoot = await fixture.CreateStandaloneDatabaseAsync("standalone");

        var conn = await fixture.Manager.ConnectDatabaseAsync(dbRoot);

        Assert.NotNull(conn);
        Assert.True(File.Exists(Path.Combine(dbRoot, "catalog.db")));
        var connections = fixture.Manager.ListConnections();
        Assert.Single(connections);
        Assert.Equal(dbRoot, conn.Path, ignoreCase: true);
    }

    [Fact]
    public async Task SwitchDatabaseAsync_ChangesActiveDatabase()
    {
        using var fixture = new TempDbManagerFixture();
        var dbPath = Path.Combine(fixture.TempDir, "dbs");

        var conn1 = await fixture.Manager.CreateDatabaseAsync("DB1", dbPath);
        var conn2 = await fixture.Manager.CreateDatabaseAsync("DB2", dbPath);

        Assert.Equal(conn2.Id, fixture.Manager.GetActiveConnection()!.Id);

        var switched = await fixture.Manager.SwitchDatabaseAsync(conn1.Id);

        Assert.True(switched);
        Assert.Equal(conn1.Id, fixture.Manager.GetActiveConnection()!.Id);
    }

    [Fact]
    public async Task SwitchDatabaseAsync_NonExistent_ReturnsFalse()
    {
        using var fixture = new TempDbManagerFixture();

        var switched = await fixture.Manager.SwitchDatabaseAsync("nonexistent");

        Assert.False(switched);
    }

    [Fact]
    public async Task DisconnectDatabaseAsync_RemovesConnection()
    {
        using var fixture = new TempDbManagerFixture();
        var dbPath = Path.Combine(fixture.TempDir, "dbs");

        var conn1 = await fixture.Manager.CreateDatabaseAsync("DB1", dbPath);
        var conn2 = await fixture.Manager.CreateDatabaseAsync("DB2", dbPath);

        var disconnected = await fixture.Manager.DisconnectDatabaseAsync(conn2.Id);

        Assert.True(disconnected);
        var connections = fixture.Manager.ListConnections();
        Assert.Single(connections);
        Assert.Equal(conn1.Id, connections[0].Id);
        Assert.Equal(conn1.Id, fixture.Manager.GetActiveConnection()!.Id);
    }

    [Fact]
    public async Task DisconnectDatabaseAsync_NonExistent_ReturnsFalse()
    {
        using var fixture = new TempDbManagerFixture();

        var disconnected = await fixture.Manager.DisconnectDatabaseAsync("nonexistent");

        Assert.False(disconnected);
    }

    [Fact]
    public async Task DeleteDatabaseAsync_DeletesFilesAndRemovesFromRegistry()
    {
        using var fixture = new TempDbManagerFixture();
        var dbPath = Path.Combine(fixture.TempDir, "dbs");

        var conn1 = await fixture.Manager.CreateDatabaseAsync("DB1", dbPath);
        var conn2 = await fixture.Manager.CreateDatabaseAsync("DB2", dbPath);

        var deleted = await fixture.Manager.DeleteDatabaseAsync(conn1.Id);

        Assert.True(deleted);
        Assert.False(Directory.Exists(conn1.Path));
        var connections = fixture.Manager.ListConnections();
        Assert.Single(connections);
        Assert.Equal(conn2.Id, connections[0].Id);
        Assert.Equal(conn2.Id, fixture.Manager.GetActiveConnection()!.Id);
    }

    [Fact]
    public void GetActiveConnection_NoConnections_ReturnsNull()
    {
        using var fixture = new TempDbManagerFixture();

        var active = fixture.Manager.GetActiveConnection();

        Assert.Null(active);
    }

    [Fact]
    public void GetActiveDatabasePath_NoConnections_ReturnsNull()
    {
        using var fixture = new TempDbManagerFixture();

        var path = fixture.Manager.GetActiveDatabasePath();

        Assert.Null(path);
    }
}
