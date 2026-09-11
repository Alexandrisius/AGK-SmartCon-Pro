using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Moq;
using SmartCon.Core.Models;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.Cloud;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

/// <summary>
/// CloudLink-механика DatabaseManager (master plan §7.3.1, срез v1): registry
/// v2 persistence, database_meta.remote_source_json dual-write (Published),
/// registry-only для Subscribed (аплаер — writer копии), self-heal из
/// database_meta после «даунгрейд-перезаписи» registry.json.
/// </summary>
public sealed class DatabaseManagerCloudLinkTests
{
    private sealed class TempDbManagerFixture : IDisposable
    {
        public string TempDir { get; }
        public LocalCatalogDatabase Database { get; }
        public DatabaseManager Manager { get; }
        public CloudDatabaseGate Gate { get; }

        public TempDbManagerFixture()
        {
            TempDir = Path.Combine(Path.GetTempPath(), $"SmartConCloudLinkTest_{Guid.NewGuid():N}");
            Directory.CreateDirectory(TempDir);

            Database = new LocalCatalogDatabase();
            Gate = new CloudDatabaseGate();

            var identityMock = new Mock<IUserIdentityService>();
            identityMock.Setup(s => s.GetCurrentUser())
                .Returns(new UserIdentity("test-user", "Test User", "TEST-PC", "test-user"));

            var registryMigratorMock = new Mock<IRegistryMigrator>();
            registryMigratorMock.Setup(m => m.MigrateAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            registryMigratorMock.SetupGet(m => m.LatestSchemaVersion).Returns(2);

            Manager = new DatabaseManager(Database, identityMock.Object, new LocalCatalogMigrator(Database), registryMigratorMock.Object, Gate);

            var field = typeof(DatabaseManager).GetField(
                "_registryPath",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            field.SetValue(Manager, Path.Combine(TempDir, "registry.json"));
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

    private static async Task<string?> ReadRemoteSourceAsync(LocalCatalogDatabase database, string dbRoot)
    {
        database.SwitchToPath(dbRoot);
        using var connection = database.CreateConnection();
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT remote_source_json FROM database_meta LIMIT 1";
        return await cmd.ExecuteScalarAsync() as string;
    }

    private static async Task WriteRemoteSourceAsync(LocalCatalogDatabase database, string dbRoot, string json)
    {
        database.SwitchToPath(dbRoot);
        using var connection = database.CreateWritableConnection();
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE database_meta SET remote_source_json = @json";
        cmd.Parameters.Add(new SqliteParameter("@json", json));
        await cmd.ExecuteNonQueryAsync();
    }

    private static CloudLink MakeLink(CloudLinkRole role, long? seq = null) =>
        new(role, "http://127.0.0.1:8787", "6f1d2c90a4b34e7f8b5c1d2e3f4a5b6c", "otvody", seq);

    [Fact]
    public async Task SetCloudLinkAsync_Published_PersistsRegistryAndRemoteSource()
    {
        using var fixture = new TempDbManagerFixture();
        var dbPath = Path.Combine(fixture.TempDir, "dbs");
        var conn = await fixture.Manager.CreateDatabaseAsync("CloudDB", dbPath);
        var link = MakeLink(CloudLinkRole.Published, 7);

        var updated = await fixture.Manager.SetCloudLinkAsync(conn.Id, link);

        Assert.NotNull(updated);
        Assert.Equal(link, updated.CloudLink);
        var listed = fixture.Manager.ListConnections().Single(c => c.Id == conn.Id);
        Assert.Equal(CloudLinkRole.Published, listed.CloudLink?.Role);
        Assert.Equal("otvody", listed.CloudLink?.Slug);
        Assert.Equal(7, listed.CloudLink?.LastSyncedPublishSeq);

        // Dual-write: database_meta.remote_source_json mirrors the link.
        var json = await ReadRemoteSourceAsync(fixture.Database, conn.Path);
        Assert.NotNull(json);
        var healed = CloudLinkJson.TryDeserialize(json);
        Assert.NotNull(healed);
        Assert.Equal(CloudLinkRole.Published, healed.Role);
        Assert.Equal("otvody", healed.Slug);

        // registry.json on disk carries the camelCase cloudLink block.
        var registryJson = await File.ReadAllTextAsync(
            (string)typeof(DatabaseManager).GetField("_registryPath", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(fixture.Manager)!);
        Assert.Contains("\"cloudLink\"", registryJson);
        Assert.Contains("\"slug\": \"otvody\"", registryJson);
    }

    [Fact]
    public async Task SetCloudLinkAsync_Subscribed_RegistryOnly_ApplierOwnsDatabaseSide()
    {
        using var fixture = new TempDbManagerFixture();
        var dbPath = Path.Combine(fixture.TempDir, "dbs");
        var conn = await fixture.Manager.CreateDatabaseAsync("SubDB", dbPath);

        // Симулируем аплаер: remote_source_json уже в копии.
        var applierJson = CloudLinkJson.Serialize(MakeLink(CloudLinkRole.Subscribed, 3));
        await WriteRemoteSourceAsync(fixture.Database, conn.Path, applierJson);

        var updated = await fixture.Manager.SetCloudLinkAsync(conn.Id, MakeLink(CloudLinkRole.Subscribed, 3));

        Assert.Equal(CloudLinkRole.Subscribed, updated!.CloudLink?.Role);
        var after = await ReadRemoteSourceAsync(fixture.Database, conn.Path);
        Assert.Equal(applierJson, after);
    }

    [Fact]
    public async Task SetCloudLinkAsync_Clear_RemovesRegistryLinkAndNullsRemoteSource()
    {
        using var fixture = new TempDbManagerFixture();
        var dbPath = Path.Combine(fixture.TempDir, "dbs");
        var conn = await fixture.Manager.CreateDatabaseAsync("CloudDB", dbPath);
        await fixture.Manager.SetCloudLinkAsync(conn.Id, MakeLink(CloudLinkRole.Published));

        var updated = await fixture.Manager.SetCloudLinkAsync(conn.Id, null);

        Assert.Null(updated!.CloudLink);
        var json = await ReadRemoteSourceAsync(fixture.Database, conn.Path);
        Assert.True(string.IsNullOrEmpty(json));
    }

    [Fact]
    public async Task SetCloudLinkAsync_UnknownConnection_ReturnsNull()
    {
        using var fixture = new TempDbManagerFixture();
        var result = await fixture.Manager.SetCloudLinkAsync("nonexistent", MakeLink(CloudLinkRole.Published));
        Assert.Null(result);
    }

    [Fact]
    public async Task SetCloudLinkAsync_OnActiveDatabase_UpdatesGate()
    {
        using var fixture = new TempDbManagerFixture();
        var dbPath = Path.Combine(fixture.TempDir, "dbs");
        var conn = await fixture.Manager.CreateDatabaseAsync("CloudDB", dbPath);

        await fixture.Manager.SetCloudLinkAsync(conn.Id, MakeLink(CloudLinkRole.Subscribed));

        Assert.True(fixture.Gate.IsWriteBlocked);
        Assert.Equal("otvody", fixture.Gate.ActiveLink?.Slug);
    }

    [Fact]
    public async Task SwitchDatabaseAsync_SubscribedActive_UpdatesGate()
    {
        using var fixture = new TempDbManagerFixture();
        var dbPath = Path.Combine(fixture.TempDir, "dbs");
        var conn1 = await fixture.Manager.CreateDatabaseAsync("LocalDB", dbPath);
        var conn2 = await fixture.Manager.CreateDatabaseAsync("CloudDB", dbPath);
        await fixture.Manager.SetCloudLinkAsync(conn2.Id, MakeLink(CloudLinkRole.Subscribed));

        await fixture.Manager.SwitchDatabaseAsync(conn1.Id);
        Assert.False(fixture.Gate.IsWriteBlocked);

        await fixture.Manager.SwitchDatabaseAsync(conn2.Id);
        Assert.True(fixture.Gate.IsWriteBlocked);
    }

    [Fact]
    public async Task CloudLink_WipedFromRegistryByDowngrade_SelfHealsOnSwitch()
    {
        // E27: старая версия плагина перезаписала registry.json без cloudLink —
        // ссылка восстанавливается из database_meta.remote_source_json.
        using var fixture = new TempDbManagerFixture();
        var dbPath = Path.Combine(fixture.TempDir, "dbs");
        var cloudConn = await fixture.Manager.CreateDatabaseAsync("CloudDB", dbPath);
        var otherConn = await fixture.Manager.CreateDatabaseAsync("OtherDB", dbPath);
        await fixture.Manager.SetCloudLinkAsync(cloudConn.Id, MakeLink(CloudLinkRole.Published, 5));

        // «Даунгрейд»: убираем cloudLink из registry.json вручную.
        var registryPath = (string)typeof(DatabaseManager).GetField("_registryPath", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(fixture.Manager)!;
        var root = JsonNode.Parse(await File.ReadAllTextAsync(registryPath))!;
        foreach (var node in root["connections"]!.AsArray())
        {
            ((JsonObject)node!).Remove("cloudLink");
        }
        await File.WriteAllTextAsync(registryPath, root.ToJsonString());

        // Переключаемся туда-обратно: heal срабатывает при активации.
        await fixture.Manager.SwitchDatabaseAsync(otherConn.Id);
        Assert.Null(fixture.Manager.ListConnections().Single(c => c.Id == cloudConn.Id).CloudLink);

        await fixture.Manager.SwitchDatabaseAsync(cloudConn.Id);
        var healed = fixture.Manager.ListConnections().Single(c => c.Id == cloudConn.Id);
        Assert.NotNull(healed.CloudLink);
        Assert.Equal(CloudLinkRole.Published, healed.CloudLink.Role);
        Assert.Equal("otvody", healed.CloudLink.Slug);
        Assert.Equal(5, healed.CloudLink.LastSyncedPublishSeq);
        Assert.Equal("otvody", fixture.Gate.ActiveLink?.Slug);
    }

    [Fact]
    public async Task SetCloudLinkAsync_ClearOnActiveDatabase_ClearsGate()
    {
        using var fixture = new TempDbManagerFixture();
        var dbPath = Path.Combine(fixture.TempDir, "dbs");
        var conn = await fixture.Manager.CreateDatabaseAsync("CloudDB", dbPath);
        await fixture.Manager.SetCloudLinkAsync(conn.Id, MakeLink(CloudLinkRole.Subscribed));
        Assert.True(fixture.Gate.IsWriteBlocked);

        await fixture.Manager.SetCloudLinkAsync(conn.Id, null);
        Assert.False(fixture.Gate.IsWriteBlocked);
    }
}

/// <summary>RegistryMigrator v1→v2: пер-коннекшен ключ cloudLink + bump schemaVersion.
/// Пути редиректятся reflection'ом в temp — реальный %APPDATA%\registry.json не трогаем.</summary>
public sealed class RegistryMigratorV2Tests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"SmartConRegMigTest_{Guid.NewGuid():N}");

    private RegistryMigrator MakeRedirectedMigrator()
    {
        Directory.CreateDirectory(_tempDir);
        var migrator = new RegistryMigrator();
        typeof(RegistryMigrator).GetField("_registryPath", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(migrator, Path.Combine(_tempDir, "registry.json"));
        typeof(RegistryMigrator).GetField("_tempPath", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(migrator, Path.Combine(_tempDir, "registry.json.tmp"));
        typeof(RegistryMigrator).GetField("_bakPath", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(migrator, Path.Combine(_tempDir, "registry.json.bak"));
        return migrator;
    }

    [Fact]
    public async Task MigrateAsync_V1Registry_AddsCloudLinkKeys()
    {
        var migrator = MakeRedirectedMigrator();
        var registryPath = Path.Combine(_tempDir, "registry.json");

        var v1Json = """
            {
              "schemaVersion": 1,
              "activeConnectionId": "abc",
              "connections": [
                { "id": "abc", "name": "DB", "path": "C:\\dbs\\db", "createdAtUtc": "2026-01-01T00:00:00Z", "kind": "General" }
              ]
            }
            """;
        await File.WriteAllTextAsync(registryPath, v1Json);

        await migrator.MigrateAsync();

            var migrated = JsonNode.Parse(await File.ReadAllTextAsync(registryPath))!;
            Assert.Equal(2, (int)migrated["schemaVersion"]!);
            var connObj = (JsonObject)migrated["connections"]![0]!;
            Assert.True(connObj.ContainsKey("cloudLink"));
    }

    [Fact]
    public async Task MigrateAsync_V0Registry_AddsAllKeys()
    {
        var migrator = MakeRedirectedMigrator();
        var registryPath = Path.Combine(_tempDir, "registry.json");

        var v0Json = """
            {
              "activeConnectionId": null,
              "connections": []
            }
            """;
        await File.WriteAllTextAsync(registryPath, v0Json);

        await migrator.MigrateAsync();

        var migrated = JsonNode.Parse(await File.ReadAllTextAsync(registryPath))!;
        Assert.Equal(2, (int)migrated["schemaVersion"]!);
    }

    [Fact]
    public async Task MigrateAsync_AlreadyV2_NoRewrite()
    {
        var migrator = MakeRedirectedMigrator();
        var registryPath = Path.Combine(_tempDir, "registry.json");

        var v2Json = """
            {
              "schemaVersion": 2,
              "activeConnectionId": null,
              "connections": []
            }
            """;
        await File.WriteAllTextAsync(registryPath, v2Json);
        var before = File.GetLastWriteTimeUtc(registryPath);

        await migrator.MigrateAsync();

        Assert.Equal(before, File.GetLastWriteTimeUtc(registryPath));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        }
        catch
        {
        }
    }
}
