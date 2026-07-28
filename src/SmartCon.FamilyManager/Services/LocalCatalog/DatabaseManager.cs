using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.UI;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed class DatabaseManager : IDatabaseManager
{
    private const int LatestRegistrySchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed class ProjectBaseBindingDto
    {
        public Core.Models.FileNameTemplate Template { get; set; } = new();
        public List<Core.Models.FieldDefinition> FieldLibrary { get; set; } = [];
    }

    private static string SerializeBinding(ProjectBaseBinding binding)
    {
        var dto = new ProjectBaseBindingDto
        {
            Template = binding.Template,
            FieldLibrary = binding.FieldLibrary.ToList()
        };
        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    private static ProjectBaseBinding? DeserializeBinding(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var dto = JsonSerializer.Deserialize<ProjectBaseBindingDto>(json!, JsonOptions);
            if (dto?.Template is null) return null;
            return new ProjectBaseBinding(dto.Template, dto.FieldLibrary ?? []);
        }
        catch
        {
            return null;
        }
    }

    private readonly LocalCatalogDatabase _catalogDatabase;
    private readonly IUserIdentityService _identityService;
    private readonly ILocalCatalogMigrator _migrator;
    private readonly IRegistryMigrator _registryMigrator;
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
        IRegistryMigrator registryMigrator)
    {
        _catalogDatabase = catalogDatabase;
        _identityService = identityService;
        _migrator = migrator;
        _registryMigrator = registryMigrator;
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

    private async Task<DatabaseConnection> CreateDatabaseCoreAsync(string name, string path, CancellationToken ct)
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
                    INSERT INTO database_meta (id, name, description, created_at_utc, schema_version, min_plugin_version)
                    VALUES (@id, @name, @description, @createdAtUtc, 2, @minPluginVersion)
                    """;
                metaCmd.Parameters.Add(new SqliteParameter("@id", id));
                metaCmd.Parameters.Add(new SqliteParameter("@name", name.Trim()));
                metaCmd.Parameters.Add(new SqliteParameter("@description", DBNull.Value));
                metaCmd.Parameters.Add(new SqliteParameter("@createdAtUtc", DateTimeOffset.UtcNow.ToString("o")));
                // ADR-058 (#173): a freshly created database is written in the
                // current breaking format from the start (e.g. FHV3 hashes), so
                // it carries the compatibility floor immediately.
                metaCmd.Parameters.Add(new SqliteParameter("@minPluginVersion", DbCompatibility.CurrentMinPluginVersion));
                await metaCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                using var ownerCmd = dbConn.CreateCommand();
                ownerCmd.CommandText = """
                    INSERT INTO db_users (user_id, display_name, role, status, joined_at_utc, last_seen_at_utc)
                    VALUES (@userId, @displayName, 'Owner', 'Active', @now, @now)
                    """;
                ownerCmd.Parameters.Add(new SqliteParameter("@userId", identity.UserId));
                ownerCmd.Parameters.Add(new SqliteParameter("@displayName", identity.DisplayName));
                ownerCmd.Parameters.Add(new SqliteParameter("@now", now));
                await ownerCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                using var ownerIdentityCmd = dbConn.CreateCommand();
                ownerIdentityCmd.CommandText = "UPDATE database_meta SET owner_identity = @ownerIdentity";
                ownerIdentityCmd.Parameters.Add(new SqliteParameter("@ownerIdentity", identity.UserId));
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

    public async Task<DatabaseConnection> CreateProjectDatabaseAsync(
        string name,
        string path,
        ProjectBaseBinding binding,
        CancellationToken ct = default)
    {
        if (binding is null)
            throw new ArgumentNullException(nameof(binding));

        await _registryLock.WaitAsync(ct);
        try
        {
            var generalConnection = await CreateDatabaseCoreAsync(name, path, ct);
            var projectConnection = generalConnection with { Kind = BaseType.Project, ProjectBinding = binding };

            await UpdateCachedBaseTypeAsync(BaseType.Project, binding, ct);

            var registry = await LoadRegistryAsync(ct);
            var updatedConnections = registry.Connections
                .Select(c => c.Id == generalConnection.Id ? projectConnection : c)
                .ToList();
            await SaveRegistryAsync(new DatabaseConnectionRegistry(generalConnection.Id, updatedConnections), ct);

            return projectConnection;
        }
        finally
        {
            _registryLock.Release();
        }
    }

    public async Task<DatabaseConnection> ConfigureProjectBaseAsync(
        string connectionId,
        ProjectBaseBinding binding,
        CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope("DatabaseManager",
            ("Method", nameof(ConfigureProjectBaseAsync)),
            ("ConnectionId", connectionId));

        if (string.IsNullOrEmpty(connectionId))
            throw new ArgumentException("connectionId cannot be empty.", nameof(connectionId));
        if (binding is null)
            throw new ArgumentNullException(nameof(binding));

        await _registryLock.WaitAsync(ct);
        try
        {
            var registry = await LoadRegistryAsync(ct);
            var conn = registry.Connections.FirstOrDefault(c => c.Id == connectionId)
                ?? throw new InvalidOperationException(
                    $"Cannot configure project binding on unknown connection '{connectionId}'.");

            // Write the source of truth first (ADR-045 A1): if the UPDATE fails
            // (e.g. SQLITE_BUSY on an SMB share), both stores consistently keep
            // the old state. A registry failure after a successful UPDATE
            // self-heals on the next reconnect — the DB wins.
            await UpdateTargetCachedBaseTypeAsync(conn.Path, BaseType.Project, binding, ct);

            var updated = conn with { Kind = BaseType.Project, ProjectBinding = binding };
            var updatedConnections = registry.Connections
                .Select(c => c.Id == connectionId ? updated : c)
                .ToList();
            await SaveRegistryAsync(new DatabaseConnectionRegistry(registry.ActiveConnectionId, updatedConnections), ct);

            SmartConLogger.Info($"Connection '{conn.Name}' configured as project base (was {conn.Kind})");
            return updated;
        }
        finally
        {
            _registryLock.Release();
        }
    }

    public async Task<DatabaseConnection> ConvertToGeneralBaseAsync(
        string connectionId,
        CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope("DatabaseManager",
            ("Method", nameof(ConvertToGeneralBaseAsync)),
            ("ConnectionId", connectionId));

        if (string.IsNullOrEmpty(connectionId))
            throw new ArgumentException("connectionId cannot be empty.", nameof(connectionId));

        await _registryLock.WaitAsync(ct);
        try
        {
            var registry = await LoadRegistryAsync(ct);
            var conn = registry.Connections.FirstOrDefault(c => c.Id == connectionId)
                ?? throw new InvalidOperationException(
                    $"Cannot convert unknown connection '{connectionId}' to general base.");

            if (conn.Kind == BaseType.General && conn.ProjectBinding is null)
            {
                SmartConLogger.Info($"Connection '{conn.Name}' is already a general base — no-op");
                return conn;
            }

            // Self-healing: a General connection with a leftover ProjectBinding
            // (externally damaged catalog.db) is normalized here as well.
            // Write the source of truth first (ADR-045 A1): if the UPDATE fails
            // (e.g. SQLITE_BUSY on an SMB share), both stores consistently keep
            // the old state. A registry failure after a successful UPDATE
            // self-heals on the next reconnect — the DB wins.
            await UpdateTargetCachedBaseTypeAsync(conn.Path, BaseType.General, null, ct);

            var updated = conn with { Kind = BaseType.General, ProjectBinding = null };
            var updatedConnections = registry.Connections
                .Select(c => c.Id == connectionId ? updated : c)
                .ToList();
            await SaveRegistryAsync(new DatabaseConnectionRegistry(registry.ActiveConnectionId, updatedConnections), ct);

            SmartConLogger.Info($"Connection '{conn.Name}' converted to general base; project binding cleared");
            return updated;
        }
        finally
        {
            _registryLock.Release();
        }
    }

    private async Task UpdateTargetCachedBaseTypeAsync(string targetPath, BaseType kind, ProjectBaseBinding? binding, CancellationToken ct)
    {
        var activePath = _catalogDatabase.GetDatabaseRoot();
        try
        {
            _catalogDatabase.SwitchToPath(targetPath);
            await UpdateCachedBaseTypeAsync(kind, binding, ct);
        }
        finally
        {
            _catalogDatabase.SwitchToPath(activePath);
        }
    }

    /// <summary>
    /// Flip the cached <c>catalog.db.database_meta.base_type</c> column on
    /// the *currently switched-to* database to <paramref name="kind"/>. Used
    /// by <see cref="CreateProjectDatabaseAsync"/> and
    /// <see cref="ConfigureProjectBaseAsync"/> right after switching to the
    /// database they are about to project-scope.
    /// </summary>
    private async Task UpdateCachedBaseTypeAsync(BaseType kind, ProjectBaseBinding? binding, CancellationToken ct)
    {
        using var _scope = SmartConLogger.BeginScope("DatabaseManager",
            ("Method", nameof(UpdateCachedBaseTypeAsync)),
            ("BaseType", (int)kind));

        var cachedValue = (int)kind;
        var bindingJson = kind == BaseType.Project && binding is not null
            ? SerializeBinding(binding)
            : null;
        using var dbConn = _catalogDatabase.CreateConnection();
        await dbConn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = dbConn.CreateCommand();
        cmd.CommandText = "UPDATE database_meta SET base_type = @baseType, project_binding_json = @bindingJson";
        cmd.Parameters.Add(new SqliteParameter("@baseType", cachedValue));
        cmd.Parameters.Add(new SqliteParameter("@bindingJson", bindingJson is null ? DBNull.Value : bindingJson));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        SmartConLogger.Info($"database_meta.base_type set to {cachedValue} ({kind}) with binding persisted");
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
                using var nameCmd = conn.CreateCommand();
                nameCmd.CommandText = "SELECT name FROM database_meta LIMIT 1";
                var dbName = await nameCmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
                if (dbName is not null)
                    name = dbName;

                await _migrator.MigrateAsync(ct);

                using var metaCmd = conn.CreateCommand();
                metaCmd.CommandText = "SELECT base_type, project_binding_json FROM database_meta LIMIT 1";
                using var reader = await metaCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                BaseType kind = BaseType.General;
                ProjectBaseBinding? binding = null;
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
                }

                var connection = new DatabaseConnection(id, name, fullPath, DateTimeOffset.UtcNow, null, null, kind, binding);

                var registry = await LoadRegistryAsync(ct);
                var connections = registry.Connections.ToList();
                connections.Add(connection);
                await SaveRegistryAsync(new DatabaseConnectionRegistry(id, connections), ct);

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

    public async Task<bool> DeleteDatabaseAsync(string connectionId, CancellationToken ct = default)
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
            }

            if (Directory.Exists(conn.Path))
            {
                // Release any idle SQLite handles before touching the filesystem.
                SqliteConnection.ClearAllPools();

                try
                {
                    var deleted = await SafeDeleteDirectoryAsync(conn.Path, _trashPath, ct);
                    if (!deleted)
                    {
                        using var _scope = SmartConLogger.BeginScope("DatabaseManager",
                            ("Method", nameof(DeleteDatabaseAsync)),
                            ("DatabaseName", conn.Name),
                            ("FileName", Path.GetFileName(conn.Path)));
                        SmartConLogger.Warn($"Database files for '{conn.Name}' moved to trash because they were locked. " +
                            $"[Action: remaining files will be removed on the next Revit launch]");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    using var _scope = SmartConLogger.BeginScope("DatabaseManager",
                        ("Method", nameof(DeleteDatabaseAsync)),
                        ("DatabaseName", conn.Name),
                        ("FileName", Path.GetFileName(conn.Path)),
                        ("Exception", ex.GetType().Name));
                    SmartConLogger.Warn($"Could not delete database files for '{conn.Name}' because they are in use. " +
                        $"[Action: close Revit and remove remaining folder manually: {conn.Path}]");

                    await SaveRegistryAsync(new DatabaseConnectionRegistry(newActiveId, connections), ct);

                    if (newActiveId != registry.ActiveConnectionId)
                    {
                        ActiveDatabaseChanged?.Invoke(this, newActiveId);
                    }

                    var message = LanguageManager.GetString(StringLocalization.Keys.FM_DbDeleteFilesLocked)
                        ?? "Database \"{0}\" removed from the list, but files could not be deleted because they are in use. Close Revit to remove remaining files.";
                    throw new InvalidOperationException(string.Format(message, conn.Name), ex);
                }
            }

            await SaveRegistryAsync(new DatabaseConnectionRegistry(newActiveId, connections), ct);

            if (newActiveId != registry.ActiveConnectionId)
            {
                ActiveDatabaseChanged?.Invoke(this, newActiveId);
            }

            SmartConLogger.Info($"DatabaseManager.Delete: Database '{conn.Name}' deleted");
            return true;
        }
        finally
        {
            _registryLock.Release();
        }
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

    private static async Task<bool> SafeDeleteDirectoryAsync(string path, string trashPath, CancellationToken ct, int maxRetries = 5)
    {
        if (!Directory.Exists(path))
            return true;

        ClearDirectoryAttributes(path);

        for (var i = 0; i < maxRetries; i++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                Directory.Delete(path, recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (i < maxRetries - 1)
                {
                    await Task.Delay(200 * (i + 1), ct);
                    ClearDirectoryAttributes(path);
                }
            }
        }

        // Fallback: move the locked folder to the trash area so it can be retried later.
        Directory.CreateDirectory(trashPath);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        var guidSuffix = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var trashName = $"{Path.GetFileName(path)}_{timestamp}_{guidSuffix}";
        var trashItemPath = Path.Combine(trashPath, trashName);

        Directory.Move(path, trashItemPath);
        return false;
    }

    private static void ClearDirectoryAttributes(string path)
    {
        if (!Directory.Exists(path))
            return;

        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
        catch
        {
            // ignored
        }

        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            catch
            {
                // ignored
            }
        }

        foreach (var dir in Directory.GetDirectories(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(dir, FileAttributes.Normal);
            }
            catch
            {
                // ignored
            }
        }
    }

    private async Task CleanupTrashAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_trashPath))
            return;

        var dirs = await Task.Run(() => Directory.GetDirectories(_trashPath), ct);
        foreach (var dir in dirs)
        {
            try
            {
                ClearDirectoryAttributes(dir);
                await Task.Run(() => Directory.Delete(dir, recursive: true), ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                using var _scope = SmartConLogger.BeginScope("DatabaseManager",
                    ("Method", nameof(CleanupTrashAsync)),
                    ("FileName", Path.GetFileName(dir)));
                SmartConLogger.Warn($"Could not clean up trashed folder '{dir}'. It will be retried on the next launch. " +
                    $"[Action: close Revit if the folder is still locked]");
            }
        }
    }

    private DatabaseConnectionRegistry LoadRegistry()
    {
        if (!File.Exists(_registryPath))
        {
            var registry = new DatabaseConnectionRegistry(null, [], SchemaVersion: LatestRegistrySchemaVersion);
            SaveRegistry(registry);
            return registry;
        }

        var dto = TryReadRegistryDto(_registryPath);
        if (dto is null && File.Exists(_bakPath))
        {
            using var _scope = SmartConLogger.BeginScope("DatabaseManager",
                ("Method", nameof(LoadRegistry)));
            SmartConLogger.Warn(
                $"registry.json parse failed — falling back to registry.json.bak. " +
                "[Action: user might want to inspect the corrupt registry.json — the previous good copy is now active]");
            dto = TryReadRegistryDto(_bakPath);
        }
        if (dto is null)
            return new DatabaseConnectionRegistry(null, [], SchemaVersion: LatestRegistrySchemaVersion);

        var connections = dto.Connections
            .Select(MapDtoToConnection)
            .ToList();

        return new DatabaseConnectionRegistry(dto.ActiveConnectionId, connections, dto.SchemaVersion);
    }

    private RegistryDto? TryReadRegistryDto(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<RegistryDto>(json, JsonOptions);
            return dto;
        }
        catch
        {
            return null;
        }
    }

    private static DatabaseConnection MapDtoToConnection(ConnectionDto c)
    {
        return new DatabaseConnection(
            c.Id,
            c.Name,
            c.Path,
            c.CreatedAtUtc,
            CurrentUserRole: c.CurrentUserRole,
            OwnerIdentity: c.OwnerIdentity,
            Kind: c.Kind,
            ProjectBinding: c.ProjectBinding);
    }

    private void SaveRegistry(DatabaseConnectionRegistry registry)
    {
        var dir = Path.GetDirectoryName(_registryPath)!;
        Directory.CreateDirectory(dir);

        var dto = new RegistryDto
        {
            SchemaVersion = LatestRegistrySchemaVersion,
            ActiveConnectionId = registry.ActiveConnectionId,
            Connections = registry.Connections
                .Select(c => new ConnectionDto
                {
                    Id = c.Id,
                    Name = c.Name,
                    Path = c.Path,
                    CreatedAtUtc = c.CreatedAtUtc,
                    CurrentUserRole = c.CurrentUserRole,
                    OwnerIdentity = c.OwnerIdentity,
                    Kind = c.Kind,
                    ProjectBinding = c.ProjectBinding
                })
                .ToList()
        };

        var json = JsonSerializer.Serialize(dto, JsonOptions);
        WriteRegistryAtomic(json);
    }

    /// <summary>
    /// Atomic write: write to <c>registry.json.tmp</c> (same directory = same
    /// volume — required by <c>File.Replace</c>), then swap with the live file
    /// and back the previous contents up to <c>registry.json.bak</c>. If
    /// <c>File.Replace</c> fails (typically antivirus locking), fall back to
    /// delete + Move. See #119 decision A12.
    /// Single-writer guarantee comes from the operation-level
    /// <see cref="_registryLock"/> on every public mutator (#171) — without it
    /// two concurrent writers collide on the shared .tmp and can leave the
    /// live file corrupted.
    /// </summary>
    private void WriteRegistryAtomic(string json)
    {
        File.WriteAllText(_tempPath, json);

        if (!File.Exists(_registryPath))
        {
            File.Move(_tempPath, _registryPath);
            return;
        }

        if (File.Exists(_bakPath))
            File.Delete(_bakPath);

        try
        {
            File.Replace(_tempPath, _registryPath, _bakPath, ignoreMetadataErrors: true);
        }
        catch (IOException)
        {
            using var _scope = SmartConLogger.BeginScope("DatabaseManager",
                ("Method", nameof(WriteRegistryAtomic)));
            SmartConLogger.Warn(
                "File.Replace of registry.json failed — falling back to delete+move. " +
                "[Action: investigate antivirus or extension locks, but the registry was still saved]");
            if (File.Exists(_registryPath))
                File.Delete(_registryPath);
            File.Move(_tempPath, _registryPath);
        }
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
        /// <summary>
        /// Schema version of this file. <c>0</c> (or missing key) marks a
        /// pre-#119 legacy file; the on-disk file is upgraded by
        /// <c>IRegistryMigrator</c> on the first launch after an upgrade.
        /// </summary>
        public int SchemaVersion { get; set; }
        public string? ActiveConnectionId { get; set; }
        public List<ConnectionDto> Connections { get; set; } = new();
    }

    private sealed class ConnectionDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public DateTimeOffset CreatedAtUtc { get; set; }
        public DbUserRole? CurrentUserRole { get; set; }
        public string? OwnerIdentity { get; set; }
        /// <summary>General or Project base, see #119. Defaults to <see cref="BaseType.General"/> for legacy entries.</summary>
        public BaseType Kind { get; set; } = BaseType.General;
        /// <summary>Project-binding template + field library. NULL for <see cref="BaseType.General"/> connections.</summary>
        public ProjectBaseBinding? ProjectBinding { get; set; }
    }
}
