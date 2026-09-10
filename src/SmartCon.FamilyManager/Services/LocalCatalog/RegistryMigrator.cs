using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

/// <summary>
/// Default <see cref="IRegistryMigrator"/>. Operates on
/// <c>%AppData%\SmartCon\FamilyManager\registry.json</c>. The v0→v1 migration
/// is purely additive: a v0 file has no <c>schemaVersion</c> key and no
/// per-connection <c>kind</c> / <c>projectBinding</c> keys — we just patch the
/// JSON tree in place with the new keys set to their #119 defaults (<see cref="BaseType.General"/>
/// and <c>null</c> binding), bump <c>schemaVersion</c> to <c>1</c> and write
/// the result atomically. See #119 decision A12.
/// </summary>
/// <remarks>
/// Future schema versions can chain on top of this method by branching on
/// the loaded <c>schemaVersion</c> and patching successively. Each step is
/// idempotent so the migrator is safe to call on every launch.
/// </remarks>
internal sealed class RegistryMigrator : IRegistryMigrator
{
    private const int LatestSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _registryPath;
    private readonly string _tempPath;
    private readonly string _bakPath;

    public RegistryMigrator()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var fmDir = Path.Combine(appData, "SmartCon", "FamilyManager");
        Directory.CreateDirectory(fmDir);
        _registryPath = Path.Combine(fmDir, "registry.json");
        _tempPath = _registryPath + ".tmp";
        _bakPath = _registryPath + ".bak";
    }

    int IRegistryMigrator.LatestSchemaVersion => LatestSchemaVersion;

    public Task MigrateAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            using var _scope = SmartConLogger.BeginScope("FMRegistryMigrator",
                ("Method", nameof(MigrateAsync)));

            if (!File.Exists(_registryPath))
            {
                SmartConLogger.Debug("registry.json not present — skipping migration");
                return;
            }

            string json;
            try
            {
                json = File.ReadAllText(_registryPath);
            }
            catch (IOException ex)
            {
                SmartConLogger.Warn($"Failed to read registry.json during migrate: {ex.Message}. [Action: verify file permissions and retry add-in load]");
                return;
            }

            JsonObject? root;
            try
            {
                root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }) as JsonObject;
            }
            catch (JsonException ex)
            {
                SmartConLogger.Warn($"Failed to parse registry.json during migrate: {ex.Message}. [Action: the .bak of the previous version will be used as fallback — manual restore may be needed]");
                return;
            }

            if (root is null) return;

            var currentVersion = root.TryGetPropertyValue("schemaVersion", out var sv) && sv is JsonValue jv && jv.TryGetValue<int>(out var parsed)
                ? parsed
                : 0;

            if (currentVersion >= LatestSchemaVersion)
            {
                SmartConLogger.Debug($"registry.json already at schemaVersion={currentVersion} — skipping migration");
                return;
            }

            root["schemaVersion"] = LatestSchemaVersion;

            if (root.TryGetPropertyValue("connections", out var connsNode) && connsNode is JsonArray conns)
            {
                foreach (var connNode in conns)
                {
                    if (connNode is not JsonObject conn) continue;
                    if (!conn.ContainsKey("kind"))
                        conn["kind"] = nameof(BaseType.General);
                    if (!conn.ContainsKey("projectBinding"))
                        conn["projectBinding"] = null;
                }
            }

            WriteRegistryAtomic(root.ToJsonString(JsonOptions));
            SmartConLogger.Info($"registry.json migrated from schemaVersion={currentVersion} to {LatestSchemaVersion}");
        }, ct);
    }

    /// <summary>
    /// Atomic write: write to <c>registry.json.tmp</c> in the same directory
    /// (same volume — required by <c>File.Replace</c> per dotnet/runtime),
    /// then <c>File.Replace</c> with <c>registry.json.bak</c> as the backup
    /// destination. Having the .bak persist between launches is desirable:
    /// if the main file ever fails to parse, <c>DatabaseManager.LoadRegistry</c>
    /// falls back to <c>registry.json.bak</c>. See #119 decision A12.
    /// </summary>
    private void WriteRegistryAtomic(string json)
    {
        var dir = Path.GetDirectoryName(_registryPath)!;
        Directory.CreateDirectory(dir);

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
            if (File.Exists(_registryPath))
                File.Delete(_registryPath);
            File.Move(_tempPath, _registryPath);
        }
    }
}