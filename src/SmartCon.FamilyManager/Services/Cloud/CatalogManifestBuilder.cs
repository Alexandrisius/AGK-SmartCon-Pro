using System.Globalization;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Models.Cloud;
using SmartCon.FamilyManager.Services.LocalCatalog;

namespace SmartCon.FamilyManager.Services.Cloud;

/// <summary>Параметры сборки манифеста publish point (заполняет publish-флоу).</summary>
public sealed class CatalogManifestBuildOptions
{
    /// <summary>Guid облачного каталога (серверный CatalogDto.Id) — не локальный id базы.</summary>
    public string CatalogId { get; init; } = string.Empty;

    /// <summary>Монотонный seq нового publish point = текущий серверный + 1.</summary>
    public long PublishSeq { get; init; }

    /// <summary>displayName публикатора. PII-whitelist: email и user@machine сюда не попадают (ADR-076 §4).</summary>
    public string PublishedBy { get; init; } = string.Empty;

    public string? MinPluginVersion { get; init; }
}

/// <summary>
/// Собирает манифест v1 активного каталога: только активные версии (label =
/// current_version_label со всеми его Revit-вариантами), routing World B
/// (V36–V38), per-type/section хэши (V32/V33) — чистый SQLite, без Revit.
/// Предполагает, что LocalCatalogDatabase уже указывает на корень нужной базы
/// (SwitchToPath выполнен вызывающим кодом).
/// </summary>
internal sealed partial class CatalogManifestBuilder
{
    private readonly LocalCatalogDatabase _database;
    private readonly StoragePathResolver _pathResolver;
    private readonly IClock _clock;

