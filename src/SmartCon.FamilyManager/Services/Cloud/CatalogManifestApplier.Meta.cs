using Microsoft.Data.Sqlite;
using SmartCon.FamilyManager.Models.Cloud;
using SmartCon.FamilyManager.Models.Metadata;

namespace SmartCon.FamilyManager.Services.Cloud;

/// <summary>Вставка meta-слоя (metadata package v4): категории, атрибуты, биндинги, assignment rules.</summary>
public sealed partial class CatalogManifestApplier
{
    /// <summary>Возвращает map «полный путь категории → id» для ремапа catalog_items.category_id.</summary>
    private static async Task<Dictionary<string, string>> InsertMetaAsync(
        SqliteConnection connection, SqliteTransaction tx, ManifestMetaV1 meta,
        DateTimeOffset now, CancellationToken ct)
    {
        var pathToId = new Dictionary<string, string>(StringComparer.Ordinal);
        await InsertCategoryNodesAsync(connection, tx, meta.Categories, string.Empty, pathToId, now, ct).ConfigureAwait(false);

        var attributeNameToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in meta.Attributes)
        {
            var id = Guid.NewGuid().ToString();
            attributeNameToId[attribute.Name] = id;
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO attribute_definitions (id, name, group_name, is_active, created_at_utc)
                VALUES (@id, @name, @group, 1, @t)
                """;
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            cmd.Parameters.Add(new SqliteParameter("@name", attribute.Name));
            cmd.Parameters.Add(new SqliteParameter("@group", (object?)attribute.Group ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@t", now.ToString("o")));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var binding in meta.Bindings)
        {
            if (!pathToId.TryGetValue(binding.CategoryPath, out var categoryId)) continue;
            if (!attributeNameToId.TryGetValue(binding.AttributeName, out var attributeId)) continue;
            var bindingId = Guid.NewGuid().ToString();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO category_attribute_bindings (id, category_id, attribute_id, sort_order, is_enabled)
                    VALUES (@id, @categoryId, @attributeId, @sortOrder, @isEnabled)
                    """;
                cmd.Parameters.Add(new SqliteParameter("@id", bindingId));
                cmd.Parameters.Add(new SqliteParameter("@categoryId", categoryId));
                cmd.Parameters.Add(new SqliteParameter("@attributeId", attributeId));
                cmd.Parameters.Add(new SqliteParameter("@sortOrder", binding.SortOrder));
                cmd.Parameters.Add(new SqliteParameter("@isEnabled", binding.IsEnabled ? 1 : 0));
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            foreach (var rule in binding.ValidationRules)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO category_validation_rules (binding_id, operator, value_text, value_number,
                                                          min_value, max_value, sort_order, is_enabled)
                    VALUES (@bindingId, @operator, @valueText, @valueNumber, @min, @max, @sortOrder, @isEnabled)
                    """;
                cmd.Parameters.Add(new SqliteParameter("@bindingId", bindingId));
                cmd.Parameters.Add(new SqliteParameter("@operator", rule.Operator));
                cmd.Parameters.Add(new SqliteParameter("@valueText", (object?)rule.ValueText ?? DBNull.Value));
                cmd.Parameters.Add(new SqliteParameter("@valueNumber", (object?)rule.ValueNumber ?? DBNull.Value));
                cmd.Parameters.Add(new SqliteParameter("@min", (object?)rule.MinValue ?? DBNull.Value));
                cmd.Parameters.Add(new SqliteParameter("@max", (object?)rule.MaxValue ?? DBNull.Value));
                // (object) обязателен: литерал 0 неявно конвертируется в SqliteType (enum) и выбирает не тот конструктор.
                cmd.Parameters.Add(new SqliteParameter("@sortOrder", (object)0));
                cmd.Parameters.Add(new SqliteParameter("@isEnabled", rule.IsEnabled ? 1 : 0));
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        foreach (var assignmentRule in meta.AssignmentRules)
        {
            if (!pathToId.TryGetValue(assignmentRule.CategoryPath, out var categoryId)) continue;
            foreach (var group in assignmentRule.Groups)
            {
                var groupId = Guid.NewGuid().ToString();
                using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        INSERT INTO category_assignment_rule_groups (id, category_id, sort_order, is_enabled)
                        VALUES (@id, @categoryId, @sortOrder, @isEnabled)
                        """;
                    cmd.Parameters.Add(new SqliteParameter("@id", groupId));
                    cmd.Parameters.Add(new SqliteParameter("@categoryId", categoryId));
                    cmd.Parameters.Add(new SqliteParameter("@sortOrder", group.SortOrder));
                    cmd.Parameters.Add(new SqliteParameter("@isEnabled", group.IsEnabled ? 1 : 0));
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                foreach (var condition in group.Conditions)
                {
                    // attribute_id/system_key взаимоисключающие (CHECK V31):AttributeName берём только для sourceKind=attribute.
                    string? attributeId = string.Equals(condition.SourceKind, "attribute", StringComparison.Ordinal)
                        && condition.AttributeName is not null
                        && attributeNameToId.TryGetValue(condition.AttributeName, out var id)
                            ? id
                            : null;
                    using var cmd = connection.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        INSERT INTO category_assignment_conditions (group_id, source_kind, attribute_id, system_key,
                                                                    operator, value_text, value_number, min_value,
                                                                    max_value, sort_order, is_enabled)
                        VALUES (@groupId, @sourceKind, @attributeId, @systemKey,
                                @operator, @valueText, @valueNumber, @min,
                                @max, @sortOrder, @isEnabled)
                        """;
                    cmd.Parameters.Add(new SqliteParameter("@groupId", groupId));
                    cmd.Parameters.Add(new SqliteParameter("@sourceKind", condition.SourceKind));
                    cmd.Parameters.Add(new SqliteParameter("@attributeId", (object?)attributeId ?? DBNull.Value));
                    cmd.Parameters.Add(new SqliteParameter("@systemKey", (object?)condition.SystemKey ?? DBNull.Value));
                    cmd.Parameters.Add(new SqliteParameter("@operator", condition.Operator));
                    cmd.Parameters.Add(new SqliteParameter("@valueText", (object?)condition.ValueText ?? DBNull.Value));
                    cmd.Parameters.Add(new SqliteParameter("@valueNumber", (object?)condition.ValueNumber ?? DBNull.Value));
                    cmd.Parameters.Add(new SqliteParameter("@min", (object?)condition.MinValue ?? DBNull.Value));
                    cmd.Parameters.Add(new SqliteParameter("@max", (object?)condition.MaxValue ?? DBNull.Value));
                    // (object) обязателен: литерал 0 неявно конвертируется в SqliteType (enum) и выбирает не тот конструктор.
                    cmd.Parameters.Add(new SqliteParameter("@sortOrder", (object)0));
                    cmd.Parameters.Add(new SqliteParameter("@isEnabled", condition.IsEnabled ? 1 : 0));
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }
        }

        return pathToId;
    }

    private static async Task InsertCategoryNodesAsync(
        SqliteConnection connection, SqliteTransaction tx,
        List<MetadataExportCategoryNode> nodes, string parentPath,
        Dictionary<string, string> pathToId, DateTimeOffset now, CancellationToken ct)
    {
        var sortOrder = 0;
        foreach (var node in nodes)
        {
            var id = Guid.NewGuid().ToString();
            var fullPath = parentPath.Length == 0 ? node.Name : $"{parentPath} > {node.Name}";
            pathToId[fullPath] = id;
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO categories (id, name, parent_id, sort_order, created_at_utc)
                VALUES (@id, @name, @parentId, @sortOrder, @t)
                """;
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            cmd.Parameters.Add(new SqliteParameter("@name", node.Name));
            cmd.Parameters.Add(new SqliteParameter("@parentId",
                pathToId.TryGetValue(parentPath, out var parentId) ? parentId : (object?)DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@sortOrder", sortOrder++));
            cmd.Parameters.Add(new SqliteParameter("@t", now.ToString("o")));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            await InsertCategoryNodesAsync(connection, tx, node.Children, fullPath, pathToId, now, ct).ConfigureAwait(false);
        }
    }
}
