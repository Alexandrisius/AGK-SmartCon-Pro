using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

/// <summary>
/// SQLite implementation of <see cref="IFamilyFactRepository"/> (ADR-055).
/// Reads <c>catalog_items.revit_category_id</c> + the <c>family_facts</c>
/// rows of one item in a single connection.
/// </summary>
internal sealed class LocalFamilyFactRepository : IFamilyFactRepository
{
    private readonly LocalCatalogDatabase _database;

    public LocalFamilyFactRepository(LocalCatalogDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<FamilyFactsData> GetForItemAsync(string catalogItemId, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        int? revitCategoryId = null;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT revit_category_id FROM catalog_items WHERE id = @itemId";
            cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
            var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (result is long l)
                revitCategoryId = (int)l;
        }

        var facts = new List<FamilyFact>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT fact_key, value_key, value_display
                FROM family_facts
                WHERE catalog_item_id = @itemId
                ORDER BY fact_key
                """;
            cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                facts.Add(new FamilyFact(
                    FactKey: reader.GetString(0),
                    ValueKey: reader.GetString(1),
                    ValueDisplay: reader.GetString(2)));
            }
        }

        return new FamilyFactsData(revitCategoryId, facts);
    }
}