    public CatalogManifestBuilder(LocalCatalogDatabase database, StoragePathResolver pathResolver, IClock clock)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<CatalogManifestV1> BuildAsync(CatalogManifestBuildOptions options, CancellationToken ct = default)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));

        using var _scope = SmartConLogger.BeginScope("CloudManifest",
            ("Method", nameof(BuildAsync)),
            ("CatalogId", options.CatalogId),
            ("PublishSeq", options.PublishSeq));

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var categoryPaths = await LoadCategoryPathsAsync(connection, ct).ConfigureAwait(false);
        var items = await LoadItemsAsync(connection, categoryPaths, ct).ConfigureAwait(false);
        var meta = await BuildMetaAsync(connection, categoryPaths, ct).ConfigureAwait(false);

        // family_dependencies.child_catalog_item_id — FK: ребёнок, не попавший в манифест
        // (битый файл, исключён выше), делает apply невозможным на реальных каталогах.
        // Dangling-зависимости отбрасываем — подписчик получит консистентное подмножество.
        var knownIds = items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var item in items)
        {
            foreach (var version in item.Versions)
            {
                if (version.Dependencies.Count == 0) continue;
                var dangling = version.Dependencies.Where(d => !knownIds.Contains(d.ChildItemId)).ToList();
                if (dangling.Count == 0) continue;
                foreach (var dependency in dangling) version.Dependencies.Remove(dependency);
                SmartConLogger.Warn(
                    $"{dangling.Count} dangling dependency(ies) of '{item.Name}' dropped — " +
                    "child item not part of the manifest. [Action: восстановите/удалите битый дочерний item и опубликуйте повторно]");
            }
        }

        int? hashFormatVersion = null;
        var revitMin = int.MaxValue;
        var revitMax = int.MinValue;
        foreach (var item in items)
        {
            if (item.Versions.Count == 0) continue;
            foreach (var version in item.Versions)
            {
                if (version.SourceRevitVersion < revitMin) revitMin = version.SourceRevitVersion;
                if (version.SourceRevitVersion > revitMax) revitMax = version.SourceRevitVersion;
            }
        }

        // FHV-эпоха каталога: MAX по items (зеркало SetActiveVersion/импорта); пустой каталог → null.
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT MAX(hash_format_version) FROM catalog_items WHERE hash_format_version IS NOT NULL";
            var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (result is not null and not DBNull) hashFormatVersion = (int)(long)result;
        }

        var manifest = new CatalogManifestV1
        {
            CatalogId = options.CatalogId,
            PublishSeq = options.PublishSeq,
            PublishedAtUtc = _clock.UtcNow,
            PublishedBy = options.PublishedBy,
            MinPluginVersion = options.MinPluginVersion,
            HashFormatVersion = hashFormatVersion,
            RevitVersionRange = items.Count > 0 ? new ManifestRevitRangeV1 { Min = revitMin, Max = revitMax } : null,
            Meta = meta,
            Items = items,
        };

        SmartConLogger.Info(
            $"Manifest built: {items.Count} item(s), " +
            $"{items.Sum(i => i.Versions.Count)} active version row(s), " +
            $"{items.Sum(i => i.Assets.Count)} asset(s)");
        return manifest;
    }

    private async Task LoadVersionDetailsAsync(
        SqliteConnection connection, Dictionary<string, ManifestVersionV1> versionRows, CancellationToken ct)
    {
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT catalog_version_id, type_identity_key, type_name, type_hash
                FROM family_type_hashes
                WHERE catalog_version_id IN (
                    SELECT cv.id FROM catalog_versions cv
                    JOIN catalog_items ci ON ci.id = cv.catalog_item_id AND cv.version_label = ci.current_version_label)
                """;
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var versionId = reader.GetString(0);
                if (!versionRows.TryGetValue(versionId, out var version)) continue;
                version.TypeHashes.Add(new ManifestTypeHashV1
                {
                    TypeIdentityKey = reader.GetString(1),
                    TypeName = reader.GetString(2),
                    TypeHash = reader.GetString(3),
                });
            }
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT catalog_version_id, segment_name, nominal_diameter, inner_diameter, outer_diameter,
                       used_in_size_lists, used_in_sizing, sort_order
                FROM family_segment_sizes
                WHERE catalog_version_id IN (
                    SELECT cv.id FROM catalog_versions cv
                    JOIN catalog_items ci ON ci.id = cv.catalog_item_id AND cv.version_label = ci.current_version_label)
                """;
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var versionId = reader.GetString(0);
                if (!versionRows.TryGetValue(versionId, out var version)) continue;
                version.SegmentSizes.Add(new ManifestSegmentSizeV1
                {
                    SegmentName = reader.GetString(1),
                    NominalDiameter = reader.GetDouble(2),
                    InnerDiameter = reader.GetDouble(3),
                    OuterDiameter = reader.GetDouble(4),
                    UsedInSizeLists = reader.GetInt32(5) != 0,
                    UsedInSizing = reader.GetInt32(6) != 0,
                    SortOrder = reader.GetInt32(7),
                });
            }
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT catalog_version_id, family_key, type_name, rule_order, segment_name,
                       min_size_feet, max_size_feet, description
                FROM family_segment_rules
                WHERE catalog_version_id IN (
                    SELECT cv.id FROM catalog_versions cv
                    JOIN catalog_items ci ON ci.id = cv.catalog_item_id AND cv.version_label = ci.current_version_label)
                """;
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var versionId = reader.GetString(0);
                if (!versionRows.TryGetValue(versionId, out var version)) continue;
                version.SegmentRules.Add(new ManifestSegmentRuleV1
                {
                    FamilyKey = reader.GetString(1),
                    TypeName = reader.GetString(2),
                    RuleOrder = reader.GetInt32(3),
                    SegmentName = reader.GetString(4),
                    MinSizeFeet = GetDoubleOrNull(reader, "min_size_feet"),
                    MaxSizeFeet = GetDoubleOrNull(reader, "max_size_feet"),
                    Description = reader.GetString(7),
                });
            }
        }

        await LoadRoutingRulesAsync(connection,
            "SELECT catalog_version_id, family_key, type_name, group_key, rule_order, part_name, description, criteria_json FROM family_routing_rules",
            "catalog_version_id",
            versionRows,
            (v, r) => v.VersionRoutingRules.Add(r), ct).ConfigureAwait(false);

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT catalog_version_id, family_key, type_name, preferred_junction_type
                FROM family_routing_type_settings
                WHERE catalog_version_id IN (
                    SELECT cv.id FROM catalog_versions cv
                    JOIN catalog_items ci ON ci.id = cv.catalog_item_id AND cv.version_label = ci.current_version_label)
                """;
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var versionId = reader.GetString(0);
                if (!versionRows.TryGetValue(versionId, out var version)) continue;
                version.VersionRoutingTypeSettings.Add(new ManifestRoutingTypeSettingV1
                {
                    FamilyKey = reader.GetString(1),
                    TypeName = reader.GetString(2),
                    PreferredJunctionType = reader.GetInt32(3),
                });
            }
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT fns.version_id, fns.nested_family_name
                FROM family_nested_shared_families fns
                WHERE fns.version_id IN (
                    SELECT cv.id FROM catalog_versions cv
                    JOIN catalog_items ci ON ci.id = cv.catalog_item_id AND cv.version_label = ci.current_version_label)
                """;
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var versionId = reader.GetString(0);
                if (!versionRows.TryGetValue(versionId, out var version)) continue;
                version.NestedSharedFamilies.Add(reader.GetString(1));
            }
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT fd.parent_version_id, fd.child_catalog_item_id, fd.dependency_kind,
                       fd.part_name, fd.ordinal, fd.child_version_label
                FROM family_dependencies fd
                WHERE fd.parent_version_id IN (
                    SELECT cv.id FROM catalog_versions cv
                    JOIN catalog_items ci ON ci.id = cv.catalog_item_id AND cv.version_label = ci.current_version_label)
                """;
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var versionId = reader.GetString(0);
                if (!versionRows.TryGetValue(versionId, out var version)) continue;
                version.Dependencies.Add(new ManifestDependencyV1
                {
                    ChildItemId = reader.GetString(1),
                    Kind = reader.GetString(2),
                    PartName = GetStringOrNull(reader, "part_name"),
                    Ordinal = reader.GetInt32(4),
                    ChildVersionLabel = GetStringOrNull(reader, "child_version_label"),
                });
            }
        }
    }

    private static async Task LoadRoutingRulesAsync(
        SqliteConnection connection,
        string baseSelect,
        string versionColumn,
        Dictionary<string, ManifestVersionV1> versionRows,
        Action<ManifestVersionV1, ManifestRoutingRuleV1> add,
        CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            {baseSelect}
            WHERE {versionColumn} IN (
                SELECT cv.id FROM catalog_versions cv
                JOIN catalog_items ci ON ci.id = cv.catalog_item_id AND cv.version_label = ci.current_version_label)
            """;
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var versionId = reader.GetString(0);
            if (!versionRows.TryGetValue(versionId, out var version)) continue;
            add(version, new ManifestRoutingRuleV1
            {
                FamilyKey = reader.GetString(1),
                TypeName = reader.GetString(2),
                GroupKey = reader.GetString(3),
                RuleOrder = reader.GetInt32(4),
                PartName = GetStringOrNull(reader, "part_name"),
                Description = reader.GetString(6),
                CriteriaJson = reader.GetString(7),
            });
        }
    }

    internal static string? GetStringOrNull(SqliteDataReader reader, string column)
    {
        var i = reader.GetOrdinal(column);
        return reader.IsDBNull(i) ? null : reader.GetString(i);
    }

    internal static int? GetIntOrNull(SqliteDataReader reader, string column)
    {
        var i = reader.GetOrdinal(column);
        return reader.IsDBNull(i) ? null : (int)reader.GetInt64(i);
    }

    internal static double? GetDoubleOrNull(SqliteDataReader reader, string column)
    {
        var i = reader.GetOrdinal(column);
        return reader.IsDBNull(i) ? null : reader.GetDouble(i);
    }

    internal static DateTimeOffset? GetDateOrNull(SqliteDataReader reader, string column)
    {
        var raw = GetStringOrNull(reader, column);
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
            ? value
            : null;
    }
}
