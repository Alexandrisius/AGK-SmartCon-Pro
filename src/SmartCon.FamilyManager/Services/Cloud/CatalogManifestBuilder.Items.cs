using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Models.Cloud;
using SmartCon.FamilyManager.Services.LocalCatalog;

namespace SmartCon.FamilyManager.Services.Cloud;

/// <summary>Чтение items/версий/типов/ассетов и хеширование файлов managed storage.</summary>
internal sealed partial class CatalogManifestBuilder
{
    private sealed record ActiveRow(
        string ItemId, string VersionId, string FileId, string RelativePath);

    private async Task<List<ManifestItemV1>> LoadItemsAsync(
        SqliteConnection connection, Dictionary<string, string> categoryPaths, CancellationToken ct)
    {
        // Items без валидной активной версии (current_version_label NULL или битый)
        // в манифест не попадают — JOIN молча их исключает, поэтому сообщаем оператору отдельно.
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT COUNT(*) FROM catalog_items ci
                WHERE ci.current_version_label IS NULL
                   OR NOT EXISTS (SELECT 1 FROM catalog_versions cv
                                  WHERE cv.catalog_item_id = ci.id AND cv.version_label = ci.current_version_label)
                """;
            var orphaned = (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
            if (orphaned > 0)
            {
                SmartConLogger.Warn(
                    $"{orphaned} item(s) have no valid active version — they will NOT be published. " +
                    "[Action: откройте эти семейства в каталоге и пересоздайте активную версию, либо удалите их]");
            }
        }

        var items = new List<ManifestItemV1>();
        var byId = new Dictionary<string, ManifestItemV1>(StringComparer.Ordinal);
        var versionRows = new Dictionary<string, ManifestVersionV1>(StringComparer.Ordinal);

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT ci.id, ci.name, ci.normalized_name, ci.description, ci.category_id,
                       ci.content_status, ci.current_version_label, ci.family_source,
                       ci.revit_category, ci.revit_category_id,
                       cv.id AS version_id, cv.revit_major_version, cv.types_count, cv.parameters_count,
                       cv.content_hash, cv.glb_state, cv.routing_backfilled,
                       cv.section_hashes, cv.section_strings,
                       cv.published_at_utc, cv.published_by, cv.file_id,
                       ff.relative_path, ff.file_name
                FROM catalog_items ci
                JOIN catalog_versions cv
                    ON cv.catalog_item_id = ci.id AND cv.version_label = ci.current_version_label
                JOIN family_files ff ON ff.id = cv.file_id
                ORDER BY ci.normalized_name, cv.revit_major_version
                """;
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var itemId = reader.GetString(reader.GetOrdinal("id"));
                if (!byId.TryGetValue(itemId, out var item))
                {
                    var categoryId = GetStringOrNull(reader, "category_id");
                    item = new ManifestItemV1
                    {
                        Id = itemId,
                        Name = reader.GetString(reader.GetOrdinal("name")),
                        NormalizedName = reader.GetString(reader.GetOrdinal("normalized_name")),
                        Description = GetStringOrNull(reader, "description"),
                        CategoryPath = categoryId is not null && categoryPaths.TryGetValue(categoryId, out var path)
                            ? path
                            : string.Empty,
                        // Wire-контракт — двух-state Active|Deprecated: legacy "Retired" маппится на границе (§5, known-workarounds #109).
                        ContentStatus = ContentStatusParser.Parse(reader.GetString(reader.GetOrdinal("content_status"))).ToString(),
                        FamilySource = reader.GetString(reader.GetOrdinal("family_source")),
                        RevitCategory = GetStringOrNull(reader, "revit_category"),
                        RevitCategoryId = GetIntOrNull(reader, "revit_category_id"),
                        CurrentVersionLabel = reader.GetString(reader.GetOrdinal("current_version_label")),
                    };
                    byId[itemId] = item;
                    items.Add(item);
                }

                var versionId = reader.GetString(reader.GetOrdinal("version_id"));
                var relativePath = reader.GetString(reader.GetOrdinal("relative_path"));
                var filePath = Path.Combine(_pathResolver.GetDatabaseRoot(), relativePath);
                var fileHash = await TryHashFileAsync(filePath, ct).ConfigureAwait(false);
                if (fileHash is null)
                {
                    SmartConLogger.Warn(
                        $"File missing on disk, version skipped: '{relativePath}' (item '{item.Name}'). " +
                        "[Action: переимпортируйте семейство или удалите битую версию перед публикацией]");
                    continue;
                }

                var version = new ManifestVersionV1
                {
                    VersionLabel = item.CurrentVersionLabel,
                    ContentHash = GetStringOrNull(reader, "content_hash"),
                    SectionHashes = ParseStringDictionary(GetStringOrNull(reader, "section_hashes")),
                    SectionStrings = ParseStringDictionary(GetStringOrNull(reader, "section_strings")),
                    SourceRevitVersion = reader.GetInt32(reader.GetOrdinal("revit_major_version")),
                    FileKind = string.Equals(item.FamilySource, "system", StringComparison.Ordinal) ? "stagedRvt" : "rfa",
                    TypesCount = GetIntOrNull(reader, "types_count"),
                    ParametersCount = GetIntOrNull(reader, "parameters_count"),
                    PublishedAtUtc = GetDateOrNull(reader, "published_at_utc"),
                    PublishedBy = GetStringOrNull(reader, "published_by"),
                    GlbState = GetIntOrNull(reader, "glb_state"),
                    RoutingBackfilled = GetIntOrNull(reader, "routing_backfilled"),
                    File = new ManifestFileRefV1
                    {
                        Sha256 = fileHash.Value.Sha256,
                        SizeBytes = fileHash.Value.SizeBytes,
                        FileName = reader.GetString(reader.GetOrdinal("file_name")),
                    },
                };
                item.Versions.Add(version);
                versionRows[versionId] = version;
            }
        }

        // Items, у которых не осталось ни одного файла — исключаем целиком
        // (и из словаря, чтобы ассеты/аватары битых items не хешировались впустую).
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (items[i].Versions.Count != 0) continue;
            byId.Remove(items[i].Id);
            items.RemoveAt(i);
        }

        if (items.Count == 0) return items;

        await LoadTypesAndParametersAsync(connection, versionRows, ct).ConfigureAwait(false);
        await LoadVersionDetailsAsync(connection, versionRows, ct).ConfigureAwait(false);
        await LoadItemDetailsAsync(connection, byId, ct).ConfigureAwait(false);
        ResolveSystemFamilyKeys(items);
        return items;
    }

    /// <summary>familyKey item (ADR-064): первый непустой family_key типов активной версии.</summary>
    private static void ResolveSystemFamilyKeys(List<ManifestItemV1> items)
    {
        foreach (var item in items)
        {
            if (!string.Equals(item.FamilySource, "system", StringComparison.Ordinal)) continue;
            item.FamilyKey ??= item.Versions
                .SelectMany(v => v.Types)
                .Select(t => t.FamilyKey)
                .FirstOrDefault(k => !string.IsNullOrEmpty(k));
        }
    }

    private async Task LoadTypesAndParametersAsync(
        SqliteConnection connection, Dictionary<string, ManifestVersionV1> versionRows, CancellationToken ct)
    {
        // Id строк family_types в wire-формат не входит (параметры привязаны вложенностью);
        // локальный словарь нужен только для матчинга extracted_attribute_values.type_id.
        var typesByVersion = new Dictionary<string, List<ManifestTypeV1>>(StringComparer.Ordinal);
        var typeById = new Dictionary<string, ManifestTypeV1>(StringComparer.Ordinal);
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT ft.id, ft.version_id, ft.type_name, ft.family_name, ft.family_key, ft.sort_order
                FROM family_types ft
                WHERE ft.version_id IN (
                    SELECT cv.id FROM catalog_versions cv
                    JOIN catalog_items ci ON ci.id = cv.catalog_item_id AND cv.version_label = ci.current_version_label)
                ORDER BY ft.sort_order, ft.type_name
                """;
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var versionId = reader.GetString(reader.GetOrdinal("version_id"));
                if (!versionRows.TryGetValue(versionId, out var version)) continue;
                var type = new ManifestTypeV1
                {
                    TypeName = reader.GetString(reader.GetOrdinal("type_name")),
                    FamilyName = reader.GetString(reader.GetOrdinal("family_name")),
                    FamilyKey = reader.GetString(reader.GetOrdinal("family_key")),
                    SortOrder = reader.GetInt32(reader.GetOrdinal("sort_order")),
                };
                typeById[reader.GetString(reader.GetOrdinal("id"))] = type;
                if (!typesByVersion.TryGetValue(versionId, out var list))
                    typesByVersion[versionId] = list = [];
                list.Add(type);
            }
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT eav.type_id, eav.parameter_name, eav.parameter_scope, eav.storage_type,
                       eav.value_text, eav.value_raw, eav.value_number, eav.unit_type_id, eav.status
                FROM extracted_attribute_values eav
                WHERE eav.version_id IN (
                    SELECT cv.id FROM catalog_versions cv
                    JOIN catalog_items ci ON ci.id = cv.catalog_item_id AND cv.version_label = ci.current_version_label)
                ORDER BY eav.parameter_name
                """;
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var typeId = GetStringOrNull(reader, "type_id");
                if (typeId is null || !typeById.TryGetValue(typeId, out var type)) continue;
                type.Parameters.Add(new ManifestParameterV1
                {
                    ParameterName = reader.GetString(reader.GetOrdinal("parameter_name")),
                    ParameterScope = GetStringOrNull(reader, "parameter_scope"),
                    StorageType = reader.GetString(reader.GetOrdinal("storage_type")),
                    ValueText = GetStringOrNull(reader, "value_text"),
                    ValueRaw = GetStringOrNull(reader, "value_raw"),
                    ValueNumber = GetDoubleOrNull(reader, "value_number"),
                    UnitTypeId = GetStringOrNull(reader, "unit_type_id"),
                    Status = GetStringOrNull(reader, "status") ?? "Found",
                });
            }
        }

        foreach (var entry in typesByVersion)
            versionRows[entry.Key].Types.AddRange(entry.Value);
    }

    private async Task LoadItemDetailsAsync(
        SqliteConnection connection,
        Dictionary<string, ManifestItemV1> byId,
        CancellationToken ct)
    {
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT catalog_item_id, tag FROM catalog_tags
                WHERE catalog_item_id IN (
                    SELECT ci.id FROM catalog_items ci
                    WHERE EXISTS (SELECT 1 FROM catalog_versions cv
                                  WHERE cv.catalog_item_id = ci.id AND cv.version_label = ci.current_version_label))
                ORDER BY tag
                """;
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (byId.TryGetValue(reader.GetString(0), out var item))
                    item.Tags.Add(reader.GetString(1));
            }
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT catalog_item_id, fact_key, value_key, value_display FROM family_facts
                WHERE catalog_item_id IN (
                    SELECT ci.id FROM catalog_items ci
                    WHERE EXISTS (SELECT 1 FROM catalog_versions cv
                                  WHERE cv.catalog_item_id = ci.id AND cv.version_label = ci.current_version_label))
                ORDER BY fact_key
                """;
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (byId.TryGetValue(reader.GetString(0), out var item))
                    item.Facts.Add(new ManifestFactV1
                    {
                        Key = reader.GetString(1),
                        ValueKey = reader.GetString(2),
                        ValueDisplay = reader.GetString(3),
                    });
            }
        }

        // Ассеты: всё, кроме GLB-превью из CAS-пула (files/_shared/models/ —
        // превью генерятся локально подписчиком, §5).
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT catalog_item_id, asset_type, file_name, relative_path, size_bytes,
                       is_primary, version_label, description
                FROM family_assets
                WHERE catalog_item_id IN (
                    SELECT ci.id FROM catalog_items ci
                    WHERE EXISTS (SELECT 1 FROM catalog_versions cv
                                  WHERE cv.catalog_item_id = ci.id AND cv.version_label = ci.current_version_label))
                  AND NOT (asset_type = 'Model3D' AND relative_path LIKE 'files/_shared/models/%')
                """;
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!byId.TryGetValue(reader.GetString(0), out var item)) continue;
                var relativePath = reader.GetString(reader.GetOrdinal("relative_path"));
                var fileHash = await TryHashFileAsync(
                    Path.Combine(_pathResolver.GetDatabaseRoot(), relativePath), ct).ConfigureAwait(false);
                if (fileHash is null)
                {
                    SmartConLogger.Warn(
                        $"Asset file missing on disk, skipped: '{relativePath}' (item '{item.Name}'). " +
                        "[Action: удалите битую запись ассета или восстановите файл]");
                    continue;
                }
                item.Assets.Add(new ManifestAssetV1
                {
                    AssetType = reader.GetString(reader.GetOrdinal("asset_type")),
                    FileName = reader.GetString(reader.GetOrdinal("file_name")),
                    Sha256 = fileHash.Value.Sha256,
                    SizeBytes = fileHash.Value.SizeBytes,
                    IsPrimary = reader.GetInt32(reader.GetOrdinal("is_primary")) != 0,
                    VersionLabel = GetStringOrNull(reader, "version_label"),
                    Description = GetStringOrNull(reader, "description") ?? string.Empty,
                });
            }
        }

        // Avatar — производный файл files/{itemId}/avatar.png (ADR-047).
        foreach (var item in byId.Values)
        {
            var avatarPath = _pathResolver.GetAvatarPath(item.Id);
            var avatarHash = await TryHashFileAsync(avatarPath, ct).ConfigureAwait(false);
            if (avatarHash is null) continue;
            item.Avatar = new ManifestFileRefV1
            {
                Sha256 = avatarHash.Value.Sha256,
                SizeBytes = avatarHash.Value.SizeBytes,
                FileName = StoragePathResolver.AvatarFileName,
            };
        }

        // Item-level routing World B (V37).
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT catalog_item_id, family_key, type_name, group_key, rule_order,
                       part_name, description, criteria_json
                FROM item_routing_rules
                WHERE catalog_item_id IN (
                    SELECT ci.id FROM catalog_items ci
                    WHERE EXISTS (SELECT 1 FROM catalog_versions cv
                                  WHERE cv.catalog_item_id = ci.id AND cv.version_label = ci.current_version_label))
                """;
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!byId.TryGetValue(reader.GetString(0), out var item)) continue;
                item.RoutingRules.Add(new ManifestRoutingRuleV1
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

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT catalog_item_id, family_key, type_name, preferred_junction_type
                FROM item_routing_type_settings
                WHERE catalog_item_id IN (
                    SELECT ci.id FROM catalog_items ci
                    WHERE EXISTS (SELECT 1 FROM catalog_versions cv
                                  WHERE cv.catalog_item_id = ci.id AND cv.version_label = ci.current_version_label))
                """;
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!byId.TryGetValue(reader.GetString(0), out var item)) continue;
                item.RoutingTypeSettings.Add(new ManifestRoutingTypeSettingV1
                {
                    FamilyKey = reader.GetString(1),
                    TypeName = reader.GetString(2),
                    PreferredJunctionType = reader.GetInt32(3),
                });
            }
        }
    }

    /// <summary>Потоковый SHA-256 файла (регистронезависимый hex — канонизация сервера).</summary>
    private static async Task<(string Sha256, long SizeBytes)?> TryHashFileAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        using var sha = SHA256.Create();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var size = stream.Length;
#if NET8_0_OR_GREATER
        var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
#else
        var hash = sha.ComputeHash(stream);
#endif
        return (BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant(), size);
    }

    private static Dictionary<string, string>? ParseStringDictionary(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            var result = JsonSerializer.Deserialize<Dictionary<string, string>>(json!);
            return result is { Count: > 0 } ? result : null;
        }
        catch (JsonException)
        {
            // Битые V33-словари (hand-edit) не должны валить публикацию — просто теряют детализацию.
            return null;
        }
    }

    private static async Task<Dictionary<string, string>> LoadCategoryPathsAsync(SqliteConnection connection, CancellationToken ct)
    {
        var categories = new Dictionary<string, (string Name, string? ParentId)>(StringComparer.Ordinal);
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT id, name, parent_id FROM categories";
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                categories[reader.GetString(0)] = (reader.GetString(1), GetStringOrNull(reader, "parent_id"));
            }
        }

        // Тот же формат, что CategoryTree.BuildFullPath (" > ") — v4-пакет и bindings его разделяют.
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in categories.Keys)
        {
            var parts = new Stack<string>();
            (string Name, string? ParentId)? current = categories[id];
            var guard = 0;
            while (current is not null && guard++ < 64)
            {
                parts.Push(current.Value.Name);
                current = current.Value.ParentId is not null && categories.TryGetValue(current.Value.ParentId, out var parent)
                    ? parent
                    : null;
            }
            paths[id] = string.Join(" > ", parts);
        }
        return paths;
    }
}
