using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.FamilyManager.Models.Cloud;
using SmartCon.FamilyManager.Services.LocalCatalog;

namespace SmartCon.FamilyManager.Services.Cloud;

/// <summary>Вставка детальных таблиц item/version: types, параметры, хэши, routing, зависимости, ассеты.</summary>
public sealed partial class CatalogManifestApplier
{
    private static async Task InsertVersionDetailsAsync(
        SqliteConnection connection, SqliteTransaction tx,
        string itemId, string versionId, string fileId, ManifestVersionV1 version,
        DateTimeOffset now, CancellationToken ct)
    {
        // Terminal import run: данные извлечены автором манифеста — копия считает их проверенными.
        var runId = Guid.NewGuid().ToString();
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO family_data_import_runs (id, catalog_item_id, version_id, file_id,
                                                     revit_major_version, status, types_count,
                                                     started_at_utc, completed_at_utc)
                VALUES (@id, @itemId, @versionId, @fileId, @revit, 'Succeeded', @typesCount, @t, @t)
                """;
            cmd.Parameters.Add(new SqliteParameter("@id", runId));
            cmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
            cmd.Parameters.Add(new SqliteParameter("@versionId", versionId));
            cmd.Parameters.Add(new SqliteParameter("@fileId", fileId));
            cmd.Parameters.Add(new SqliteParameter("@revit", version.SourceRevitVersion));
            cmd.Parameters.Add(new SqliteParameter("@typesCount", version.TypesCount ?? version.Types.Count));
            cmd.Parameters.Add(new SqliteParameter("@t", now.ToString("o")));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // family_types: новые локальные id; параметры привязаны через принадлежность типу.
        foreach (var type in version.Types)
        {
            var newTypeId = Guid.NewGuid().ToString();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO family_types (id, catalog_item_id, type_name, sort_order, version_id, file_id,
                                              family_name, family_key, extraction_run_id)
                    VALUES (@id, @itemId, @typeName, @sortOrder, @versionId, @fileId, @familyName, @familyKey, @runId)
                    """;
                cmd.Parameters.Add(new SqliteParameter("@id", newTypeId));
                cmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
                cmd.Parameters.Add(new SqliteParameter("@typeName", type.TypeName));
                cmd.Parameters.Add(new SqliteParameter("@sortOrder", type.SortOrder));
                cmd.Parameters.Add(new SqliteParameter("@versionId", versionId));
                cmd.Parameters.Add(new SqliteParameter("@fileId", fileId));
                cmd.Parameters.Add(new SqliteParameter("@familyName", type.FamilyName));
                cmd.Parameters.Add(new SqliteParameter("@familyKey", type.FamilyKey));
                cmd.Parameters.Add(new SqliteParameter("@runId", runId));
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            foreach (var parameter in type.Parameters)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO extracted_attribute_values (id, catalog_item_id, version_id, file_id, type_id,
                                                            parameter_name, parameter_scope, storage_type,
                                                            value_text, value_raw, value_number, unit_type_id,
                                                            status, extraction_run_id, extracted_at_utc)
                    VALUES (@id, @itemId, @versionId, @fileId, @typeId,
                            @name, @scope, @storage,
                            @valueText, @valueRaw, @valueNumber, @unitTypeId,
                            @status, @runId, @t)
                    """;
                cmd.Parameters.Add(new SqliteParameter("@id", Guid.NewGuid().ToString()));
                cmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
                cmd.Parameters.Add(new SqliteParameter("@versionId", versionId));
                cmd.Parameters.Add(new SqliteParameter("@fileId", fileId));
                cmd.Parameters.Add(new SqliteParameter("@typeId", newTypeId));
                cmd.Parameters.Add(new SqliteParameter("@name", parameter.ParameterName));
                cmd.Parameters.Add(new SqliteParameter("@scope", (object?)parameter.ParameterScope ?? DBNull.Value));
                cmd.Parameters.Add(new SqliteParameter("@storage", parameter.StorageType));
                cmd.Parameters.Add(new SqliteParameter("@valueText", (object?)parameter.ValueText ?? DBNull.Value));
                cmd.Parameters.Add(new SqliteParameter("@valueRaw", (object?)parameter.ValueRaw ?? DBNull.Value));
                cmd.Parameters.Add(new SqliteParameter("@valueNumber", (object?)parameter.ValueNumber ?? DBNull.Value));
                cmd.Parameters.Add(new SqliteParameter("@unitTypeId", (object?)parameter.UnitTypeId ?? DBNull.Value));
                cmd.Parameters.Add(new SqliteParameter("@status", parameter.Status));
                cmd.Parameters.Add(new SqliteParameter("@runId", runId));
                cmd.Parameters.Add(new SqliteParameter("@t", now.ToString("o")));
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        foreach (var typeHash in version.TypeHashes)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO family_type_hashes (catalog_version_id, type_identity_key, type_name, type_hash, created_at_utc)
                VALUES (@versionId, @key, @typeName, @hash, @t)
                """;
            cmd.Parameters.Add(new SqliteParameter("@versionId", versionId));
            cmd.Parameters.Add(new SqliteParameter("@key", typeHash.TypeIdentityKey));
            cmd.Parameters.Add(new SqliteParameter("@typeName", typeHash.TypeName));
            cmd.Parameters.Add(new SqliteParameter("@hash", typeHash.TypeHash));
            cmd.Parameters.Add(new SqliteParameter("@t", now.ToString("o")));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var size in version.SegmentSizes)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO family_segment_sizes (catalog_version_id, segment_name, nominal_diameter,
                                                  inner_diameter, outer_diameter, used_in_size_lists,
                                                  used_in_sizing, sort_order)
                VALUES (@versionId, @name, @nominal, @inner, @outer, @usedInLists, @usedInSizing, @sortOrder)
                """;
            cmd.Parameters.Add(new SqliteParameter("@versionId", versionId));
            cmd.Parameters.Add(new SqliteParameter("@name", size.SegmentName));
            cmd.Parameters.Add(new SqliteParameter("@nominal", size.NominalDiameter));
            cmd.Parameters.Add(new SqliteParameter("@inner", size.InnerDiameter));
            cmd.Parameters.Add(new SqliteParameter("@outer", size.OuterDiameter));
            cmd.Parameters.Add(new SqliteParameter("@usedInLists", size.UsedInSizeLists ? 1 : 0));
            cmd.Parameters.Add(new SqliteParameter("@usedInSizing", size.UsedInSizing ? 1 : 0));
            cmd.Parameters.Add(new SqliteParameter("@sortOrder", size.SortOrder));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var rule in version.SegmentRules)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO family_segment_rules (catalog_version_id, family_key, type_name, rule_order,
                                                  segment_name, min_size_feet, max_size_feet, description)
                VALUES (@versionId, @familyKey, @typeName, @ruleOrder, @segmentName, @min, @max, @description)
                """;
            cmd.Parameters.Add(new SqliteParameter("@versionId", versionId));
            cmd.Parameters.Add(new SqliteParameter("@familyKey", rule.FamilyKey));
            cmd.Parameters.Add(new SqliteParameter("@typeName", rule.TypeName));
            cmd.Parameters.Add(new SqliteParameter("@ruleOrder", rule.RuleOrder));
            cmd.Parameters.Add(new SqliteParameter("@segmentName", rule.SegmentName));
            cmd.Parameters.Add(new SqliteParameter("@min", (object?)rule.MinSizeFeet ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@max", (object?)rule.MaxSizeFeet ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@description", rule.Description));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var rule in version.VersionRoutingRules)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO family_routing_rules (catalog_item_id, catalog_version_id, family_key, type_name,
                                                  group_key, rule_order, part_name, description, criteria_json)
                VALUES (@itemId, @versionId, @familyKey, @typeName, @groupKey, @ruleOrder, @partName, @description, @criteria)
                """;
            cmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
            cmd.Parameters.Add(new SqliteParameter("@versionId", versionId));
            cmd.Parameters.Add(new SqliteParameter("@familyKey", rule.FamilyKey));
            cmd.Parameters.Add(new SqliteParameter("@typeName", rule.TypeName));
            cmd.Parameters.Add(new SqliteParameter("@groupKey", rule.GroupKey));
            cmd.Parameters.Add(new SqliteParameter("@ruleOrder", rule.RuleOrder));
            cmd.Parameters.Add(new SqliteParameter("@partName", (object?)rule.PartName ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@description", rule.Description));
            cmd.Parameters.Add(new SqliteParameter("@criteria", rule.CriteriaJson));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var setting in version.VersionRoutingTypeSettings)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO family_routing_type_settings (catalog_version_id, family_key, type_name, preferred_junction_type)
                VALUES (@versionId, @familyKey, @typeName, @preferred)
                """;
            cmd.Parameters.Add(new SqliteParameter("@versionId", versionId));
            cmd.Parameters.Add(new SqliteParameter("@familyKey", setting.FamilyKey));
            cmd.Parameters.Add(new SqliteParameter("@typeName", setting.TypeName));
            cmd.Parameters.Add(new SqliteParameter("@preferred", setting.PreferredJunctionType));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var nestedName in version.NestedSharedFamilies)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO family_nested_shared_families (catalog_item_id, version_id, nested_family_name)
                VALUES (@itemId, @versionId, @name)
                """;
            cmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
            cmd.Parameters.Add(new SqliteParameter("@versionId", versionId));
            cmd.Parameters.Add(new SqliteParameter("@name", nestedName));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var dependency in version.Dependencies)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO family_dependencies (parent_catalog_item_id, parent_version_id, child_catalog_item_id,
                                                 dependency_kind, part_name, ordinal, child_version_label)
                VALUES (@itemId, @versionId, @childItemId, @kind, @partName, @ordinal, @childLabel)
                """;
            cmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
            cmd.Parameters.Add(new SqliteParameter("@versionId", versionId));
            cmd.Parameters.Add(new SqliteParameter("@childItemId", dependency.ChildItemId));
            cmd.Parameters.Add(new SqliteParameter("@kind", dependency.Kind));
            cmd.Parameters.Add(new SqliteParameter("@partName", (object?)dependency.PartName ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@ordinal", dependency.Ordinal));
            cmd.Parameters.Add(new SqliteParameter("@childLabel", (object?)dependency.ChildVersionLabel ?? DBNull.Value));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task InsertItemDetailsAsync(
        SqliteConnection connection, SqliteTransaction tx, ManifestItemV1 item,
        DateTimeOffset now, CancellationToken ct)
    {
        foreach (var tag in item.Tags)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO catalog_tags (catalog_item_id, tag, normalized_tag) VALUES (@itemId, @tag, @norm)";
            cmd.Parameters.Add(new SqliteParameter("@itemId", item.Id));
            cmd.Parameters.Add(new SqliteParameter("@tag", tag));
            cmd.Parameters.Add(new SqliteParameter("@norm", tag.ToLowerInvariant()));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var fact in item.Facts)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO family_facts (catalog_item_id, fact_key, value_key, value_display)
                VALUES (@itemId, @key, @valueKey, @valueDisplay)
                """;
            cmd.Parameters.Add(new SqliteParameter("@itemId", item.Id));
            cmd.Parameters.Add(new SqliteParameter("@key", fact.Key));
            cmd.Parameters.Add(new SqliteParameter("@valueKey", fact.ValueKey));
            cmd.Parameters.Add(new SqliteParameter("@valueDisplay", fact.ValueDisplay));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var rule in item.RoutingRules)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO item_routing_rules (catalog_item_id, family_key, type_name, group_key, rule_order,
                                                part_name, description, criteria_json)
                VALUES (@itemId, @familyKey, @typeName, @groupKey, @ruleOrder, @partName, @description, @criteria)
                """;
            cmd.Parameters.Add(new SqliteParameter("@itemId", item.Id));
            cmd.Parameters.Add(new SqliteParameter("@familyKey", rule.FamilyKey));
            cmd.Parameters.Add(new SqliteParameter("@typeName", rule.TypeName));
            cmd.Parameters.Add(new SqliteParameter("@groupKey", rule.GroupKey));
            cmd.Parameters.Add(new SqliteParameter("@ruleOrder", rule.RuleOrder));
            cmd.Parameters.Add(new SqliteParameter("@partName", (object?)rule.PartName ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@description", rule.Description));
            cmd.Parameters.Add(new SqliteParameter("@criteria", rule.CriteriaJson));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var setting in item.RoutingTypeSettings)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO item_routing_type_settings (catalog_item_id, family_key, type_name, preferred_junction_type)
                VALUES (@itemId, @familyKey, @typeName, @preferred)
                """;
            cmd.Parameters.Add(new SqliteParameter("@itemId", item.Id));
            cmd.Parameters.Add(new SqliteParameter("@familyKey", setting.FamilyKey));
            cmd.Parameters.Add(new SqliteParameter("@typeName", setting.TypeName));
            cmd.Parameters.Add(new SqliteParameter("@preferred", setting.PreferredJunctionType));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Ассеты и avatar: файлы из CAS + строки family_assets (путь — канонический по assetType).</summary>
    private static async Task<int> InsertAssetsAsync(
        SqliteConnection connection, SqliteTransaction tx, LocalCatalogDatabase database,
        ManifestItemV1 item, ICloudObjectSource objects, DateTimeOffset now, CancellationToken ct)
    {
        var written = 0;
        foreach (var asset in item.Assets)
        {
            var effectiveLabel = asset.VersionLabel ?? "shared";
            var relativePath = $"files/{item.Id}/{effectiveLabel}/assets/{AssetTypeFolder(asset.AssetType)}/{asset.FileName}";
            var absolutePath = Path.Combine(database.GetDatabaseRoot(), relativePath);
            var fileRef = new ManifestFileRefV1
            {
                Sha256 = asset.Sha256,
                SizeBytes = asset.SizeBytes,
                FileName = asset.FileName,
            };
            if (!await CopyObjectAsync(objects, fileRef, absolutePath, ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    $"Asset CAS object '{asset.Sha256}' (item '{item.Name}') unavailable or size mismatch — " +
                    "apply aborted, transaction rolled back");
            }

            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO family_assets (id, catalog_item_id, version_label, asset_type, file_name,
                                           relative_path, size_bytes, description, created_at_utc, is_primary)
                VALUES (@id, @itemId, @versionLabel, @assetType, @fileName, @path, @size, @description, @t, @isPrimary)
                """;
            cmd.Parameters.Add(new SqliteParameter("@id", Guid.NewGuid().ToString()));
            cmd.Parameters.Add(new SqliteParameter("@itemId", item.Id));
            cmd.Parameters.Add(new SqliteParameter("@versionLabel", (object?)asset.VersionLabel ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@assetType", asset.AssetType));
            cmd.Parameters.Add(new SqliteParameter("@fileName", asset.FileName));
            cmd.Parameters.Add(new SqliteParameter("@path", relativePath));
            cmd.Parameters.Add(new SqliteParameter("@size", asset.SizeBytes));
            cmd.Parameters.Add(new SqliteParameter("@description", (object?)asset.Description ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@t", now.ToString("o")));
            cmd.Parameters.Add(new SqliteParameter("@isPrimary", asset.IsPrimary ? 1 : 0));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            written++;
        }

        if (item.Avatar is not null)
        {
            var avatarPath = Path.Combine(database.GetDatabaseRoot(), "files", item.Id, "avatar.png");
            if (!await CopyObjectAsync(objects, item.Avatar, avatarPath, ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    $"Avatar CAS object '{item.Avatar.Sha256}' (item '{item.Name}') unavailable or size mismatch — " +
                    "apply aborted, transaction rolled back");
            }
            written++;
        }
        return written;
    }

    /// <summary>Тот же маппинг, что StoragePathResolver.GetAssetTypeFolder (строка assetType из манифеста).</summary>
    private static string AssetTypeFolder(string assetType) => assetType switch
    {
        "Image" => "images",
        "Video" => "videos",
        "Document" => "documents",
        "Model3D" => "models",
        "LookupTable" => "lookup",
        "Spreadsheet" => "spreadsheets",
        _ => "other",
    };
}
