using Microsoft.Data.Sqlite;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed partial class LocalCatalogProvider
{
    public async Task<IReadOnlyList<string>> GetAllTagsAsync(CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT tag FROM catalog_tags ORDER BY tag";

        var tags = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            tags.Add(reader.GetString(0));
        }
        return tags;
    }
}
