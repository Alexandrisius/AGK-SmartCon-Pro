using System.IO;
using System.Text.Json;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.UI;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed class DatabaseManager : IDatabaseManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly LocalCatalogDatabase _catalogDatabase;
    private readonly IUserIdentityService _identityService;
    private readonly ILocalCatalogMigrator _migrator;
    private readonly string _registryPath;

    public DatabaseManager(
        LocalCatalogDatabase catalogDatabase,
        IUserIdentityService identityService,
        ILocalCatalogMigrator migrator)
    {
        _catalogDatabase = catalogDatabase;
        _identityService = identityService;
        _migrator = migrator;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var fmDir = Path.Combine(appData, "SmartCon", "FamilyManager");
        Directory.CreateDirectory(fmDir);
        _registryPath = Path.Combine(fmDir, "registry.json");

        var registry = LoadRegistry();
        if (registry.ActiveConnectionId is not null)
        {
            var active = registry.Connections.FirstOrDefault(c => c.Id == registry.ActiveConnectionId);
            if (active is not null)
            {
                _catalogDatabase.SwitchToPath(active.Path);
            }
        }
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var registry = LoadRegistry();
        if (registry.ActiveConnectionId is not null)
        {
            var active = registry.Connections.FirstOrDefault(c => c.Id == registry.ActiveConnectionId);
            if (active is not null)
            {
                _catalogDatabase.SwitchToPath(active.Path);
                await _migrator.MigrateAsync(ct);
            }
        }
    }

    public event EventHandler<string>? ActiveDatabaseChanged;

    public IReadOnlyList<DatabaseConnection> ListConnections()
    {
        var registry = LoadRegistry();
        return registry.Connections;
    }

    public DatabaseConnection? GetActiveConnection()
    {
        var registry = LoadRegistry();
        if (registry.ActiveConnectionId is null) return null;
        return registry.Connections.FirstOrDefault(c => c.Id == registry.ActiveConnectionId);
    }

    public string? GetActiveDatabasePath()
    {
        return GetActiveConnection()?.Path;
    }

    public async Task<DatabaseConnection> CreateDatabaseAsync(string name, string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Database name cannot be empty.", nameof(name));
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Database path cannot be empty.", nameof(path));

        var id = Guid.NewGuid().ToString("N");
        var dbRoot = Path.GetFullPath(Path.Combine(path, name.Trim()));
        await Task.Run(() => Directory.CreateDirectory(dbRoot), ct);

        var connection = new DatabaseConnection(id, name.Trim(), dbRoot, DateTimeOffset.UtcNow);

        var previousRoot = _catalogDatabase.GetDatabaseRoot();
        _catalogDatabase.SwitchToPath(dbRoot);
        try
        {
            await _migrator.MigrateAsync(ct);

            using var dbConn = _catalogDatabase.CreateConnection();
            await dbConn.OpenAsync(ct).ConfigureAwait(false);

            var identity = _identityService.GetCurrentUser();
            var now = DateTimeOffset.UtcNow.ToString("o");

            using var tx = dbConn.BeginTransaction();
            try
            {
                using var metaCmd = dbConn.CreateCommand();
                metaCmd.CommandText = """
                    INSERT INTO database_meta (id, name, description, created_at_utc, schema_version)
                    VALUES (@id, @name, @description, @createdAtUtc, 2)
                    """;
                metaCmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@id", id));
                metaCmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@name", name.Trim()));
                metaCmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@description", DBNull.Value));
                metaCmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@createdAtUtc", DateTimeOffset.UtcNow.ToString("o")));
                await metaCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                using var ownerCmd = dbConn.CreateCommand();
                ownerCmd.CommandText = """
                    INSERT INTO db_users (user_id, display_name, role, status, joined_at_utc, last_seen_at_utc)
                    VALUES (@userId, @displayName, 'Owner', 'Active', @now, @now)
                    """;
                ownerCmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@userId", identity.UserId));
                ownerCmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@displayName", identity.DisplayName));
                ownerCmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@now", now));
                await ownerCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                using var ownerIdentityCmd = dbConn.CreateCommand();
                ownerIdentityCmd.CommandText = "UPDATE database_meta SET owner_identity = @ownerIdentity";
                ownerIdentityCmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@ownerIdentity", identity.UserId));
                await ownerIdentityCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
        catch
        {
            _catalogDatabase.SwitchToPath(previousRoot);
            throw;
        }

        var registry = await LoadRegistryAsync(ct);
        var connections = registry.Connections.ToList();
        connections.Add(connection);
        await SaveRegistryAsync(new DatabaseConnectionRegistry(id, connections), ct);

        _catalogDatabase.SwitchToPath(dbRoot);
        ActiveDatabaseChanged?.Invoke(this, id);
        return connection;
    }

    public async Task<DatabaseConnection> ConnectDatabaseAsync(string path, CancellationToken ct = default)
    {
        var fullPath = Path.GetFullPath(path);
        var dbFile = Path.Combine(fullPath, "catalog.db");
        if (!File.Exists(dbFile))
            throw new FileNotFoundException(
                LanguageManager.GetString(StringLocalization.Keys.FM_DbNotFoundAtPath) ?? string.Empty,
                dbFile);

        EnsureDatabaseWritable(dbFile);

        var existingRegistry = await LoadRegistryAsync(ct);
        var existing = existingRegistry.Connections.FirstOrDefault(
            c => c.Path.Equals(fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SmartConLogger.Info($"[DatabaseManager] Database at '{fullPath}' already connected as '{existing.Name}', activating");
            if (existingRegistry.ActiveConnectionId != existing.Id)
            {
                await SaveRegistryAsync(new DatabaseConnectionRegistry(existing.Id, existingRegistry.Connections), ct);
                _catalogDatabase.SwitchToPath(fullPath);
                ActiveDatabaseChanged?.Invoke(this, existing.Id);
            }
            return existing;
        }

        var id = Guid.NewGuid().ToString("N");
        var name = Path.GetFileName(fullPath);

        var previousRoot = _catalogDatabase.GetDatabaseRoot();
        _catalogDatabase.SwitchToPath(fullPath);
        try
        {
            using var conn = _catalogDatabase.CreateConnection();
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM database_meta LIMIT 1";
            var dbName = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
            if (dbName is not null)
                name = dbName;

            var connection = new DatabaseConnection(id, name, fullPath, DateTimeOffset.UtcNow);

            var registry = await LoadRegistryAsync(ct);
            var connections = registry.Connections.ToList();
            connections.Add(connection);
            await SaveRegistryAsync(new DatabaseConnectionRegistry(id, connections), ct);

            await _migrator.MigrateAsync(ct);

            ActiveDatabaseChanged?.Invoke(this, id);
            return connection;
        }
        catch
        {
            _catalogDatabase.SwitchToPath(previousRoot);
            throw;
        }
    }

    public async Task<bool> SwitchDatabaseAsync(string connectionId, CancellationToken ct = default)
    {
        var registry = await LoadRegistryAsync(ct);
        var conn = registry.Connections.FirstOrDefault(c => c.Id == connectionId);
        if (conn is null)
            return false;

        if (registry.ActiveConnectionId == connectionId)
            return true;

        var dbFile = Path.Combine(conn.Path, "catalog.db");
        EnsureDatabaseWritable(dbFile);

        await SaveRegistryAsync(new DatabaseConnectionRegistry(connectionId, registry.Connections), ct);
        _catalogDatabase.SwitchToPath(conn.Path);

        await _migrator.MigrateAsync(ct);

        ActiveDatabaseChanged?.Invoke(this, connectionId);
        return true;
    }

    public async Task<bool> DisconnectDatabaseAsync(string connectionId, CancellationToken ct = default)
    {
        var registry = await LoadRegistryAsync(ct);
        var conn = registry.Connections.FirstOrDefault(c => c.Id == connectionId);
        if (conn is null)
            return false;

        var connections = registry.Connections.Where(c => c.Id != connectionId).ToList();

        string? newActiveId = registry.ActiveConnectionId;
        if (registry.ActiveConnectionId == connectionId)
        {
            var other = connections.FirstOrDefault();
            if (other is null)
                return false;

            newActiveId = other.Id;
            _catalogDatabase.SwitchToPath(other.Path);
        }

        await SaveRegistryAsync(new DatabaseConnectionRegistry(newActiveId, connections), ct);
        ActiveDatabaseChanged?.Invoke(this, newActiveId ?? connectionId);
        return true;
    }

    public async Task<bool> DeleteDatabaseAsync(string connectionId, CancellationToken ct = default)
    {
        var registry = await LoadRegistryAsync(ct);
        var conn = registry.Connections.FirstOrDefault(c => c.Id == connectionId);
        if (conn is null)
            return false;

        var connections = registry.Connections.Where(c => c.Id != connectionId).ToList();

        string? newActiveId = registry.ActiveConnectionId;
        if (registry.ActiveConnectionId == connectionId)
        {
            var other = connections.FirstOrDefault();
            if (other is null)
                return false;

            newActiveId = other.Id;
            _catalogDatabase.SwitchToPath(other.Path);
        }

        if (Directory.Exists(conn.Path))
        {
            await DeleteDirectoryWithRetryAsync(conn.Path, ct);
        }

        await SaveRegistryAsync(new DatabaseConnectionRegistry(newActiveId, connections), ct);

        if (newActiveId != registry.ActiveConnectionId)
        {
            ActiveDatabaseChanged?.Invoke(this, newActiveId!);
        }

        SmartConLogger.Info($"[DatabaseManager] Database at '{conn.Path}' deleted");
        return true;
    }

    private void EnsureDatabaseWritable(string dbFile)
    {
        if (!File.Exists(dbFile))
            return;

        try
        {
            using var connection = _catalogDatabase.CreateConnectionForPath(dbFile);
            connection.Open();
            using var tx = connection.BeginTransaction();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = user_version";
            cmd.ExecuteNonQuery();
            tx.Rollback();
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 8)
        {
            throw new InvalidOperationException(
                LanguageManager.GetString(StringLocalization.Keys.FM_DbReadOnlyError)
                ?? "Database is read-only. Change file permissions or contact your administrator.");
        }
    }

    private static async Task DeleteDirectoryWithRetryAsync(string path, CancellationToken ct, int maxRetries = 3)
    {
        for (var i = 0; i < maxRetries; i++)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (i < maxRetries - 1)
            {
                await Task.Delay(200 * (i + 1), ct);
            }
        }
    }

    private DatabaseConnectionRegistry LoadRegistry()
    {
        if (!File.Exists(_registryPath))
        {
            var registry = new DatabaseConnectionRegistry(null, []);
            SaveRegistry(registry);
            return registry;
        }

        try
        {
            var json = File.ReadAllText(_registryPath);
            var dto = JsonSerializer.Deserialize<RegistryDto>(json, JsonOptions);
            if (dto is null)
                return new DatabaseConnectionRegistry(null, []);

            var connections = dto.Connections
                .Select(c => new DatabaseConnection(c.Id, c.Name, c.Path, c.CreatedAtUtc))
                .ToList();

            return new DatabaseConnectionRegistry(dto.ActiveConnectionId, connections);
        }
        catch
        {
            return new DatabaseConnectionRegistry(null, []);
        }
    }

    private void SaveRegistry(DatabaseConnectionRegistry registry)
    {
        var dir = Path.GetDirectoryName(_registryPath)!;
        Directory.CreateDirectory(dir);

        var dto = new RegistryDto
        {
            ActiveConnectionId = registry.ActiveConnectionId,
            Connections = registry.Connections
                .Select(c => new ConnectionDto
                {
                    Id = c.Id,
                    Name = c.Name,
                    Path = c.Path,
                    CreatedAtUtc = c.CreatedAtUtc
                })
                .ToList()
        };

        var json = JsonSerializer.Serialize(dto, JsonOptions);
        File.WriteAllText(_registryPath, json);
    }

    private Task<DatabaseConnectionRegistry> LoadRegistryAsync(CancellationToken ct)
    {
        return Task.Run(() => LoadRegistry(), ct);
    }

    private Task SaveRegistryAsync(DatabaseConnectionRegistry registry, CancellationToken ct)
    {
        return Task.Run(() => SaveRegistry(registry), ct);
    }

    private sealed class RegistryDto
    {
        public string? ActiveConnectionId { get; set; }
        public List<ConnectionDto> Connections { get; set; } = new();
    }

    private sealed class ConnectionDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public DateTimeOffset CreatedAtUtc { get; set; }
    }
}
