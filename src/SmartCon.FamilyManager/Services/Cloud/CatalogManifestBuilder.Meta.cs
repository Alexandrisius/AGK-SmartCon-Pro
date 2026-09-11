using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.FamilyManager.Models.Cloud;
using SmartCon.FamilyManager.Models.Metadata;

namespace SmartCon.FamilyManager.Services.Cloud;

/// <summary>Meta-слой манифеста: категории/атрибуты/биндинги/assignment rules — структуры metadata package v4.</summary>
internal sealed partial class CatalogManifestBuilder
{
    private sealed record CategoryRow(string Id, string Name, string? ParentId, int SortOrder);

    private async Task<ManifestMetaV1> BuildMetaAsync(
        SqliteConnection connection, Dictionary<string, string> categoryPaths, CancellationToken ct)
    {
        var categories = new List<CategoryRow>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT id, name, parent_id, sort_order FROM categories ORDER BY sort_order, name";
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                categories.Add(new CategoryRow(
                    reader.GetString(0), reader.GetString(1), GetStringOrNull(reader, "parent_id"), reader.GetInt32(3)));
            }
        }

        var meta = new ManifestMetaV1
        {
            Categories = BuildCategoryTree(categories, null),
        };

        var attributeNamesById = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT id, name, group_name FROM attribute_definitions WHERE is_active = 1 ORDER BY name";
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var id = reader.GetString(0);
                var name = reader.GetString(1);
                attributeNamesById[id] = name;
                meta.Attributes.Add(new MetadataExportAttribute { Name = name, Group = GetStringOrNull(reader, "group_name") });
            }
        }

        await LoadBindingsAsync(connection, meta, categoryPaths, attributeNamesById, ct).ConfigureAwait(false);
        await LoadAssignmentRulesAsync(connection, meta, categoryPaths, attributeNamesById, ct).ConfigureAwait(false);
        return meta;
    }

    private static List<MetadataExportCategoryNode> BuildCategoryTree(List<CategoryRow> categories, string? parentId)
    {
        return categories
            .Where(c => c.ParentId == parentId)
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new MetadataExportCategoryNode
            {
                Name = c.Name,
                Children = BuildCategoryTree(categories, c.Id),
            })
            .ToList();
    }

    private static async Task LoadBindingsAsync(
        SqliteConnection connection,
        ManifestMetaV1 meta,
        Dictionary<string, string> categoryPaths,
        Dictionary<string, string> attributeNamesById,
        CancellationToken ct)
    {
        var bindingsByCategory = new Dictionary<string, List<(string Id, string AttributeId, int SortOrder, bool IsEnabled)>>(
            StringComparer.Ordinal);
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id, category_id, attribute_id, sort_order, is_enabled
                FROM category_attribute_bindings
                ORDER BY category_id, sort_order
                """;
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var categoryId = reader.GetString(1);
                if (!bindingsByCategory.TryGetValue(categoryId, out var list))
                    bindingsByCategory[categoryId] = list = [];
                list.Add((reader.GetString(0), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4) != 0));
            }
        }

        var validationRules = new Dictionary<string, List<MetadataExportValidationRule>>(StringComparer.Ordinal);
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT binding_id, operator, value_text, value_number, min_value, max_value, is_enabled
                FROM category_validation_rules
                ORDER BY binding_id, sort_order
                """;
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var bindingId = reader.GetString(0);
                if (!validationRules.TryGetValue(bindingId, out var list))
                    validationRules[bindingId] = list = [];
                list.Add(new MetadataExportValidationRule
                {
                    Operator = reader.GetString(1),
                    ValueText = GetStringOrNull(reader, "value_text"),
                    ValueNumber = GetDoubleOrNull(reader, "value_number"),
                    MinValue = GetDoubleOrNull(reader, "min_value"),
                    MaxValue = GetDoubleOrNull(reader, "max_value"),
                    IsEnabled = reader.GetInt32(6) != 0,
                });
            }
        }

        foreach (var bindingEntry in bindingsByCategory)
        {
            if (!categoryPaths.TryGetValue(bindingEntry.Key, out var categoryPath)) continue;
            foreach (var binding in bindingEntry.Value)
            {
                if (!attributeNamesById.TryGetValue(binding.AttributeId, out var attributeName)) continue;
                validationRules.TryGetValue(binding.Id, out var rules);
                meta.Bindings.Add(new MetadataExportBinding
                {
                    CategoryPath = categoryPath,
                    AttributeName = attributeName,
                    SortOrder = binding.SortOrder,
                    IsEnabled = binding.IsEnabled,
                    ValidationRules = rules ?? [],
                });
            }
        }

        // Порядок детерминирован (CategoryPath, AttributeName): applier генерирует новые
        // category id — сортировка по id давала бы разный порядок между базами автора
        // и подписчика и ложную метадата-дельту при каждом sync.
        meta.Bindings.Sort((a, b) =>
        {
            var byPath = string.CompareOrdinal(a.CategoryPath, b.CategoryPath);
            return byPath != 0 ? byPath : string.CompareOrdinal(a.AttributeName, b.AttributeName);
        });
    }

    private static async Task LoadAssignmentRulesAsync(
        SqliteConnection connection,
        ManifestMetaV1 meta,
        Dictionary<string, string> categoryPaths,
        Dictionary<string, string> attributeNamesById,
        CancellationToken ct)
    {
        var groups = new List<(string Id, string CategoryId, int SortOrder, bool IsEnabled)>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id, category_id, sort_order, is_enabled
                FROM category_assignment_rule_groups
                ORDER BY category_id, sort_order
                """;
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                groups.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3) != 0));
        }

        var conditions = new Dictionary<string, List<MetadataExportAssignmentCondition>>(StringComparer.Ordinal);
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT group_id, source_kind, attribute_id, system_key, operator,
                       value_text, value_number, min_value, max_value, is_enabled
                FROM category_assignment_conditions
                ORDER BY group_id, sort_order
                """;
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var groupId = reader.GetString(0);
                var attributeId = GetStringOrNull(reader, "attribute_id");
                if (!conditions.TryGetValue(groupId, out var list))
                    conditions[groupId] = list = [];
                list.Add(new MetadataExportAssignmentCondition
                {
                    SourceKind = reader.GetString(1),
                    AttributeName = attributeId is not null && attributeNamesById.TryGetValue(attributeId, out var name)
                        ? name
                        : null,
                    SystemKey = GetStringOrNull(reader, "system_key"),
                    Operator = reader.GetString(4),
                    ValueText = GetStringOrNull(reader, "value_text"),
                    ValueNumber = GetDoubleOrNull(reader, "value_number"),
                    MinValue = GetDoubleOrNull(reader, "min_value"),
                    MaxValue = GetDoubleOrNull(reader, "max_value"),
                    IsEnabled = reader.GetInt32(9) != 0,
                });
            }
        }

        foreach (var group in groups)
        {
            if (!categoryPaths.TryGetValue(group.CategoryId, out var categoryPath)) continue;
            conditions.TryGetValue(group.Id, out var groupConditions);
            groupConditions ??= [];

            // Условие с неразрешимым атрибутом (неактивная definition) нельзя ни перенести
            // (CHECK V31 упадёт на apply), ни отбросить поодиночке (AND-группа изменит смысл —
            // станет срабатывать там, где у автора не срабатывала). Группа отбрасывается целиком.
            if (groupConditions.Any(c =>
                    string.Equals(c.SourceKind, "attribute", StringComparison.Ordinal) && c.AttributeName is null))
            {
                SmartConLogger.Warn(
                    $"Assignment group '{categoryPath}' #{group.SortOrder} dropped — its attribute condition " +
                    "references an inactive/deleted attribute definition. " +
                    "[Action: реактивируйте атрибут или удалите правило в редакторе категорий]");
                continue;
            }

            var existing = meta.AssignmentRules.FirstOrDefault(r => r.CategoryPath == categoryPath);
            if (existing is null)
            {
                existing = new MetadataExportAssignmentRule { CategoryPath = categoryPath };
                meta.AssignmentRules.Add(existing);
            }
            existing.Groups.Add(new MetadataExportAssignmentGroup
            {
                SortOrder = group.SortOrder,
                IsEnabled = group.IsEnabled,
                Conditions = groupConditions,
            });
        }

        // Детерминированный порядок по CategoryPath (новые category id у подписчика
        // иначе дают другой порядок и ложную метадата-дельту); группы — по SortOrder.
        meta.AssignmentRules.Sort((a, b) => string.CompareOrdinal(a.CategoryPath, b.CategoryPath));
        foreach (var rule in meta.AssignmentRules)
            rule.Groups.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
    }
}
