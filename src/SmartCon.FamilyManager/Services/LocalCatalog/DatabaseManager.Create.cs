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

internal sealed partial class DatabaseManager
{
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
}
