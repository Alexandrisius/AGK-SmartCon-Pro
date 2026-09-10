using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

/// <summary>
/// <see cref="IContentHashAnalyticsRepository"/> over the local SQLite
/// catalog (I-14: connections via <see cref="LocalCatalogDatabase"/>).
/// Section analytics are read from the FIRST Revit variant of the label
/// (the content is identical across variants — the same rule as
/// <c>content_hash</c>).
/// </summary>
internal sealed class LocalContentHashAnalyticsRepository : IContentHashAnalyticsRepository
{
    private readonly LocalCatalogDatabase _database;

    public LocalContentHashAnalyticsRepository(LocalCatalogDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<IReadOnlyDictionary<string, string>?> GetSectionHashesAsync(
        string catalogItemId, string versionLabel, CancellationToken ct)
    {
        var json = await ReadSectionColumnAsync(catalogItemId, versionLabel, "section_hashes", ct)
            .ConfigureAwait(false);
        return ContentSectionJsonSerializer.Deserialize(json);
    }

    public async Task<IReadOnlyDictionary<string, string>?> GetSectionStringsAsync(
        string catalogItemId, string versionLabel, CancellationToken ct)
    {
        var json = await ReadSectionColumnAsync(catalogItemId, versionLabel, "section_strings", ct)
            .ConfigureAwait(false);
        return ContentSectionJsonSerializer.Deserialize(json);
    }

    public async Task<IReadOnlyList<FamilyTypeHashEntry>?> GetTypeHashesAsync(
        string catalogItemId, string versionLabel, CancellationToken ct)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        // PENDING check first: the version stores types in family_types
        // but the backfill has not written any hash rows — report null
        // (pending), NOT an empty list (which would read as "typeless").
        using (var pendingCmd = connection.CreateCommand())
        {
            pendingCmd.CommandText = """
                SELECT COUNT(*) FROM family_types ft
                JOIN catalog_versions cv ON cv.id = ft.version_id
                WHERE cv.catalog_item_id = @itemId AND cv.version_label = @label
                """;
            pendingCmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
            pendingCmd.Parameters.Add(new SqliteParameter("@label", versionLabel));
            var storedTypes = Convert.ToInt64(await pendingCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            if (storedTypes > 0)
            {
                using var hashCountCmd = connection.CreateCommand();
                hashCountCmd.CommandText = """
                    SELECT COUNT(*) FROM family_type_hashes fth
                    JOIN catalog_versions cv ON cv.id = fth.catalog_version_id
                    WHERE cv.catalog_item_id = @itemId AND cv.version_label = @label
                    """;
                hashCountCmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
                hashCountCmd.Parameters.Add(new SqliteParameter("@label", versionLabel));
                var hashRows = Convert.ToInt64(await hashCountCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
                if (hashRows == 0)
                {
                    return null;
                }
            }
        }

        var result = new List<FamilyTypeHashEntry>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT fth.type_identity_key, fth.type_name, fth.type_hash
            FROM family_type_hashes fth
            JOIN catalog_versions cv ON cv.id = fth.catalog_version_id
            WHERE cv.catalog_item_id = @itemId AND cv.version_label = @label
            ORDER BY fth.type_identity_key
            """;
        cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
        cmd.Parameters.Add(new SqliteParameter("@label", versionLabel));
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new FamilyTypeHashEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }
        return result;
    }

    private async Task<string?> ReadSectionColumnAsync(
        string catalogItemId, string versionLabel, string column, CancellationToken ct)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        // The column name is an internal constant (two call sites above) —
        // never user input, so inlining it is safe.
        cmd.CommandText = $"""
            SELECT {column} FROM catalog_versions
            WHERE catalog_item_id = @itemId AND version_label = @label
            LIMIT 1
            """;
        cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
        cmd.Parameters.Add(new SqliteParameter("@label", versionLabel));
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is null or DBNull ? null : Convert.ToString(result);
    }
}
