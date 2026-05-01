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
    private readonly string _registryPath;

    public DatabaseManager(LocalCatalogDatabase catalogDatabase)
    {
        _catalogDatabase = catalogDatabase;
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
        Directory.CreateDirectory(dbRoot);

        var connection = new DatabaseConnection(id, name.Trim(), dbRoot, DateTimeOffset.UtcNow);

        var previousRoot = _catalogDatabase.GetDatabaseRoot();
        _catalogDatabase.SwitchToPath(dbRoot);
        try
        {
            var migrator = new LocalCatalogMigrator(_catalogDatabase);
            await migrator.MigrateAsync(ct);

            using var dbConn = _catalogDatabase.CreateConnection();
            await dbConn.OpenAsync(ct);
            using var metaCmd = dbConn.CreateCommand();
            metaCmd.CommandText = """
                INSERT INTO database_meta (id, name, description, created_at_utc, schema_version)
                VALUES (@id, @name, @description, @createdAtUtc, 2)
                """;
            metaCmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@id", id));
            metaCmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@name", name.Trim()));
            metaCmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@description", DBNull.Value));
            metaCmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@createdAtUtc", DateTimeOffset.UtcNow.ToString("o")));
            await metaCmd.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            _catalogDatabase.SwitchToPath(previousRoot);
            throw;
        }

        var registry = LoadRegistry();
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
            throw new FileNotFoundException($"Database not found at: {fullPath}");

        var id = Guid.NewGuid().ToString("N");
        var name = Path.GetFileName(fullPath);

        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbFile};Mode=ReadOnly;Pooling=false");
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM database_meta LIMIT 1";
        var dbName = await cmd.ExecuteScalarAsync(ct) as string;
        if (dbName is not null)
            name = dbName;

        var connection = new DatabaseConnection(id, name, fullPath, DateTimeOffset.UtcNow);

        var registry = LoadRegistry();
        var connections = registry.Connections.ToList();
        connections.Add(connection);
        await SaveRegistryAsync(new DatabaseConnectionRegistry(id, connections), ct);

        _catalogDatabase.SwitchToPath(fullPath);
        ActiveDatabaseChanged?.Invoke(this, id);
        return connection;
    }

    public Task<bool> SwitchDatabaseAsync(string connectionId, CancellationToken ct = default)
    {
        var registry = LoadRegistry();
        var conn = registry.Connections.FirstOrDefault(c => c.Id == connectionId);
        if (conn is null)
            return Task.FromResult(false);

        if (registry.ActiveConnectionId == connectionId)
            return Task.FromResult(true);

        SaveRegistry(new DatabaseConnectionRegistry(connectionId, registry.Connections));
        _catalogDatabase.SwitchToPath(conn.Path);

        ActiveDatabaseChanged?.Invoke(this, connectionId);
        return Task.FromResult(true);
    }

    public Task<bool> DisconnectDatabaseAsync(string connectionId, CancellationToken ct = default)
    {
        var registry = LoadRegistry();
        var conn = registry.Connections.FirstOrDefault(c => c.Id == connectionId);
        if (conn is null)
            return Task.FromResult(false);

        var connections = registry.Connections.Where(c => c.Id != connectionId).ToList();

        string? newActiveId = registry.ActiveConnectionId;
        if (registry.ActiveConnectionId == connectionId)
        {
            var other = connections.FirstOrDefault();
            if (other is null)
                return Task.FromResult(false);

            newActiveId = other.Id;
            _catalogDatabase.SwitchToPath(other.Path);
        }

        SaveRegistry(new DatabaseConnectionRegistry(newActiveId, connections));
        ActiveDatabaseChanged?.Invoke(this, newActiveId ?? connectionId);
        return Task.FromResult(true);
    }

    public async Task<bool> DeleteDatabaseAsync(string connectionId, CancellationToken ct = default)
    {
        var registry = LoadRegistry();
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
            DeleteDirectoryWithRetry(conn.Path);
        }

        await SaveRegistryAsync(new DatabaseConnectionRegistry(newActiveId, connections), ct);

        if (newActiveId != registry.ActiveConnectionId)
        {
            ActiveDatabaseChanged?.Invoke(this, newActiveId!);
        }

        SmartConLogger.Info($"[DatabaseManager] Database at '{conn.Path}' deleted");
        return true;
    }

    private static void DeleteDirectoryWithRetry(string path, int maxRetries = 3)
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
                Thread.Sleep(200 * (i + 1));
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

    private Task SaveRegistryAsync(DatabaseConnectionRegistry registry, CancellationToken ct)
    {
        SaveRegistry(registry);
        return Task.CompletedTask;
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
