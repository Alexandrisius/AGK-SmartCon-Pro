using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.Cloud;
using SmartCon.UI;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed partial class DatabaseManager : IDatabaseManager
{
    private const int LatestRegistrySchemaVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly LocalCatalogDatabase _catalogDatabase;
    private readonly IUserIdentityService _identityService;
    private readonly ILocalCatalogMigrator _migrator;
    private readonly IRegistryMigrator _registryMigrator;
    private readonly CloudDatabaseGate _cloudGate;
    private readonly string _registryPath;
    private readonly string _bakPath;
    private readonly string _tempPath;
    private readonly string _trashPath;

    // #171: serializes every registry read-modify-write operation (and the
    // catalog.db path switches bundled with it). Without it the fire-and-forget
    // project-base auto-activation raced itself and UI commands on
    // registry.json.tmp, corrupting registry.json (IOException + .bak rollback)
    // and losing concurrent updates. Sync readers (ListConnections etc.) stay
    // lock-free on purpose: taking this lock on the UI thread would deadlock
    // against an async writer awaiting a UI-thread continuation.
    private readonly SemaphoreSlim _registryLock = new(1, 1);

    public DatabaseManager(
        LocalCatalogDatabase catalogDatabase,
        IUserIdentityService identityService,
        ILocalCatalogMigrator migrator,
        IRegistryMigrator registryMigrator,
        Services.Cloud.CloudDatabaseGate? cloudGate = null)
    {
        _catalogDatabase = catalogDatabase;
        _identityService = identityService;
        _migrator = migrator;
        _registryMigrator = registryMigrator;
        // Optional for unit tests (a detached gate instance is a no-op); DI
        // injects the process-wide singleton shared with DbAccessControlService.
        _cloudGate = cloudGate ?? new Services.Cloud.CloudDatabaseGate();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var fmDir = Path.Combine(appData, "SmartCon", "FamilyManager");
        Directory.CreateDirectory(fmDir);
        _registryPath = Path.Combine(fmDir, "registry.json");
        _bakPath = _registryPath + ".bak";
        _tempPath = _registryPath + ".tmp";
        _trashPath = Path.Combine(fmDir, ".trash");

        var registry = LoadRegistry();
        if (registry.ActiveConnectionId is not null)
        {
            var active = registry.Connections.FirstOrDefault(c => c.Id == registry.ActiveConnectionId);
            if (active is not null)
            {
                _catalogDatabase.SwitchToPath(active.Path);
                _cloudGate.Update(active);
            }
        }
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _registryLock.WaitAsync(ct);
        try
        {
            await _registryMigrator.MigrateAsync(ct);

            await CleanupTrashAsync(ct);

            var registry = LoadRegistry();
            if (registry.ActiveConnectionId is not null)
            {
                var active = registry.Connections.FirstOrDefault(c => c.Id == registry.ActiveConnectionId);
                if (active is not null)
                {
                    _catalogDatabase.SwitchToPath(active.Path);
                    await _migrator.MigrateAsync(ct);
                    // E27 self-heal: the active DB is migrated (V39 column
                    // guaranteed) — restore a cloudLink wiped by a downgrade.
                    var connections = registry.Connections.ToList();
                    active = await HealCloudLinkAsync(connections, active, ct);
                    _cloudGate.Update(active);
                }
            }
        }
        finally
        {
            _registryLock.Release();
        }
    }

    public event EventHandler<string?>? ActiveDatabaseChanged;

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
        await _registryLock.WaitAsync(ct);
        try
        {
            return await CreateDatabaseCoreAsync(name, path, ct);
        }
        finally
        {
            _registryLock.Release();
        }
    }

    public async Task<DatabaseConnection> ConnectDatabaseAsync(string path, CancellationToken ct = default)
    {
        var fullPath = Path.GetFullPath(path);
        var dbFile = Path.Combine(fullPath, "catalog.db");
        if (!File.Exists(dbFile))
            throw new FileNotFoundException(
                LanguageManager.GetString(StringLocalization.Keys.FM_DbNotFoundAtPath) ?? string.Empty,
                dbFile);

        await _registryLock.WaitAsync(ct);
        try
        {
            EnsureDatabaseWritable(dbFile);

            var existingRegistry = await LoadRegistryAsync(ct);
            var existing = existingRegistry.Connections.FirstOrDefault(
                c => c.Path.Equals(fullPath, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                using var _scope = SmartConLogger.BeginScope("DatabaseManager",
                    ("FileName", Path.GetFileName(fullPath)), ("ExistingName", existing.Name));
                SmartConLogger.Info($"Database '{Path.GetFileName(fullPath)}' already connected as '{existing.Name}', activating");
                // Reconnect self-heal: запись могла быть создана без cloudLink
                // (подключена старой сборкой) — восстанавливаем из database_meta.
                // Heal мутирует переданный список и сохраняет реестр — работаем
                // дальше с этим же списком, чтобы не перезаписать heal стацией.
                var healedRegistry = existingRegistry;
                if (existing.CloudLink is null)
                {
                    var healedConnections = existingRegistry.Connections.ToList();
                    existing = await HealCloudLinkAsync(healedConnections, existing, ct);
                    healedRegistry = new DatabaseConnectionRegistry(existingRegistry.ActiveConnectionId, healedConnections);
                }
                if (healedRegistry.ActiveConnectionId != existing.Id)
                {
                    await SaveRegistryAsync(new DatabaseConnectionRegistry(existing.Id, healedRegistry.Connections), ct);
                    _catalogDatabase.SwitchToPath(fullPath);
                    _cloudGate.Update(existing);
                    ActiveDatabaseChanged?.Invoke(this, existing.Id);
                }
                else
                {
                    _cloudGate.Update(existing);
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
                using var nameCmd = conn.CreateCommand();
                nameCmd.CommandText = "SELECT name FROM database_meta LIMIT 1";
                var dbName = await nameCmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
                if (dbName is not null)
                    name = dbName;

                await _migrator.MigrateAsync(ct);

                using var metaCmd = conn.CreateCommand();
                metaCmd.CommandText = "SELECT base_type, project_binding_json, remote_source_json FROM database_meta LIMIT 1";
                using var reader = await metaCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                BaseType kind = BaseType.General;
                ProjectBaseBinding? binding = null;
                CloudLink? cloudLink = null;
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var baseTypeValue = reader.GetValue(0);
                    if (baseTypeValue is not null && baseTypeValue != DBNull.Value)
                    {
                        var intValue = Convert.ToInt32(baseTypeValue);
                        if (Enum.IsDefined(typeof(BaseType), intValue))
                            kind = (BaseType)intValue;
                    }

                    if (!reader.IsDBNull(1))
                    {
                        binding = DeserializeBinding(reader.GetString(1));
                    }

                    // Kind и binding переживают disconnect/connect из database_meta —
                    // облачная ссылка (remote_source_json, V39) должна выживать так же.
                    if (!reader.IsDBNull(2))
                        cloudLink = CloudLinkJson.TryDeserialize(reader.GetString(2));
                }

                if (cloudLink is not null)
                {
                    using var _healScope = SmartConLogger.BeginScope("DatabaseManager",
                        ("Method", nameof(ConnectDatabaseAsync)),
                        ("BaseName", name),
                        ("Slug", cloudLink.Slug));
                    SmartConLogger.Info(
                        $"CloudLink restored from database_meta.remote_source_json on connect (role={cloudLink.Role}, slug={cloudLink.Slug})");
                }

                var connection = new DatabaseConnection(id, name, fullPath, DateTimeOffset.UtcNow, null, null, kind, binding, cloudLink);

                var registry = await LoadRegistryAsync(ct);
                var connections = registry.Connections.ToList();
                connections.Add(connection);
                await SaveRegistryAsync(new DatabaseConnectionRegistry(id, connections), ct);

                _cloudGate.Update(connection);
                ActiveDatabaseChanged?.Invoke(this, id);
                return connection;
            }
            catch
            {
                _catalogDatabase.SwitchToPath(previousRoot);
                throw;
            }
        }
        finally
        {
            _registryLock.Release();
        }
    }

    public async Task<bool> SwitchDatabaseAsync(string connectionId, CancellationToken ct = default)
    {
        await _registryLock.WaitAsync(ct);
        try
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

            // E27 self-heal: this DB is migrated (V39 column guaranteed) —
            // restore a cloudLink wiped from the registry by a downgrade.
            var connections = registry.Connections.ToList();
            conn = await HealCloudLinkAsync(connections, conn, ct);
            _cloudGate.Update(conn);

            ActiveDatabaseChanged?.Invoke(this, connectionId);
            return true;
        }
        finally
        {
            _registryLock.Release();
        }
    }

    public async Task<bool> DisconnectDatabaseAsync(string connectionId, CancellationToken ct = default)
    {
        await _registryLock.WaitAsync(ct);
        try
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
                newActiveId = other?.Id;
                if (other is not null)
                    _catalogDatabase.SwitchToPath(other.Path);
                _cloudGate.Update(other);
            }

            await SaveRegistryAsync(new DatabaseConnectionRegistry(newActiveId, connections), ct);
            ActiveDatabaseChanged?.Invoke(this, newActiveId);
            return true;
        }
        finally
        {
            _registryLock.Release();
        }
    }

}
