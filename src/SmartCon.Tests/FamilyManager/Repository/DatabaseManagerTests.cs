using System.IO;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Moq;
using SmartCon.Core.Models;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
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

            var identityMock = new Mock<IUserIdentityService>();
            identityMock.Setup(s => s.GetCurrentUser())
                .Returns(new UserIdentity("test-user", "Test User", "TEST-PC", "test-user"));

            var registryMigratorMock = new Mock<IRegistryMigrator>();
            registryMigratorMock.Setup(m => m.MigrateAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            registryMigratorMock.SetupGet(m => m.LatestSchemaVersion).Returns(1);

            Manager = new DatabaseManager(Database, identityMock.Object, new LocalCatalogMigrator(Database), registryMigratorMock.Object);

            var field = typeof(DatabaseManager).GetField(
                "_registryPath",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            field.SetValue(Manager, Path.Combine(TempDir, "registry.json"));
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
    public async Task CreateDatabaseAsync_WritesMinPluginVersionFloor()
    {
        // ADR-058 (#173): a freshly created database carries the current
        // breaking-format floor from the start (it is written with FHV3
        // hashes), so older plugins connect to it read-only.
        using var fixture = new TempDbManagerFixture();
        var dbPath = Path.Combine(fixture.TempDir, "dbs");

        await fixture.Manager.CreateDatabaseAsync("TestDB", dbPath);

        using var dbConn = fixture.Database.CreateConnection();
        await dbConn.OpenAsync();
        using var cmd = dbConn.CreateCommand();
        cmd.CommandText = "SELECT min_plugin_version FROM database_meta LIMIT 1";
        var value = (string?)(await cmd.ExecuteScalarAsync());
        Assert.Equal(DbCompatibility.CurrentMinPluginVersion, value);
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
    public async Task ConnectDatabaseAsync_AfterDisconnect_RestoresProjectBaseKind()
    {
        using var fixture = new TempDbManagerFixture();
        var dbPath = Path.Combine(fixture.TempDir, "dbs");

        var template = new FileNameTemplate
        {
            Blocks =
            [
                new FileBlockDefinition { Index = 0, Field = "project", ParseRule = new ParseRule { Mode = ParseMode.DelimiterSegment, Delimiter = "-", SegmentIndex = 1 } }
            ]
        };
        var fieldLibrary = new List<FieldDefinition> { new() { Name = "project" } };
        var binding = new ProjectBaseBinding(template, fieldLibrary);

        var projectDb = await fixture.Manager.CreateProjectDatabaseAsync("ProjectDB", dbPath, binding);
        Assert.Equal(BaseType.Project, projectDb.Kind);

        var otherDb = await fixture.Manager.CreateDatabaseAsync("OtherDB", dbPath);

        await fixture.Manager.DisconnectDatabaseAsync(projectDb.Id);

        var reconnected = await fixture.Manager.ConnectDatabaseAsync(projectDb.Path);

        Assert.Equal(BaseType.Project, reconnected.Kind);
        Assert.NotNull(reconnected.ProjectBinding);
        Assert.Single(reconnected.ProjectBinding.Template.Blocks);
        Assert.Equal("project", reconnected.ProjectBinding.Template.Blocks[0].Field);
        Assert.Single(reconnected.ProjectBinding.FieldLibrary);
        Assert.Equal("project", reconnected.ProjectBinding.FieldLibrary[0].Name);
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
    public async Task DeleteDatabaseAsync_WhenFilesLocked_RemovesFromRegistryAndThrowsInformativeException()
    {
        using var fixture = new TempDbManagerFixture();
        var dbPath = Path.Combine(fixture.TempDir, "dbs");

        var conn1 = await fixture.Manager.CreateDatabaseAsync("DB1", dbPath);
        var conn2 = await fixture.Manager.CreateDatabaseAsync("DB2", dbPath);

        var lockedFile = Path.Combine(conn1.Path, "locked.rfa");
        await File.WriteAllTextAsync(lockedFile, "locked");
        var stream = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager.DeleteDatabaseAsync(conn1.Id));
            Assert.Contains("удалена из списка, но файлы не удалены", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            stream.Dispose();
        }

        var connections = fixture.Manager.ListConnections();
        Assert.Single(connections);
        Assert.Equal(conn2.Id, connections[0].Id);
        Assert.Equal(conn2.Id, fixture.Manager.GetActiveConnection()!.Id);
    }

    [Fact]
    public async Task DeleteDatabaseAsync_WithReadOnlyFiles_DeletesDirectory()
    {
        using var fixture = new TempDbManagerFixture();
        var dbPath = Path.Combine(fixture.TempDir, "dbs");

        var conn1 = await fixture.Manager.CreateDatabaseAsync("DB1", dbPath);
        var conn2 = await fixture.Manager.CreateDatabaseAsync("DB2", dbPath);

        var filesDir = Path.Combine(conn1.Path, "files");
        Directory.CreateDirectory(filesDir);
        var rfaFile = Path.Combine(filesDir, "family.rfa");
        await File.WriteAllTextAsync(rfaFile, "rfa");
        File.SetAttributes(rfaFile, File.GetAttributes(rfaFile) | FileAttributes.ReadOnly);

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

    [Fact]
    public async Task CreateProjectDatabaseAsync_CreatesProjectBase()
    {
        using var fixture = new TempDbManagerFixture();
        var dbPath = Path.Combine(fixture.TempDir, "dbs");
        var binding = new ProjectBaseBinding(
            new FileNameTemplate
            {
                Blocks =
                [
                    new() { Index = 0, Field = "project", ParseRule = ParseRule.DefaultDelimiter("-", 1) }
                ]
            },
            [new FieldDefinition { Name = "project", ValidationMode = ValidationMode.None }]);

        var conn = await fixture.Manager.CreateProjectDatabaseAsync("ProjectDB", dbPath, binding);

        Assert.NotNull(conn);
        Assert.Equal(BaseType.Project, conn.Kind);
        Assert.NotNull(conn.ProjectBinding);
        Assert.Single(conn.ProjectBinding.Template.Blocks);

        var active = fixture.Manager.GetActiveConnection();
        Assert.NotNull(active);
        Assert.Equal(conn.Id, active.Id);
        Assert.Equal(BaseType.Project, active.Kind);
    }

    [Fact]
    public async Task CreateProjectDatabaseAsync_SetsCachedBaseTypeToProject()
    {
        using var fixture = new TempDbManagerFixture();
        var dbPath = Path.Combine(fixture.TempDir, "dbs");
        var binding = new ProjectBaseBinding(
            new FileNameTemplate { Blocks = [new() { Index = 0, Field = "project", ParseRule = ParseRule.DefaultDelimiter("-", 1) }] },
            []);

        var conn = await fixture.Manager.CreateProjectDatabaseAsync("ProjectDB", dbPath, binding);

        using var dbConn = fixture.Database.CreateConnection();
        await dbConn.OpenAsync();
        using var cmd = dbConn.CreateCommand();
        cmd.CommandText = "SELECT base_type FROM database_meta LIMIT 1";
        var baseType = (long?)(await cmd.ExecuteScalarAsync());
        Assert.Equal(1L, baseType);
    }

    [Fact]
    public async Task ConfigureProjectBaseAsync_UpdatesExistingGeneralToProject()
    {
        using var fixture = new TempDbManagerFixture();
        var dbPath = Path.Combine(fixture.TempDir, "dbs");
        var conn = await fixture.Manager.CreateDatabaseAsync("GeneralDB", dbPath);
        Assert.Equal(BaseType.General, conn.Kind);

        var binding = new ProjectBaseBinding(
            new FileNameTemplate { Blocks = [new() { Index = 0, Field = "project", ParseRule = ParseRule.DefaultDelimiter("-", 1) }] },
            []);

        var updated = await fixture.Manager.ConfigureProjectBaseAsync(conn.Id, binding);

        Assert.Equal(BaseType.Project, updated.Kind);
        Assert.NotNull(updated.ProjectBinding);
        var listed = fixture.Manager.ListConnections().First(c => c.Id == conn.Id);
        Assert.Equal(BaseType.Project, listed.Kind);
    }

    [Fact]
    public async Task ConfigureProjectBaseAsync_UpdatesCachedBaseTypeOnNonActiveDatabase()
    {
        using var fixture = new TempDbManagerFixture();
        var dbPath = Path.Combine(fixture.TempDir, "dbs");
        var conn1 = await fixture.Manager.CreateDatabaseAsync("ActiveDB", dbPath);
        var conn2 = await fixture.Manager.CreateDatabaseAsync("TargetDB", dbPath);

        await fixture.Manager.SwitchDatabaseAsync(conn1.Id);
        Assert.Equal(conn1.Id, fixture.Manager.GetActiveConnection()!.Id);

        var binding = new ProjectBaseBinding(
            new FileNameTemplate { Blocks = [new() { Index = 0, Field = "project", ParseRule = ParseRule.DefaultDelimiter("-", 1) }] },
            []);

        await fixture.Manager.ConfigureProjectBaseAsync(conn2.Id, binding);

        fixture.Database.SwitchToPath(conn2.Path);
        using var dbConn = fixture.Database.CreateConnection();
        await dbConn.OpenAsync();
        using var cmd = dbConn.CreateCommand();
        cmd.CommandText = "SELECT base_type FROM database_meta LIMIT 1";
        var baseType = (long?)(await cmd.ExecuteScalarAsync());
        Assert.Equal(1L, baseType);
    }

    [Fact]
    public async Task ConnectDatabaseAsync_FromEmptyRegistry_ConnectsSuccessfully()
    {
        using var fixture = new TempDbManagerFixture();

        var dbRoot = await fixture.CreateStandaloneDatabaseAsync("standalone_clean");

        var conn = await fixture.Manager.ConnectDatabaseAsync(dbRoot);

        Assert.NotNull(conn);
        Assert.Equal(dbRoot, conn.Path, ignoreCase: true);
        var connections = fixture.Manager.ListConnections();
        Assert.Single(connections);
    }

    [Fact]
    public async Task ConnectDatabaseAsync_PathWithoutCatalogDb_ThrowsFileNotFound()
    {
        using var fixture = new TempDbManagerFixture();

        var emptyDir = Path.Combine(fixture.TempDir, "empty_db_folder");
        Directory.CreateDirectory(emptyDir);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => fixture.Manager.ConnectDatabaseAsync(emptyDir));
    }

    [Fact]
    public async Task DisconnectDatabaseAsync_LastConnection_SucceedsAndClearsActive()
    {
        using var fixture = new TempDbManagerFixture();
        var conn = await fixture.Manager.CreateDatabaseAsync("TestDB", Path.Combine(fixture.TempDir, "dbs"));

        var result = await fixture.Manager.DisconnectDatabaseAsync(conn.Id);

        Assert.True(result);
        Assert.Null(fixture.Manager.GetActiveConnection());
        Assert.Empty(fixture.Manager.ListConnections());
        Assert.True(Directory.Exists(conn.Path));
    }

    [Fact]
    public async Task DeleteDatabaseAsync_LastConnection_SucceedsAndClearsActive()
    {
        using var fixture = new TempDbManagerFixture();
        var conn = await fixture.Manager.CreateDatabaseAsync("TestDB", Path.Combine(fixture.TempDir, "dbs"));

        var result = await fixture.Manager.DeleteDatabaseAsync(conn.Id);

        Assert.True(result);
        Assert.Null(fixture.Manager.GetActiveConnection());
        Assert.Empty(fixture.Manager.ListConnections());
        Assert.False(Directory.Exists(conn.Path));
    }

    [Fact]
    public async Task DeleteDatabaseAsync_LastConnection_RaisesEventWithNull()
    {
        using var fixture = new TempDbManagerFixture();
        var conn = await fixture.Manager.CreateDatabaseAsync("TestDB", Path.Combine(fixture.TempDir, "dbs"));

        string? captured = "not-null-marker";
        fixture.Manager.ActiveDatabaseChanged += (_, id) => captured = id;

        await fixture.Manager.DeleteDatabaseAsync(conn.Id);

        Assert.Null(captured);
    }

    [Fact]
    public async Task ConvertToGeneralBaseAsync_UpdatesExistingProjectToGeneral()
    {
        using var fixture = new TempDbManagerFixture();
        var dbPath = Path.Combine(fixture.TempDir, "dbs");
        var binding = new ProjectBaseBinding(
            new FileNameTemplate { Blocks = [new() { Index = 0, Field = "project", ParseRule = ParseRule.DefaultDelimiter("-", 1) }] },
            []);
        var conn = await fixture.Manager.CreateProjectDatabaseAsync("ProjectDB", dbPath, binding);
        Assert.Equal(BaseType.Project, conn.Kind);

        var updated = await fixture.Manager.ConvertToGeneralBaseAsync(conn.Id);

        Assert.Equal(BaseType.General, updated.Kind);
        Assert.Null(updated.ProjectBinding);
        var listed = fixture.Manager.ListConnections().First(c => c.Id == conn.Id);
        Assert.Equal(BaseType.General, listed.Kind);
        Assert.Null(listed.ProjectBinding);

        using var dbConn = fixture.Database.CreateConnection();
        await dbConn.OpenAsync();
        using var cmd = dbConn.CreateCommand();
        cmd.CommandText = "SELECT base_type, project_binding_json FROM database_meta LIMIT 1";
        using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0L, reader.GetInt64(0));
        Assert.True(await reader.IsDBNullAsync(1));
    }

    [Fact]
    public async Task ConvertToGeneralBaseAsync_ClearsCachedBaseTypeOnNonActiveDatabase()
    {
        using var fixture = new TempDbManagerFixture();
        var dbPath = Path.Combine(fixture.TempDir, "dbs");
        var conn1 = await fixture.Manager.CreateDatabaseAsync("ActiveDB", dbPath);
        var binding = new ProjectBaseBinding(
            new FileNameTemplate { Blocks = [new() { Index = 0, Field = "project", ParseRule = ParseRule.DefaultDelimiter("-", 1) }] },
            []);
        var conn2 = await fixture.Manager.CreateProjectDatabaseAsync("TargetProjectDB", dbPath, binding);

        await fixture.Manager.SwitchDatabaseAsync(conn1.Id);
        Assert.Equal(conn1.Id, fixture.Manager.GetActiveConnection()!.Id);

        await fixture.Manager.ConvertToGeneralBaseAsync(conn2.Id);

        fixture.Database.SwitchToPath(conn2.Path);
        using var dbConn = fixture.Database.CreateConnection();
        await dbConn.OpenAsync();
        using var cmd = dbConn.CreateCommand();
        cmd.CommandText = "SELECT base_type, project_binding_json FROM database_meta LIMIT 1";
        using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0L, reader.GetInt64(0));
        Assert.True(await reader.IsDBNullAsync(1));

        Assert.Equal(conn1.Id, fixture.Manager.GetActiveConnection()!.Id);
    }

    [Fact]
    public async Task ConvertToGeneralBaseAsync_WhenAlreadyGeneral_NoOp()
    {
        using var fixture = new TempDbManagerFixture();
        var dbPath = Path.Combine(fixture.TempDir, "dbs");
        var conn = await fixture.Manager.CreateDatabaseAsync("GeneralDB", dbPath);
        Assert.Equal(BaseType.General, conn.Kind);

        var updated = await fixture.Manager.ConvertToGeneralBaseAsync(conn.Id);

        Assert.Equal(BaseType.General, updated.Kind);
        Assert.Null(updated.ProjectBinding);
        Assert.Equal(conn.Id, updated.Id);
        var listed = fixture.Manager.ListConnections().First(c => c.Id == conn.Id);
        Assert.Equal(BaseType.General, listed.Kind);
    }

    [Fact]
    public async Task ConfigureProjectBaseAsync_UnknownConnection_Throws()
    {
        using var fixture = new TempDbManagerFixture();
        var binding = new ProjectBaseBinding(
            new FileNameTemplate { Blocks = [new() { Index = 0, Field = "project", ParseRule = ParseRule.DefaultDelimiter("-", 1) }] },
            []);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Manager.ConfigureProjectBaseAsync("nonexistent", binding));
    }

    [Fact]
    public async Task ConvertToGeneralBaseAsync_UnknownConnection_Throws()
    {
        using var fixture = new TempDbManagerFixture();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Manager.ConvertToGeneralBaseAsync("nonexistent"));
    }

    [Fact]
    public async Task ConnectDatabaseAsync_AfterConvertToGeneral_RestoresGeneralKind()
    {
        using var fixture = new TempDbManagerFixture();
        var dbPath = Path.Combine(fixture.TempDir, "dbs");
        var binding = new ProjectBaseBinding(
            new FileNameTemplate { Blocks = [new() { Index = 0, Field = "project", ParseRule = ParseRule.DefaultDelimiter("-", 1) }] },
            [new FieldDefinition { Name = "project", ValidationMode = ValidationMode.None }]);
        var projectDb = await fixture.Manager.CreateProjectDatabaseAsync("ProjectDB", dbPath, binding);
        Assert.Equal(BaseType.Project, projectDb.Kind);

        await fixture.Manager.ConvertToGeneralBaseAsync(projectDb.Id);
        await fixture.Manager.DisconnectDatabaseAsync(projectDb.Id);

        var reconnected = await fixture.Manager.ConnectDatabaseAsync(projectDb.Path);

        Assert.Equal(BaseType.General, reconnected.Kind);
        Assert.Null(reconnected.ProjectBinding);
    }

    [Fact]
    public async Task ConcurrentRegistryMutations_RegistryStaysValidAndConsistent()
    {
        // #171: pre-fix, concurrent SaveRegistryAsync calls collided on
        // registry.json.tmp (IOException) and could corrupt registry.json.
        // The operation-level lock must serialize every read-modify-write.
        using var fixture = new TempDbManagerFixture();
        var dbPath = Path.Combine(fixture.TempDir, "dbs");
        var connA = await fixture.Manager.CreateDatabaseAsync("DB-A", dbPath);
        var connB = await fixture.Manager.CreateDatabaseAsync("DB-B", dbPath);
        var binding = new ProjectBaseBinding(
            new FileNameTemplate { Blocks = [new() { Index = 0, Field = "project", ParseRule = ParseRule.DefaultDelimiter("-", 1) }] },
            []);

        var tasks = Enumerable.Range(0, 30).Select(i => (i % 3) switch
        {
            0 => (Task)fixture.Manager.SwitchDatabaseAsync(i % 2 == 0 ? connA.Id : connB.Id),
            1 => fixture.Manager.ConfigureProjectBaseAsync(connB.Id, binding),
            _ => fixture.Manager.ConvertToGeneralBaseAsync(connB.Id),
        }).ToArray();

        await Task.WhenAll(tasks);

        var registryPath = Path.Combine(fixture.TempDir, "registry.json");
        var json = await File.ReadAllTextAsync(registryPath);
        Assert.False(string.IsNullOrWhiteSpace(json));
        var connections = fixture.Manager.ListConnections();
        Assert.Equal(2, connections.Count);
        var active = fixture.Manager.GetActiveConnection();
        Assert.NotNull(active);
        Assert.Contains(connections, c => c.Id == active.Id);
    }
}
