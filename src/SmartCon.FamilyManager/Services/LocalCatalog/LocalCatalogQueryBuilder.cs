using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal static class LocalCatalogQueryBuilder
{
    public static (string Sql, List<SqliteParameter> Parameters) BuildWhereClause(FamilyCatalogQuery query)
    {
        var conditions = new List<string>();
        var parameters = new List<SqliteParameter>();
        var paramIndex = 0;

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var tokens = Core.Services.FamilyManager.FamilySearchNormalizer.Tokenize(query.SearchText!);
            foreach (var token in tokens)
            {
                var paramName = $"@search_{paramIndex++}";
                conditions.Add($"""
                    (ci.normalized_name LIKE {paramName}
                     OR EXISTS (
                         SELECT 1 FROM catalog_tags ct
                         WHERE ct.catalog_item_id = ci.id AND ct.normalized_tag LIKE {paramName}
                     ))
                    """);
                parameters.Add(new SqliteParameter(paramName, $"%{token}%"));
            }
        }

        // Category filters are composed with OR, not chained with else-if.
        // The caller may legitimately want to combine them — e.g.
        // ["__no_category__", "real-id-1", "real-id-2"] should match BOTH
        // uncategorized rows AND rows in the two named categories. Using
        // else-if would silently drop the IN-clause when IncludeUncategorized
        // is set, and would also drop the recursive CategoryFilter when
        // CategoryIdsFilter is set. Each condition is a separate SQL predicate
        // that contributes one AND to the final WHERE.
        if (query.IncludeUncategorized)
        {
            conditions.Add("(ci.category_id IS NULL OR ci.category_id = '')");
        }
        // Import Validation Gate (quarantine zone): read-only roles never
        // see uncategorized families — editors distribute them from the
        // quarantine into rule-protected categories.
        if (query.ExcludeUncategorized)
        {
            conditions.Add("(ci.category_id IS NOT NULL AND ci.category_id != '')");
        }
        if (query.CategoryIdsFilter is { Count: > 0 })
        {
            var placeholders = new List<string>(query.CategoryIdsFilter.Count);
            foreach (var cid in query.CategoryIdsFilter)
            {
                var paramName = $"@category_{paramIndex++}";
                placeholders.Add(paramName);
                parameters.Add(new SqliteParameter(paramName, cid));
            }
            conditions.Add($"ci.category_id IN ({string.Join(", ", placeholders)})");
        }
        if (!string.IsNullOrWhiteSpace(query.CategoryFilter))
        {
            var paramName = $"@category_{paramIndex++}";
            conditions.Add($"""
                ci.category_id IN (
                    WITH RECURSIVE subtree AS (
                        SELECT id FROM categories WHERE id = {paramName}
                        UNION ALL
                        SELECT c.id FROM categories c JOIN subtree s ON c.parent_id = s.id
                    )
                    SELECT id FROM subtree
                )
                """);
            parameters.Add(new SqliteParameter(paramName, query.CategoryFilter));
        }

        if (query.StatusFilter is not null)
        {
            var paramName = $"@status_{paramIndex++}";
            conditions.Add($"ci.content_status = {paramName}");
            parameters.Add(new SqliteParameter(paramName, query.StatusFilter.Value.ToString()));
        }

        if (query.Tags is not null && query.Tags.Count > 0)
        {
            for (var i = 0; i < query.Tags.Count; i++)
            {
                var normalizedTag = Core.Services.FamilyManager.FamilySearchNormalizer.Normalize(query.Tags[i]);
                var paramName = $"@tag_{paramIndex++}";
                conditions.Add($"""
                    EXISTS (
                        SELECT 1 FROM catalog_tags ct
                        WHERE ct.catalog_item_id = ci.id AND ct.normalized_tag = {paramName}
                    )
                    """);
                parameters.Add(new SqliteParameter(paramName, normalizedTag));
            }
        }

        AddAttributeFilterConditions(query, conditions, parameters, ref paramIndex);

        var where = conditions.Count > 0
            ? "WHERE " + string.Join(" AND ", conditions)
            : "";

        return (where, parameters);
    }

    // ── Advanced attribute search (#87) ────────────────────────────────
    // Every condition becomes one AND-ed EXISTS/NOT EXISTS predicate over
    // extracted_attribute_values of the item's ACTIVE version (the same
    // rows the properties dialog's ATTRIBUTES tab shows — resolved via
    // catalog_items.current_version_label). Version-less rows
    // (version_id IS NULL) are legacy item-level facts and match too.

    private static void AddAttributeFilterConditions(
        FamilyCatalogQuery query,
        List<string> conditions,
        List<SqliteParameter> parameters,
        ref int paramIndex)
    {
        if (query.AttributeFilters is not { Count: > 0 })
        {
            return;
        }

        foreach (var filter in query.AttributeFilters)
        {
            if (filter.SourceKind == AssignmentConditionSourceKind.System)
            {
                if (filter.SystemField is null)
                {
                    continue;
                }
            }
            else if (string.IsNullOrEmpty(filter.AttributeId) && string.IsNullOrEmpty(filter.AttributeName))
            {
                continue;
            }

            conditions.Add(BuildAttributePredicate(filter, parameters, ref paramIndex));
        }
    }

    private static string BuildAttributePredicate(
        AttributeFilterCondition filter,
        List<SqliteParameter> parameters,
        ref int paramIndex)
    {
        if (filter.SourceKind == AssignmentConditionSourceKind.System && filter.SystemField is { } field)
        {
            return BuildSystemFieldPredicate(field, filter.Operator, filter.Value, parameters, ref paramIndex);
        }

        var attrIdParam = $"@attrId_{paramIndex++}";
        parameters.Add(new SqliteParameter(attrIdParam, filter.AttributeId));
        var attrNameParam = $"@attrName_{paramIndex++}";
        parameters.Add(new SqliteParameter(attrNameParam, filter.AttributeName));

        var matchPredicate = $"""
                    av.attribute_id = {attrIdParam}
                    OR (av.attribute_id IS NULL AND av.parameter_name = {attrNameParam})
            """;

        return filter.Operator switch
        {
            ValidationRuleOperator.HasValue => BuildExists(matchPredicate, "av.status = 'Found' AND TRIM(COALESCE(av.value_text, '')) != ''"),
            ValidationRuleOperator.IsEmpty => "NOT " + BuildExists(matchPredicate, "av.status = 'Found' AND TRIM(COALESCE(av.value_text, '')) != ''"),
            ValidationRuleOperator.Equals => BuildExists(matchPredicate, BuildValueEqualsPredicate(filter.Value, parameters, ref paramIndex)),
            ValidationRuleOperator.NotEquals => "NOT " + BuildExists(matchPredicate, BuildValueEqualsPredicate(filter.Value, parameters, ref paramIndex)),
            ValidationRuleOperator.Contains => BuildExists(matchPredicate, BuildValueContainsPredicate(filter.Value, parameters, ref paramIndex)),
            ValidationRuleOperator.NotContains => "NOT " + BuildExists(matchPredicate, BuildValueContainsPredicate(filter.Value, parameters, ref paramIndex)),
            _ => throw new ArgumentOutOfRangeException(nameof(filter), filter.Operator, "Unsupported attribute filter operator"),
        };
    }

    /// <summary>
    /// System-field conditions (#87, vocabulary of #241): family name is
    /// plain text on catalog_items; Revit category compares the stored
    /// BuiltInCategory ordinal; Part Type lives in family_facts (PK
    /// catalog_item_id + fact_key — at most one row per item).
    /// NotEquals is the exact complement of Equals (an absent value counts
    /// as "not equal"), consistent with the attribute conditions.
    /// </summary>
    private static string BuildSystemFieldPredicate(
        AssignmentSystemField field,
        ValidationRuleOperator op,
        string? value,
        List<SqliteParameter> parameters,
        ref int paramIndex)
    {
        return field switch
        {
            AssignmentSystemField.FamilyName => op switch
            {
                ValidationRuleOperator.Equals => BuildNameEquals(value, parameters, ref paramIndex, negate: false),
                ValidationRuleOperator.NotEquals => BuildNameEquals(value, parameters, ref paramIndex, negate: true),
                ValidationRuleOperator.Contains => BuildNameContains(value, parameters, ref paramIndex, negate: false),
                ValidationRuleOperator.NotContains => BuildNameContains(value, parameters, ref paramIndex, negate: true),
                _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Unsupported family-name filter operator"),
            },
            AssignmentSystemField.RevitCategory => op switch
            {
                ValidationRuleOperator.Equals => $"ci.revit_category_id = {AddOrdinalParam(value, parameters, ref paramIndex)}",
                ValidationRuleOperator.NotEquals => $"(ci.revit_category_id IS NULL OR ci.revit_category_id != {AddOrdinalParam(value, parameters, ref paramIndex)})",
                ValidationRuleOperator.HasValue => "ci.revit_category_id IS NOT NULL",
                ValidationRuleOperator.IsEmpty => "ci.revit_category_id IS NULL",
                _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Unsupported Revit category filter operator"),
            },
            AssignmentSystemField.PartType => BuildPartTypePredicate(op, value, parameters, ref paramIndex),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unsupported system field in advanced search"),
        };
    }

    private static string BuildNameEquals(string? value, List<SqliteParameter> parameters, ref int paramIndex, bool negate)
    {
        var param = $"@name_{paramIndex++}";
        parameters.Add(new SqliteParameter(param, value ?? string.Empty));
        var predicate = $"ci.name = {param} COLLATE NOCASE";
        return negate ? $"NOT ({predicate})" : predicate;
    }

    private static string BuildNameContains(string? value, List<SqliteParameter> parameters, ref int paramIndex, bool negate)
    {
        var param = $"@name_{paramIndex++}";
        parameters.Add(new SqliteParameter(param, $"%{EscapeLikePattern(value ?? string.Empty)}%"));
        var predicate = $"ci.name LIKE {param} ESCAPE '\\'";
        return negate ? $"NOT ({predicate})" : predicate;
    }

    private static string AddOrdinalParam(string? value, List<SqliteParameter> parameters, ref int paramIndex)
    {
        var param = $"@ordinal_{paramIndex++}";
        var parsed = long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var ordinal)
            ? ordinal
            : (long?)null;
        parameters.Add(new SqliteParameter(param, (object?)parsed ?? DBNull.Value));
        return param;
    }

    private static string BuildPartTypePredicate(
        ValidationRuleOperator op, string? value, List<SqliteParameter> parameters, ref int paramIndex)
    {
        var keyParam = $"@factKey_{paramIndex++}";
        parameters.Add(new SqliteParameter(keyParam, FamilyFactRuleSet.PartTypeFactKey));

        var withValue = op is ValidationRuleOperator.Equals or ValidationRuleOperator.NotEquals;
        var valueClause = string.Empty;
        if (withValue)
        {
            var valueParam = $"@factValue_{paramIndex++}";
            parameters.Add(new SqliteParameter(valueParam, value ?? string.Empty));
            valueClause = $" AND ff.value_key = {valueParam}";
        }

        var exists = $"EXISTS (SELECT 1 FROM family_facts ff WHERE ff.catalog_item_id = ci.id AND ff.fact_key = {keyParam}{valueClause})";
        return op is ValidationRuleOperator.NotEquals or ValidationRuleOperator.IsEmpty
            ? $"NOT {exists}"
            : exists;
    }

    private static string BuildExists(string matchPredicate, string valuePredicate) => $"""
        EXISTS (
            SELECT 1 FROM extracted_attribute_values av
            WHERE av.catalog_item_id = ci.id
              AND (
                  av.version_id IS NULL
                  OR av.version_id = (
                      SELECT cv.id FROM catalog_versions cv
                      WHERE cv.catalog_item_id = ci.id
                        AND cv.version_label = ci.current_version_label
                      LIMIT 1
                  )
              )
              AND ({matchPredicate})
              AND {valuePredicate}
        )
        """;

    /// <summary>
    /// Equals matches the display text (ASCII-case-insensitive, LIKE the
    /// search-by-name path) or the numeric column when the typed value
    /// parses as a number — covering both string and Double attributes.
    /// </summary>
    private static string BuildValueEqualsPredicate(string? value, List<SqliteParameter> parameters, ref int paramIndex)
    {
        var textParam = $"@val_{paramIndex++}";
        parameters.Add(new SqliteParameter(textParam, value ?? string.Empty));
        var numberParam = $"@valNum_{paramIndex++}";
        parameters.Add(new SqliteParameter(numberParam, (object?)ParseNumberOrNull(value) ?? DBNull.Value));

        return $"(av.value_text = {textParam} COLLATE NOCASE OR av.value_number = {numberParam})";
    }

    private static string BuildValueContainsPredicate(string? value, List<SqliteParameter> parameters, ref int paramIndex)
    {
        var textParam = $"@val_{paramIndex++}";
        var escaped = EscapeLikePattern(value ?? string.Empty);
        parameters.Add(new SqliteParameter(textParam, $"%{escaped}%"));

        return $"av.value_text LIKE {textParam} ESCAPE '\\'";
    }

    /// <summary>SQLite LIKE wildcards must be escaped in user input.</summary>
    private static string EscapeLikePattern(string input) =>
        input.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    private static double? ParseNumberOrNull(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var invariant))
        {
            return invariant;
        }

        return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var current)
            ? current
            : null;
    }

    public static string BuildOrderBy(FamilyCatalogSort sort) => sort switch
    {
        FamilyCatalogSort.NameAsc => "ORDER BY ci.normalized_name ASC",
        FamilyCatalogSort.NameDesc => "ORDER BY ci.normalized_name DESC",
        FamilyCatalogSort.DateAsc => "ORDER BY ci.created_at_utc ASC",
        FamilyCatalogSort.DateDesc => "ORDER BY ci.created_at_utc DESC",
        _ => "ORDER BY ci.normalized_name ASC"
    };

    public static List<SqliteParameter> BuildLimitOffsetParameters(FamilyCatalogQuery query)
    {
        return
        [
            new SqliteParameter("@limit", query.Limit > 0 ? query.Limit : 50),
            new SqliteParameter("@offset", query.Offset >= 0 ? query.Offset : 0)
        ];
    }
}
