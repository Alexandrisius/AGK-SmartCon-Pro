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
                conditions.Add($"ci.normalized_name LIKE {paramName}");
                parameters.Add(new SqliteParameter(paramName, $"%{token}%"));
            }
        }

        if (!string.IsNullOrWhiteSpace(query.CategoryFilter))
        {
            var paramName = $"@category_{paramIndex++}";
            conditions.Add($"ci.category_name = {paramName}");
            parameters.Add(new SqliteParameter(paramName, query.CategoryFilter));
        }

        if (query.StatusFilter is not null)
        {
            var paramName = $"@status_{paramIndex++}";
            conditions.Add($"ci.content_status = {paramName}");
            parameters.Add(new SqliteParameter(paramName, query.StatusFilter.Value.ToString()));
        }

        if (!string.IsNullOrWhiteSpace(query.ManufacturerFilter))
        {
            var paramName = $"@manufacturer_{paramIndex++}";
            conditions.Add($"ci.manufacturer = {paramName}");
            parameters.Add(new SqliteParameter(paramName, query.ManufacturerFilter));
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

        var where = conditions.Count > 0
            ? "WHERE " + string.Join(" AND ", conditions)
            : "";

        return (where, parameters);
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
