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
