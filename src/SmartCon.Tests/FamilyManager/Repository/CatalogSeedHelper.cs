using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Tests.FamilyManager.Repository;

/// <summary>
/// Shared seeding helpers for actualization-engine/task tests (ADR-054):
/// bare loadable items, extracted-data rows, attribute values, GLB assets,
/// hash readers, and a canonical snapshot. Extracted from the deleted
/// hash-recalc/backfill test suites to keep the new suites DRY.
/// </summary>
internal static class CatalogSeedHelper
{
    /// <summary>
    /// Seeds a bare loadable item (file + version + item). With no import
    /// run / types / assets / hash the row is pending on EVERY criterion.
    /// </summary>
    public static async Task<(string ItemId, string VersionId, string FileId, string RelativePath)> SeedBareLoadableAsync(
        TempCatalogFixture fixture,
        string name = "FamA",
        string versionLabel = "v1",
        int revitVersion = 2025,
        int? hashFormatVersion = null,
        string? contentHash = null,
        bool createFileOnDisk = true,
        string familySource = "loadable",
        string? currentLabel = null,
        string? revitCategory = null,
        int? revitCategoryId = null,
        int? glbState = null)
    {
        var itemId = Guid.NewGuid().ToString("N");
        var versionId = Guid.NewGuid().ToString();
        var fileId = Guid.NewGuid().ToString();
        var ext = familySource == "system" ? ".rvt" : ".rfa";
        var relativePath = $"files/{itemId}/{versionLabel}/{name}{ext}";
        currentLabel ??= versionLabel;

        if (createFileOnDisk)
        {
            var absolute = Path.Combine(fixture.GetDatabaseRoot(), relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            File.WriteAllText(absolute, "FAKE");
        }

        using var conn = fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction();

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO family_files (id, relative_path, file_name, revit_major_version, imported_at_utc)
                VALUES (@id, @path, @name, @revit, @t)
                """;
            cmd.Parameters.Add(new SqliteParameter("@id", fileId));
            cmd.Parameters.Add(new SqliteParameter("@path", relativePath));
            cmd.Parameters.Add(new SqliteParameter("@name", name + ext));
            cmd.Parameters.Add(new SqliteParameter("@revit", revitVersion));
            cmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
            await cmd.ExecuteNonQueryAsync();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO catalog_items (id, name, normalized_name, current_version_label,
                                           family_source, revit_category, revit_category_id, hash_format_version, created_at_utc, updated_at_utc)
                VALUES (@id, @name, @norm, @currentLabel, @source, @revitCategory, @revitCategoryId, @fmt, @t, @t)
                """;
            cmd.Parameters.Add(new SqliteParameter("@id", itemId));
            cmd.Parameters.Add(new SqliteParameter("@name", name));
            cmd.Parameters.Add(new SqliteParameter("@norm", name.ToLowerInvariant()));
            cmd.Parameters.Add(new SqliteParameter("@currentLabel", currentLabel));
            cmd.Parameters.Add(new SqliteParameter("@source", familySource));
            cmd.Parameters.Add(new SqliteParameter("@revitCategory", (object?)revitCategory ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@revitCategoryId", (object?)revitCategoryId ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@fmt", (object?)hashFormatVersion ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
            await cmd.ExecuteNonQueryAsync();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO catalog_versions (id, catalog_item_id, file_id, version_label,
                                              revit_major_version, content_hash, hash_format_version, glb_state, published_at_utc)
                VALUES (@id, @itemId, @fileId, @label, @revit, @hash, @fmt, @glbState, @t)
                """;
            cmd.Parameters.Add(new SqliteParameter("@id", versionId));
            cmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
            cmd.Parameters.Add(new SqliteParameter("@fileId", fileId));
            cmd.Parameters.Add(new SqliteParameter("@label", versionLabel));
            cmd.Parameters.Add(new SqliteParameter("@revit", revitVersion));
            cmd.Parameters.Add(new SqliteParameter("@hash", (object?)contentHash ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@fmt", (object?)hashFormatVersion ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@glbState", (object?)glbState ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
            await cmd.ExecuteNonQueryAsync();
        }

        tx.Commit();
        return (itemId, versionId, fileId, relativePath);
    }

    /// <summary>Adds a second Revit variant (same label) to an existing item.</summary>
    public static async Task<string> SeedAdditionalVariantAsync(
        TempCatalogFixture fixture, string itemId, string name, string versionLabel, int revitVersion)
    {
        var versionId = Guid.NewGuid().ToString();
        var fileId = Guid.NewGuid().ToString();
        var relativePath = $"files/{itemId}/{versionLabel}/{name}.rfa";
        var absolute = Path.Combine(fixture.GetDatabaseRoot(), relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, "FAKE");

        using var conn = fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO family_files (id, relative_path, file_name, revit_major_version, imported_at_utc)
                VALUES (@id, @path, @name, @revit, @t)
                """;
            cmd.Parameters.Add(new SqliteParameter("@id", fileId));
            cmd.Parameters.Add(new SqliteParameter("@path", relativePath));
            cmd.Parameters.Add(new SqliteParameter("@name", name + ".rfa"));
            cmd.Parameters.Add(new SqliteParameter("@revit", revitVersion));
            cmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
            await cmd.ExecuteNonQueryAsync();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO catalog_versions (id, catalog_item_id, file_id, version_label,
                                              revit_major_version, published_at_utc)
                VALUES (@id, @itemId, @fileId, @label, @revit, @t)
                """;
            cmd.Parameters.Add(new SqliteParameter("@id", versionId));
            cmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
            cmd.Parameters.Add(new SqliteParameter("@fileId", fileId));
            cmd.Parameters.Add(new SqliteParameter("@label", versionLabel));
            cmd.Parameters.Add(new SqliteParameter("@revit", revitVersion));
            cmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
            await cmd.ExecuteNonQueryAsync();
        }
        tx.Commit();
        return versionId;
    }

    /// <summary>Marks a version as fully extracted: terminal run + one type. Returns the run id.</summary>
    public static async Task<string> SeedDataExtractedAsync(
        TempCatalogFixture fixture, string itemId, string versionId, string fileId, int revitVersion = 2025)
    {
        var runId = Guid.NewGuid().ToString();
        using var conn = fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction();

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO family_data_import_runs (id, catalog_item_id, version_id, file_id,
                                                     revit_major_version, status, types_count, started_at_utc)
                VALUES (@id, @itemId, @versionId, @fileId, @revit, 'Succeeded', 1, @t)
                """;
            cmd.Parameters.Add(new SqliteParameter("@id", runId));
            cmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
            cmd.Parameters.Add(new SqliteParameter("@versionId", versionId));
            cmd.Parameters.Add(new SqliteParameter("@fileId", fileId));
            cmd.Parameters.Add(new SqliteParameter("@revit", revitVersion));
            cmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
            await cmd.ExecuteNonQueryAsync();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO family_types (id, catalog_item_id, type_name, sort_order, version_id, file_id)
                VALUES (@id, @itemId, 'DN50', 0, @versionId, @fileId)
                """;
            cmd.Parameters.Add(new SqliteParameter("@id", Guid.NewGuid().ToString()));
            cmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
            cmd.Parameters.Add(new SqliteParameter("@versionId", versionId));
            cmd.Parameters.Add(new SqliteParameter("@fileId", fileId));
            await cmd.ExecuteNonQueryAsync();
        }

        tx.Commit();
        return runId;
    }

    /// <summary>Seeds one extracted attribute value (broken or healthy).</summary>
    public static async Task SeedAttributeValueAsync(
        TempCatalogFixture fixture,
        string itemId, string versionId, string fileId,
        string? valueText, string? unitTypeId, string runId)
    {
        using var conn = fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO extracted_attribute_values
                (id, catalog_item_id, version_id, file_id, parameter_name, storage_type,
                 value_text, value_number, unit_type_id, status, extraction_run_id, extracted_at_utc)
            VALUES (@id, @itemId, @versionId, @fileId, 'Width', 'Double',
                    @valueText, 50.0, @unitTypeId, 'Found', @runId, @t)
            """;
        cmd.Parameters.Add(new SqliteParameter("@id", Guid.NewGuid().ToString()));
        cmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
        cmd.Parameters.Add(new SqliteParameter("@versionId", versionId));
        cmd.Parameters.Add(new SqliteParameter("@fileId", fileId));
        cmd.Parameters.Add(new SqliteParameter("@valueText", (object?)valueText ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@unitTypeId", (object?)unitTypeId ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@runId", runId));
        cmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Seeds the auto-extracted Model3D asset for a label.</summary>
    public static async Task SeedGlbAssetAsync(TempCatalogFixture fixture, string itemId, string versionLabel)
    {
        using var conn = fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO family_assets (id, catalog_item_id, version_label, asset_type,
                                       file_name, relative_path, size_bytes, description, created_at_utc)
            VALUES (@id, @itemId, @label, 'Model3D', 'preview.glb', 'assets/preview.glb', 100,
                    'auto-extracted-preview:DN50', @t)
            """;
        cmd.Parameters.Add(new SqliteParameter("@id", Guid.NewGuid().ToString()));
        cmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
        cmd.Parameters.Add(new SqliteParameter("@label", versionLabel));
        cmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<(int? Fmt, string? Hash, int? Types, int? Params)> ReadVersionAsync(
        TempCatalogFixture fixture, string versionId)
    {
        using var conn = fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT hash_format_version, content_hash, types_count, parameters_count FROM catalog_versions WHERE id = @id";
        cmd.Parameters.Add(new SqliteParameter("@id", versionId));
        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException("version row must exist");
        return (
            reader.IsDBNull(0) ? null : reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3));
    }

    /// <summary>Seeds one family_facts row (ADR-055).</summary>
    public static async Task SeedFactAsync(
        TempCatalogFixture fixture, string itemId,
        string factKey, string valueKey, string valueDisplay)
    {
        using var conn = fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO family_facts (catalog_item_id, fact_key, value_key, value_display)
            VALUES (@itemId, @factKey, @valueKey, @valueDisplay)
            """;
        cmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
        cmd.Parameters.Add(new SqliteParameter("@factKey", factKey));
        cmd.Parameters.Add(new SqliteParameter("@valueKey", valueKey));
        cmd.Parameters.Add(new SqliteParameter("@valueDisplay", valueDisplay));
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Reads (revit_category_id, facts) of an item for assertions.</summary>
    public static async Task<(int? CategoryId, List<(string Key, string ValueKey, string ValueDisplay)> Facts)>
        ReadFactsAsync(TempCatalogFixture fixture, string itemId)
    {
        using var conn = fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();

        int? categoryId = null;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT revit_category_id FROM catalog_items WHERE id = @id";
            cmd.Parameters.Add(new SqliteParameter("@id", itemId));
            var result = await cmd.ExecuteScalarAsync();
            if (result is long l) categoryId = (int)l;
        }

        var facts = new List<(string, string, string)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT fact_key, value_key, value_display FROM family_facts WHERE catalog_item_id = @id ORDER BY fact_key";
            cmd.Parameters.Add(new SqliteParameter("@id", itemId));
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                facts.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return (categoryId, facts);
    }

    public static FamilySnapshot CreateSnapshot()
    {
        return new FamilySnapshot(
            FamilyName: "FamA",
            Category: "Pipe Fittings",
            Parameters:
            [
                new FamilyParameterInfo("Width", "Double", "PG_GEOMETRY", false, false, null, false, false, null, null)
            ],
            Types:
            [
                new FamilyTypeSnapshot("DN50",
                    [new FamilyParameterValue("Width", "Double", true, "50", 50.0, null)])
            ],
            Geometry: new GeometryMetrics(1, [new FormMetrics("Extrusion", true, 1250.0, 6, 12, null)]),
            SharedNestedFamilyNames: ["SharedNestedA"]);
    }
}
