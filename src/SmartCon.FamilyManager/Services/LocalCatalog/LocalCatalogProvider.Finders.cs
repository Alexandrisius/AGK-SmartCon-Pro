using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed partial class LocalCatalogProvider
{
    public async Task<FamilyCatalogItem?> FindByNormalizedNameAsync(string normalizedName, string? familySource = null, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = familySource is null
            ? "SELECT * FROM catalog_items WHERE normalized_name = @name ORDER BY created_at_utc LIMIT 1"
            : "SELECT * FROM catalog_items WHERE normalized_name = @name AND family_source = @source ORDER BY created_at_utc LIMIT 1";
        cmd.Parameters.Add(new SqliteParameter("@name", normalizedName));
        if (familySource is not null)
        {
            cmd.Parameters.Add(new SqliteParameter("@source", familySource));
        }

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        return ReadCatalogItem(reader);
    }

    public async Task<FamilyCatalogItem?> FindByRevitCategoryIdAsync(int revitCategoryId, string familySource, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM catalog_items WHERE revit_category_id = @catId AND family_source = @source ORDER BY created_at_utc LIMIT 1";
        cmd.Parameters.Add(new SqliteParameter("@catId", revitCategoryId));
        cmd.Parameters.Add(new SqliteParameter("@source", familySource));

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        return ReadCatalogItem(reader);
    }

    public async Task<IReadOnlyList<FamilyCatalogItem>> GetItemsBySourceAsync(string familySource, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM catalog_items WHERE family_source = @source ORDER BY name";
        cmd.Parameters.Add(new SqliteParameter("@source", familySource));

        var items = new List<FamilyCatalogItem>();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(ReadCatalogItem(reader));
        }

        return items;
    }

    private static string? TryGetString(SqliteDataReader reader, string columnName)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (reader.GetName(i) == columnName && !reader.IsDBNull(i))
                return reader.GetString(i);
        }
        return null;
    }

    private static int? TryGetInt(SqliteDataReader reader, string columnName)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (reader.GetName(i) == columnName && !reader.IsDBNull(i))
                return reader.GetInt32(i);
        }
        return null;
    }

    /// <summary>
    /// Cross-version content-hash search. Looks for a matching
    /// <c>content_hash</c> across ALL versions (current and archived) of
    /// ALL catalog items, filtered by <paramref name="familySource"/> to
    /// enforce cross-source separation (system hashes never match
    /// loadable hashes) and by <paramref name="hashFormatVersion"/> so
    /// old-format hashes do not produce false duplicate matches against
    /// new-format ones.
    /// </summary>
    /// <param name="hexHash">SHA-256 hex string.</param>
    /// <param name="hashFormatVersion">Hash format version (must match
    /// <c>hash_format_version</c> column).</param>
    /// <param name="familySource"><c>"loadable"</c> or <c>"system"</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="ContentHashMatch"/> if a match was found,
    /// or <c>null</c> if no version has this hash.</returns>
    public async Task<ContentHashMatch?> FindByContentHashAcrossVersionsAsync(
        string hexHash,
        int hashFormatVersion,
        string familySource,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(hexHash))
            return null;

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        // Issue #126: also select the matched item's name/normalized_name/
        // current_version_label so the dedup service can detect a cross-name
        // duplicate and treat the matched item as the canonical "existing"
        // item for MakeActive / IncrementVersion. The lookup itself is
        // covered by ix_catalog_versions_content_hash (partial index).
        cmd.CommandText = """
            SELECT cv.catalog_item_id AS itemId,
                   cv.version_label AS versionLabel,
                   ci.current_version_label AS currentLabel,
                   ci.name AS itemName,
                   ci.normalized_name AS itemNormalizedName
            FROM catalog_versions cv
            JOIN catalog_items ci ON cv.catalog_item_id = ci.id
            WHERE cv.content_hash = @hash
              AND cv.hash_format_version = @fmt
              AND ci.family_source = @source
            ORDER BY cv.published_at_utc DESC, cv.rowid DESC
            LIMIT 1
            """;
        cmd.Parameters.Add(new SqliteParameter("@hash", hexHash));
        cmd.Parameters.Add(new SqliteParameter("@fmt", hashFormatVersion));
        cmd.Parameters.Add(new SqliteParameter("@source", familySource));

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        var itemId = reader.GetString(reader.GetOrdinal("itemId"));
        var versionLabel = reader.GetString(reader.GetOrdinal("versionLabel"));
        var currentLabel = reader.IsDBNull(reader.GetOrdinal("currentLabel"))
            ? null
            : reader.GetString(reader.GetOrdinal("currentLabel"));
        var isCurrent = string.Equals(versionLabel, currentLabel, StringComparison.Ordinal);

        return new ContentHashMatch(
            CatalogItemId: itemId,
            MatchedVersionLabel: versionLabel,
            IsCurrentVersion: isCurrent,
            CurrentVersionLabel: currentLabel,
            MatchedItemName: reader.GetString(reader.GetOrdinal("itemName")),
            MatchedItemNormalizedName: reader.GetString(reader.GetOrdinal("itemNormalizedName")));
    }
}
