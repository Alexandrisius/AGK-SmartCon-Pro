using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SmartCon.FamilyManager.Models.Cloud;
using SmartCon.FamilyManager.Services.Cloud;
using SmartCon.FamilyManager.Services.LocalCatalog;
using SmartCon.Tests.FamilyManager.Repository;
using SmartCon.Tests.TestDoubles;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services.Cloud;

/// <summary>
/// CatalogManifestApplier (срез v1): манифест → чистая cloud-копия catalog.db.
/// Главный тест — round-trip: build(source) → apply → build(copy) ≡ build(source)
/// (контрольная точка среза C0 «манифест уезжает на сервер и возвращается»).
/// </summary>
public sealed class CatalogManifestApplierTests : IDisposable
{
    private readonly TempCatalogFixture _source = new();
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));
    private readonly CatalogManifestBuilder _builder;
    private readonly CatalogManifestApplier _applier;

    public CatalogManifestApplierTests()
    {
        _builder = new CatalogManifestBuilder(_source.GetDatabase(), new StoragePathResolver(_source.GetDatabase()), _clock);
        _applier = new CatalogManifestApplier(_clock);
    }

    public void Dispose()
    {
        _source.Dispose();
        foreach (var leftover in Directory.GetDirectories(Path.GetTempPath(), "CloudApplyTest_*"))
        {
            try { Directory.Delete(leftover, recursive: true); } catch (IOException) { }
        }
    }

    private string NewTargetRoot() =>
        Path.Combine(Path.GetTempPath(), $"CloudApplyTest_{Guid.NewGuid():N}");

    private CatalogManifestBuildOptions BuildOptions() => new()
    {
        CatalogId = "f47ac10b-58cc-4372-a567-0e02b2c3d479",
        PublishSeq = 7,
        PublishedBy = "BIM-отдел ВентПроект",
        MinPluginVersion = "2.1.0",
    };

    /// <summary>Сидирует полный каталог: категории, атрибуты, binding+rules, assignment, item со всем §13.19-хламом.</summary>
    private async Task SeedFullCatalogAsync()
    {
        var now = DateTimeOffset.UtcNow.ToString("o");
        var parentId = Guid.NewGuid().ToString();
        var childId = Guid.NewGuid().ToString();
        var attributeId = Guid.NewGuid().ToString();
        var bindingId = Guid.NewGuid().ToString();
        var groupId = Guid.NewGuid().ToString();

        var (itemId, versionId, fileId, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _source, "Отвод 90° стальной", "v3", revitVersion: 2023,
            hashFormatVersion: 22, contentHash: "A1B2C3D4E5F6", revitCategory: "Трубы",
            revitCategoryId: -2008055, categoryId: childId);
        await CatalogSeedHelper.SeedAdditionalVariantAsync(_source, itemId, "Отвод 90° стальной", "v3", 2021);
        var runId = await CatalogSeedHelper.SeedDataExtractedAsync(_source, itemId, versionId, fileId);
        await CatalogSeedHelper.SeedFactAsync(_source, itemId, "PartType", "23", "Тройник");
        var typeId = await ReadSingleTypeIdAsync(versionId);

        var avatarDir = Path.Combine(_source.GetDatabaseRoot(), "files", itemId);
        Directory.CreateDirectory(avatarDir);
        await File.WriteAllBytesAsync(Path.Combine(avatarDir, "avatar.png"), [1, 2, 3, 4]);
        var imagePath = Path.Combine(_source.GetDatabaseRoot(), "files", itemId, "v3", "photo.png");
        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        await File.WriteAllBytesAsync(imagePath, [9, 9, 9]);

        using var conn = _source.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction();
        await ExecAsync(conn, tx,
            "INSERT INTO categories (id, name, parent_id, sort_order, created_at_utc) VALUES (@id, 'Трубопроводы', NULL, 0, @t)",
            ("@id", parentId), ("@t", now));
        await ExecAsync(conn, tx,
            "INSERT INTO categories (id, name, parent_id, sort_order, created_at_utc) VALUES (@id, 'Отводы', @parent, 0, @t)",
            ("@id", childId), ("@parent", parentId), ("@t", now));
        await ExecAsync(conn, tx,
            "INSERT INTO attribute_definitions (id, name, group_name, is_active, created_at_utc) VALUES (@id, 'DN', 'Геометрия', 1, @t)",
            ("@id", attributeId), ("@t", now));
        await ExecAsync(conn, tx,
            "INSERT INTO category_attribute_bindings (id, category_id, attribute_id, sort_order, is_enabled) VALUES (@id, @cat, @attr, 0, 1)",
            ("@id", bindingId), ("@cat", childId), ("@attr", attributeId));
        await ExecAsync(conn, tx,
            "INSERT INTO category_validation_rules (binding_id, operator, value_number, min_value, max_value, is_enabled, sort_order) VALUES (@b, 'InRange', NULL, 15.0, 500.0, 1, 0)",
            ("@b", bindingId));
        await ExecAsync(conn, tx,
            "INSERT INTO category_assignment_rule_groups (id, category_id, sort_order, is_enabled) VALUES (@id, @cat, 0, 1)",
            ("@id", groupId), ("@cat", childId));
        await ExecAsync(conn, tx,
            "INSERT INTO category_assignment_conditions (group_id, source_kind, attribute_id, system_key, operator, value_text, sort_order, is_enabled) VALUES (@g, 'attribute', @attr, NULL, 'Equals', 'Ду50', 0, 1)",
            ("@g", groupId), ("@attr", attributeId));
        await ExecAsync(conn, tx,
            "INSERT INTO catalog_tags (catalog_item_id, tag, normalized_tag) VALUES (@itemId, 'сталь', 'сталь')",
            ("@itemId", itemId));
        await ExecAsync(conn, tx,
            "UPDATE family_types SET family_name = 'Отвод 90°' WHERE id = @typeId",
            ("@typeId", typeId));
        await ExecAsync(conn, tx,
            """
            INSERT INTO extracted_attribute_values
                (id, catalog_item_id, version_id, file_id, type_id, parameter_name, parameter_scope,
                 storage_type, value_text, value_number, unit_type_id, status, extraction_run_id, extracted_at_utc)
            VALUES (@id, @itemId, @versionId, @fileId, @typeId, 'ADSK_Размер', 'Type',
                    'Double', NULL, 0.164041994750656, 'autodesk.spec.aec.length-1.0.0', 'Found', @runId, @t)
            """,
            ("@id", Guid.NewGuid().ToString()), ("@itemId", itemId), ("@versionId", versionId),
            ("@fileId", fileId), ("@typeId", typeId), ("@runId", runId), ("@t", now));
        await ExecAsync(conn, tx,
            "INSERT INTO item_routing_rules (catalog_item_id, family_key, type_name, group_key, rule_order, part_name, description, criteria_json) " +
            "VALUES (@itemId, '', 'DN50', 'Муфты', 0, 'Муфта', '', '[{\"kind\":\"eq\"}]')",
            ("@itemId", itemId));
        await ExecAsync(conn, tx,
            "INSERT INTO item_routing_type_settings (catalog_item_id, family_key, type_name, preferred_junction_type) VALUES (@itemId, '', 'DN50', 2)",
            ("@itemId", itemId));
        await ExecAsync(conn, tx,
            "INSERT INTO family_routing_rules (catalog_item_id, catalog_version_id, family_key, type_name, group_key, rule_order, part_name, description, criteria_json) " +
            "VALUES (@itemId, @versionId, '', 'DN50', 'Муфты', 0, NULL, '', '[]')",
            ("@itemId", itemId), ("@versionId", versionId));
        await ExecAsync(conn, tx,
            "INSERT INTO family_type_hashes (catalog_version_id, type_identity_key, type_name, type_hash, created_at_utc) VALUES (@versionId, 'DN50', 'DN50', 'ab12cd34', @t)",
            ("@versionId", versionId), ("@t", now));
        await ExecAsync(conn, tx,
            "INSERT INTO family_segment_sizes (catalog_version_id, segment_name, nominal_diameter, inner_diameter, outer_diameter, used_in_size_lists, used_in_sizing, sort_order) " +
            "VALUES (@versionId, 'Диаметр', 0.164041994750656, 0.15, 0.18, 1, 0, 0)",
            ("@versionId", versionId));
        await ExecAsync(conn, tx,
            "INSERT INTO family_segment_rules (catalog_version_id, family_key, type_name, rule_order, segment_name, min_size_feet, max_size_feet, description) " +
            "VALUES (@versionId, '', 'DN50', 0, 'Диаметр', NULL, 0.5, 'до ДУ400')",
            ("@versionId", versionId));
        await ExecAsync(conn, tx,
            "UPDATE catalog_versions SET section_hashes = @hashes, section_strings = @strings, glb_state = -1, routing_backfilled = 1 WHERE id = @versionId",
            ("@versionId", versionId), ("@hashes", "{\"META|DN50\":\"aabb\"}"), ("@strings", "{\"META|DN50\":\"canonical\"}"));
        await ExecAsync(conn, tx,
            "INSERT INTO family_nested_shared_families (catalog_item_id, version_id, nested_family_name) VALUES (@itemId, @versionId, 'Болт М12')",
            ("@itemId", itemId), ("@versionId", versionId));
        await ExecAsync(conn, tx,
            "INSERT INTO family_assets (id, catalog_item_id, version_label, asset_type, file_name, relative_path, size_bytes, description, created_at_utc, is_primary) " +
            "VALUES (@id, @itemId, 'v3', 'Image', 'photo.png', @path, 3, 'фото', @t, 1)",
            ("@id", Guid.NewGuid().ToString()), ("@itemId", itemId), ("@path", $"files/{itemId}/v3/photo.png"), ("@t", now));
        await tx.CommitAsync();
        return;
    }

    [Fact]
    public async Task RoundTrip_BuildApplyBuild_ManifestsEquivalent()
    {
        await SeedFullCatalogAsync();

        var sourceManifest = await _builder.BuildAsync(BuildOptions());
        var json1 = JsonSerializer.Serialize(sourceManifest, CatalogManifestJson.WriteCompact);

        var targetRoot = NewTargetRoot();
        var objects = await MemoryObjectSource.FromDirectoryAsync(_source.GetDatabaseRoot());
        var result = await _applier.ApplyAsync(sourceManifest, objects,
            new CatalogManifestApplyOptions { DatabaseRootPath = targetRoot, DatabaseName = "Отводы компании" });

        Assert.Equal(1, result.Items);
        Assert.Equal(2, result.Versions); // оба Revit-варианта активного label

        // build(copy) через builder на приватной базе копии.
        var copyDatabase = new LocalCatalogDatabase();
        copyDatabase.SwitchToPath(targetRoot);
        var copyBuilder = new CatalogManifestBuilder(
            copyDatabase, new StoragePathResolver(copyDatabase), _clock);
        var copyManifest = await copyBuilder.BuildAsync(BuildOptions());
        var json2 = JsonSerializer.Serialize(copyManifest, CatalogManifestJson.WriteCompact);

        Assert.Equal(json1, json2);
    }

    [Fact]
    public async Task Apply_EmptyManifest_CreatesMetaOnlyDatabase()
    {
        var manifest = new CatalogManifestV1
        {
            CatalogId = "id",
            PublishSeq = 1,
            PublishedBy = "Автор",
            Items = [],
        };

        var targetRoot = NewTargetRoot();
        var result = await _applier.ApplyAsync(manifest, MemoryObjectSource.Empty,
            new CatalogManifestApplyOptions { DatabaseRootPath = targetRoot, DatabaseName = "Пустая" });

        Assert.Equal(0, result.Items);
        Assert.True(File.Exists(Path.Combine(targetRoot, "catalog.db")));
        Assert.Equal(38L, await ReadSchemaVersionAsync(targetRoot));
    }

    [Fact]
    public async Task Apply_UnknownFormatVersion_Throws()
    {
        var manifest = new CatalogManifestV1 { FormatVersion = 99, Items = [] };
        await Assert.ThrowsAsync<NotSupportedException>(() => _applier.ApplyAsync(
            manifest, MemoryObjectSource.Empty,
            new CatalogManifestApplyOptions { DatabaseRootPath = NewTargetRoot() }));
    }

    [Fact]
    public async Task Apply_MissingCasObject_FailsFastAndRollsBack()
    {
        await SeedFullCatalogAsync();
        var sourceManifest = await _builder.BuildAsync(BuildOptions());

        // Пустой источник: ни один объект не доступен.
        var targetRoot = NewTargetRoot();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _applier.ApplyAsync(
            sourceManifest, MemoryObjectSource.Empty,
            new CatalogManifestApplyOptions { DatabaseRootPath = targetRoot }));

        // Транзакция откатилась: items в базе нет.
        var count = await CountRowsAsync(targetRoot, "catalog_items");
        Assert.Equal(0L, count);
    }

    [Fact]
    public async Task Apply_VersionRowCarriesAllColumns()
    {
        await SeedFullCatalogAsync();
        var sourceManifest = await _builder.BuildAsync(BuildOptions());
        var targetRoot = NewTargetRoot();
        var objects = await MemoryObjectSource.FromDirectoryAsync(_source.GetDatabaseRoot());
        await _applier.ApplyAsync(sourceManifest, objects,
            new CatalogManifestApplyOptions { DatabaseRootPath = targetRoot, DatabaseName = "X" });

        Assert.Equal(1L, await CountRowsAsync(targetRoot, "catalog_items"));
        Assert.Equal(2L, await CountRowsAsync(targetRoot, "catalog_versions"));
        Assert.Equal(2L, await CountRowsAsync(targetRoot, "family_files"));
        Assert.Equal(1L, await CountRowsAsync(targetRoot, "family_types"));
        Assert.Equal(1L, await CountRowsAsync(targetRoot, "extracted_attribute_values"));
        Assert.Equal(2L, await CountRowsAsync(targetRoot, "family_data_import_runs"));
        // Детальные per-version таблицы сидированы только на первом варианте (r2023);
        // второй вариант (r2021, additional) — bare, без детализации.
        Assert.Equal(1L, await CountRowsAsync(targetRoot, "family_type_hashes"));
        Assert.Equal(1L, await CountRowsAsync(targetRoot, "family_segment_sizes"));
        Assert.Equal(1L, await CountRowsAsync(targetRoot, "family_segment_rules"));
        Assert.Equal(1L, await CountRowsAsync(targetRoot, "family_routing_rules"));
        Assert.Equal(0L, await CountRowsAsync(targetRoot, "family_routing_type_settings")); // version-level V34 настройки в сиде отсутствуют
        Assert.Equal(1L, await CountRowsAsync(targetRoot, "family_nested_shared_families"));
        Assert.Equal(1L, await CountRowsAsync(targetRoot, "item_routing_rules"));
        Assert.Equal(1L, await CountRowsAsync(targetRoot, "item_routing_type_settings"));
        Assert.Equal(1L, await CountRowsAsync(targetRoot, "catalog_tags"));
        Assert.Equal(1L, await CountRowsAsync(targetRoot, "family_facts"));
        Assert.Equal(1L, await CountRowsAsync(targetRoot, "family_assets"));
        Assert.Equal(2L, await CountRowsAsync(targetRoot, "categories"));
        Assert.Equal(1L, await CountRowsAsync(targetRoot, "attribute_definitions"));
        Assert.Equal(1L, await CountRowsAsync(targetRoot, "category_attribute_bindings"));
        Assert.Equal(1L, await CountRowsAsync(targetRoot, "category_validation_rules"));
        Assert.Equal(1L, await CountRowsAsync(targetRoot, "category_assignment_rule_groups"));
        Assert.Equal(1L, await CountRowsAsync(targetRoot, "category_assignment_conditions"));

        // Файлы физически скопированы: .rfa оба варианта + avatar + asset.
        Assert.Equal(2, Directory.GetFiles(Path.Combine(targetRoot, "files"), "*.rfa", SearchOption.AllDirectories).Length);
        Assert.True(File.Exists(Path.Combine(targetRoot, "files", sourceManifest.Items[0].Id, "avatar.png")));
        Assert.True(File.Exists(Path.Combine(targetRoot, "files", sourceManifest.Items[0].Id, "v3", "assets", "images", "photo.png")));
    }

    [Fact]
    public async Task RoundTrip_DependencyWhereChildSortsBeforeParent_Applies()
    {
        // Родитель «Тройник-Z» зависит от ребёнка «Муфта-A»: normalized(child) < normalized(parent),
        // в манифесте ребёнок раньше. FK требует, чтобы все catalog_items существовали до
        // versions-фазы с family_dependencies (двухпроходная вставка апликера).
        var (parentId, parentVersionId, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_source, "Тройник-Z", "v1");
        var (childId, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_source, "Муфта-A", "v1");
        using (var conn = _source.GetDatabase().CreateConnection())
        {
            await conn.OpenAsync();
            await ExecAsync(conn, null,
                "INSERT INTO family_dependencies (parent_catalog_item_id, parent_version_id, child_catalog_item_id, dependency_kind, part_name, ordinal, child_version_label) " +
                "VALUES (@p, @pv, @c, 'routing', 'Муфта', 0, 'v1')",
                ("@p", parentId), ("@pv", parentVersionId), ("@c", childId));
        }

        var sourceManifest = await _builder.BuildAsync(BuildOptions());
        Assert.Single(sourceManifest.Items.Single(i => i.Id == parentId).Versions.SelectMany(v => v.Dependencies));

        var targetRoot = NewTargetRoot();
        var objects = await MemoryObjectSource.FromDirectoryAsync(_source.GetDatabaseRoot());
        await _applier.ApplyAsync(sourceManifest, objects,
            new CatalogManifestApplyOptions { DatabaseRootPath = targetRoot, DatabaseName = "Z" });

        Assert.Equal(2L, await CountRowsAsync(targetRoot, "catalog_items"));
        Assert.Equal(1L, await CountRowsAsync(targetRoot, "family_dependencies"));
    }

    [Fact]
    public async Task Build_DependencyOnExcludedChild_Dropped()
    {
        // Ребёнок существует в БД автора, но его файл бит — item исключается из манифеста;
        // зависимость на него становится dangling и обязана быть отброшена (иначе apply упадёт на FK).
        var (parentId, parentVersionId, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_source, "Отвод", "v1");
        var (_, childVersionId, _, childPath) = await CatalogSeedHelper.SeedBareLoadableAsync(_source, "Муфта", "v1");
        using (var conn = _source.GetDatabase().CreateConnection())
        {
            await conn.OpenAsync();
            await ExecAsync(conn, null,
                "INSERT INTO family_dependencies (parent_catalog_item_id, parent_version_id, child_catalog_item_id, dependency_kind, ordinal) " +
                "VALUES (@p, @pv, @c, 'routing', 0)",
                ("@p", parentId), ("@pv", parentVersionId), ("@c", (await ReadItemIdAsync("Муфта"))!));
        }
        File.Delete(Path.Combine(_source.GetDatabaseRoot(), childPath));

        var manifest = await _builder.BuildAsync(BuildOptions());

        var parent = manifest.Items.Single(i => i.Name == "Отвод");
        Assert.Empty(parent.Versions.Single().Dependencies);
        Assert.DoesNotContain(manifest.Items, i => i.Name == "Муфта");
    }

    private async Task<string?> ReadItemIdAsync(string name)
    {
        using var conn = _source.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM catalog_items WHERE name = @name";
        cmd.Parameters.Add(new SqliteParameter("@name", name));
        return await cmd.ExecuteScalarAsync() as string;
    }

    [Fact]
    public async Task Apply_NonEmptyTargetRoot_Throws()
    {
        var manifest = new CatalogManifestV1 { CatalogId = "id", PublishSeq = 1, PublishedBy = "A", Items = [] };
        var targetRoot = NewTargetRoot();
        Directory.CreateDirectory(targetRoot);
        await File.WriteAllTextAsync(Path.Combine(targetRoot, "leftover.txt"), "x");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _applier.ApplyAsync(
            manifest, MemoryObjectSource.Empty,
            new CatalogManifestApplyOptions { DatabaseRootPath = targetRoot }));
    }

    private async Task<string> ReadSingleTypeIdAsync(string versionId)
    {
        using var conn = _source.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM family_types WHERE version_id = @versionId";
        cmd.Parameters.Add(new SqliteParameter("@versionId", versionId));
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<long> CountRowsAsync(string databaseRoot, string table)
    {
        var database = new LocalCatalogDatabase();
        database.SwitchToPath(databaseRoot);
        using var connection = database.CreateConnection();
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<long> ReadSchemaVersionAsync(string databaseRoot)
    {
        var database = new LocalCatalogDatabase();
        database.SwitchToPath(databaseRoot);
        using var connection = database.CreateConnection();
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT CAST(value AS INTEGER) FROM schema_info WHERE key = 'schema_version'";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task ExecAsync(
        SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.Add(new SqliteParameter(name, value));
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Собирает все файлы каталога в CAS-словарь sha256→bytes (эмуляция полного кэша sync-флоу).</summary>
    private sealed class MemoryObjectSource : ICloudObjectSource
    {
        private readonly Dictionary<string, byte[]> _objects;

        private MemoryObjectSource(Dictionary<string, byte[]> objects) => _objects = objects;

        public static MemoryObjectSource Empty { get; } = new([]);

        public static async Task<MemoryObjectSource> FromDirectoryAsync(string root)
        {
            var objects = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith("catalog.db", StringComparison.OrdinalIgnoreCase)) continue;
                var bytes = await File.ReadAllBytesAsync(file);
                var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                objects[sha] = bytes;
            }
            return new MemoryObjectSource(objects);
        }

        public Task<Stream> OpenReadAsync(string sha256, CancellationToken ct = default)
        {
            if (!_objects.TryGetValue(sha256, out var bytes))
                throw new FileNotFoundException($"CAS object not found: {sha256}");
            return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        }
    }
}
