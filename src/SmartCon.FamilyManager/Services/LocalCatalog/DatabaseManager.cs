using System.IO;
using System.Text.Json;
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
    private readonly string _rootPath;
    private readonly string _registryPath;

    public DatabaseManager(LocalCatalogDatabase catalogDatabase)
    {
        _catalogDatabase = catalogDatabase;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _rootPath = Path.Combine(appData, "SmartCon", "FamilyManager", "databases");
        _registryPath = Path.Combine(Path.GetDirectoryName(_rootPath)!, "databases.json");

        var registry = LoadRegistry();
        _catalogDatabase.SwitchDatabase(registry.ActiveDatabaseId);
    }

    public event EventHandler<string>? ActiveDatabaseChanged;

    public IReadOnlyList<DatabaseInfo> ListDatabases()
    {
        var registry = LoadRegistry();
        return registry.Databases;
    }

    public string GetActiveDatabaseId()
    {
        var registry = LoadRegistry();
        return registry.ActiveDatabaseId;
    }

    public async Task<DatabaseInfo> CreateDatabaseAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Database name cannot be empty.", nameof(name));

        var registry = LoadRegistry();
        var id = Guid.NewGuid().ToString("N");
        var dbPath = Path.Combine(_rootPath, id);
        Directory.CreateDirectory(dbPath);

        var info = new DatabaseInfo(id, name.Trim(), DateTimeOffset.UtcNow);
        var databases = registry.Databases.ToList();
        databases.Add(info);

        // Temporarily switch to new DB path, migrate, then restore
        var previousDbId = registry.ActiveDatabaseId;
        _catalogDatabase.SwitchDatabase(id);
        try
        {
            var migrator = new LocalCatalogMigrator(_catalogDatabase);
            await migrator.MigrateAsync(ct);
        }
        finally
        {
            _catalogDatabase.SwitchDatabase(previousDbId);
        }

        // Save registry after successful migration
        await SaveRegistryAsync(new DatabaseRegistry(registry.ActiveDatabaseId, databases), ct);

        return info;
    }

    public Task<bool> SwitchDatabaseAsync(string databaseId, CancellationToken ct = default)
    {
        var registry = LoadRegistry();
        if (!registry.Databases.Any(d => d.Id == databaseId))
            return Task.FromResult(false);

        if (registry.ActiveDatabaseId == databaseId)
            return Task.FromResult(true);

        var newRegistry = new DatabaseRegistry(databaseId, registry.Databases);
        SaveRegistry(newRegistry);

        _catalogDatabase.SwitchDatabase(databaseId);

        ActiveDatabaseChanged?.Invoke(this, databaseId);
        return Task.FromResult(true);
    }

    public async Task<bool> DeleteDatabaseAsync(string databaseId, CancellationToken ct = default)
    {
        var registry = LoadRegistry();
        var db = registry.Databases.FirstOrDefault(d => d.Id == databaseId);
        if (db is null)
            return false;

        // If deleting active database, switch to another one first
        string newActiveId = registry.ActiveDatabaseId;
        if (registry.ActiveDatabaseId == databaseId)
        {
            var otherDb = registry.Databases.FirstOrDefault(d => d.Id != databaseId);
            if (otherDb is null)
                return false; // Cannot delete the only database

            newActiveId = otherDb.Id;
            _catalogDatabase.SwitchDatabase(newActiveId);
        }

        var dbPath = Path.Combine(_rootPath, databaseId);
        if (Directory.Exists(dbPath))
        {
            Directory.Delete(dbPath, recursive: true);
        }

        var databases = registry.Databases.Where(d => d.Id != databaseId).ToList();
        await SaveRegistryAsync(new DatabaseRegistry(newActiveId, databases), ct);

        if (newActiveId != registry.ActiveDatabaseId)
        {
            ActiveDatabaseChanged?.Invoke(this, newActiveId);
        }

        return true;
    }

    private DatabaseRegistry LoadRegistry()
    {
        if (!File.Exists(_registryPath))
        {
            var defaultDbName = LanguageManager.GetString(StringLocalization.Keys.FM_DefaultDbName) ?? "Main Catalog";
            var defaultDb = new DatabaseInfo("default", defaultDbName, DateTimeOffset.UtcNow);
            var registry = new DatabaseRegistry("default", new[] { defaultDb });
            SaveRegistry(registry);

            var dbPath = Path.Combine(_rootPath, "default");
            Directory.CreateDirectory(dbPath);
            _catalogDatabase.SwitchDatabase("default");
            var migrator = new LocalCatalogMigrator(_catalogDatabase);
            migrator.Migrate();

            return registry;
        }

        var json = File.ReadAllText(_registryPath);
        var dto = JsonSerializer.Deserialize<RegistryDto>(json, JsonOptions);
        if (dto is null || dto.Databases is null)
        {
            var fallbackDbName = LanguageManager.GetString(StringLocalization.Keys.FM_DefaultDbName) ?? "Main Catalog";
            var fallbackDb = new DatabaseInfo("default", fallbackDbName, DateTimeOffset.UtcNow);
            return new DatabaseRegistry("default", new[] { fallbackDb });
        }

        var databases = dto.Databases
            .Select(d => new DatabaseInfo(d.Id, d.Name, d.CreatedAtUtc))
            .ToList();

        return new DatabaseRegistry(dto.ActiveDatabaseId, databases);
    }

    private void SaveRegistry(DatabaseRegistry registry)
    {
        var dir = Path.GetDirectoryName(_registryPath)!;
        Directory.CreateDirectory(dir);

        var dto = new RegistryDto
        {
            ActiveDatabaseId = registry.ActiveDatabaseId,
            Databases = registry.Databases
                .Select(d => new DatabaseDto
                {
                    Id = d.Id,
                    Name = d.Name,
                    CreatedAtUtc = d.CreatedAtUtc
                })
                .ToList()
        };

        var json = JsonSerializer.Serialize(dto, JsonOptions);
        File.WriteAllText(_registryPath, json);
    }

    private Task SaveRegistryAsync(DatabaseRegistry registry, CancellationToken ct)
    {
        SaveRegistry(registry);
        return Task.CompletedTask;
    }

    private sealed class RegistryDto
    {
        public string ActiveDatabaseId { get; set; } = string.Empty;
        public List<DatabaseDto> Databases { get; set; } = new();
    }

    private sealed class DatabaseDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public DateTimeOffset CreatedAtUtc { get; set; }
    }
}
