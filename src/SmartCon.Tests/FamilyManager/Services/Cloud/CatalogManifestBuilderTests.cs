using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Models.Cloud;
using SmartCon.FamilyManager.Services.Cloud;
using SmartCon.FamilyManager.Services.LocalCatalog;
using SmartCon.Tests.FamilyManager.Repository;
using SmartCon.Tests.TestDoubles;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services.Cloud;

/// <summary>
/// CatalogManifestBuilder (срез v1, ADR-075 §5): только активные версии
/// (label + все Revit-варианты), перенос §13.19-данных (V32/V33/V36–V38),
/// PII-whitelist, CAS-хеши файлов, round-trip стабильность JSON.
/// </summary>
public sealed class CatalogManifestBuilderTests : IDisposable
{
    private readonly TempCatalogFixture _fixture = new();
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));
    private readonly CatalogManifestBuilder _builder;

    public CatalogManifestBuilderTests()
    {
        _builder = new CatalogManifestBuilder(_fixture.GetDatabase(), new StoragePathResolver(_fixture.GetDatabase()), _clock);
    }

    public void Dispose() => _fixture.Dispose();

    private CatalogManifestBuildOptions Options(string catalogId = "c0a80101-0000-4000-8000-000000000001") =>
        new()
        {
            CatalogId = catalogId,
            PublishSeq = 5,
            PublishedBy = "BIM-отдел ВентПроект",
            MinPluginVersion = "2.1.0",
        };

    [Fact]
    public async Task BuildAsync_EmptyCatalog_EmptyItemsAndNullRange()
    {
        var manifest = await _builder.BuildAsync(Options());

        Assert.Equal("smartcon.cloud.catalog-manifest", manifest.Format);
        Assert.Equal(1, manifest.FormatVersion);
        Assert.Equal(5, manifest.PublishSeq);
        Assert.Equal("BIM-отдел ВентПроект", manifest.PublishedBy);
        Assert.Equal(_clock.UtcNow, manifest.PublishedAtUtc);
        Assert.Empty(manifest.Items);
        Assert.Empty(manifest.Removed);
        Assert.Null(manifest.RevitVersionRange);
        Assert.Null(manifest.HashFormatVersion);
        Assert.Empty(manifest.Meta.Categories);
    }

    [Fact]
    public async Task BuildAsync_MixedVersions_OnlyActiveLabelVariantsTravel()
    {
        var (itemId, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA", "v1", revitVersion: 2025);
        await CatalogSeedHelper.SeedAdditionalVariantAsync(_fixture, itemId, "FamA", "v1", 2021); // активный вариант
        await CatalogSeedHelper.SeedAdditionalVariantAsync(_fixture, itemId, "FamA", "v2", 2025); // неактивный label

        var manifest = await _builder.BuildAsync(Options());

        var item = Assert.Single(manifest.Items);
        Assert.Equal(itemId, item.Id);
        Assert.Equal("v1", item.CurrentVersionLabel);
        Assert.Equal(2, item.Versions.Count);
        Assert.All(item.Versions, v => Assert.Equal("v1", v.VersionLabel));
        Assert.Contains(item.Versions, v => v.SourceRevitVersion == 2021);
        Assert.Contains(item.Versions, v => v.SourceRevitVersion == 2025);
        Assert.Equal(2021, manifest.RevitVersionRange!.Min);
        Assert.Equal(2025, manifest.RevitVersionRange!.Max);
    }

    [Fact]
    public async Task BuildAsync_KnownFileContent_Sha256Matches()
    {
        var (itemId, _, _, relativePath) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamB", "v1", revitVersion: 2025);
        var content = Encoding.UTF8.GetBytes("HELLO-CLOUD-CONTENT");
        var absolute = Path.Combine(_fixture.GetDatabaseRoot(), relativePath);
        await File.WriteAllBytesAsync(absolute, content);

        var manifest = await _builder.BuildAsync(Options());

        var item = Assert.Single(manifest.Items);
        var file = Assert.Single(item.Versions).File;
        var expected = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        Assert.Equal(expected, file.Sha256);
        Assert.Equal(content.Length, file.SizeBytes);
        Assert.Equal("FamB.rfa", file.FileName);
    }

    [Fact]
    public async Task BuildAsync_MissingFileOnDisk_ItemSkipped()
    {
        var (itemId, _, _, relativePath) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamC", "v1");
        File.Delete(Path.Combine(_fixture.GetDatabaseRoot(), relativePath));

        var manifest = await _builder.BuildAsync(Options());

        Assert.Empty(manifest.Items);
    }

    [Fact]
    public async Task BuildAsync_QuarantinedUncategorizedItem_ExcludedFromManifest()
    {
        // Import Validation Gate: карантин «Без категории» не публикуется —
        // подписчик не должен получать непровалидированный контент.
        // null = явный карантин (сид-хелпер иначе подставляет дефолтную категорию).
        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "Карантин", "v1", categoryId: null);

        var manifest = await _builder.BuildAsync(Options());

        Assert.Empty(manifest.Items);
    }

    [Fact]
    public async Task BuildAsync_NestedCategory_PathUsesSeparator()
    {
        var parentId = Guid.NewGuid().ToString();
        var childId = Guid.NewGuid().ToString();
        using (var conn = _fixture.GetDatabase().CreateConnection())
        {
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            await ExecAsync(conn, tx,
                "INSERT INTO categories (id, name, parent_id, sort_order, created_at_utc) VALUES (@id, 'Трубопроводы', NULL, 0, @t)",
                ("@id", parentId), ("@t", DateTimeOffset.UtcNow.ToString("o")));
            await ExecAsync(conn, tx,
                "INSERT INTO categories (id, name, parent_id, sort_order, created_at_utc) VALUES (@id, 'Отводы', @parent, 0, @t)",
                ("@id", childId), ("@parent", parentId), ("@t", DateTimeOffset.UtcNow.ToString("o")));
            await tx.CommitAsync();
        }

        await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamD", "v1", categoryId: childId);

        var manifest = await _builder.BuildAsync(Options());

        var item = Assert.Single(manifest.Items);
        Assert.Equal("Трубопроводы > Отводы", item.CategoryPath);
        var root = Assert.Single(manifest.Meta.Categories);
        Assert.Equal("Трубопроводы", root.Name);
        var child = Assert.Single(root.Children);
        Assert.Equal("Отводы", child.Name);
    }

    [Fact]
    public async Task BuildAsync_SystemItem_FamilyKeyFromTypes()
    {
        var (itemId, versionId, fileId, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "Стена газобетон", "v1", revitVersion: 2025, familySource: "system", revitCategory: "Стены", revitCategoryId: -2000011);
        var typeId = Guid.NewGuid().ToString();
        using (var conn = _fixture.GetDatabase().CreateConnection())
        {
            await conn.OpenAsync();
            await ExecAsync(conn, null,
                "INSERT INTO family_types (id, catalog_item_id, type_name, sort_order, version_id, file_id, family_name, family_key) " +
                "VALUES (@id, @itemId, 'Газобетон 400', 0, @versionId, @fileId, 'Базовая стена', 'Основные_Стены_Газобетон')",
                ("@id", typeId), ("@itemId", itemId), ("@versionId", versionId), ("@fileId", fileId));
        }

        var manifest = await _builder.BuildAsync(Options());

        var item = Assert.Single(manifest.Items);
        Assert.Equal("system", item.FamilySource);
        Assert.Equal("Основные_Стены_Газобетон", item.FamilyKey);
        var version = Assert.Single(item.Versions);
        Assert.Equal("stagedRvt", version.FileKind);
        var type = Assert.Single(version.Types);
        Assert.Equal("Газобетон 400", type.TypeName);
        Assert.Equal("Основные_Стены_Газобетон", type.FamilyKey);
    }

    [Fact]
    public async Task BuildAsync_TypeWithParameter_ParameterTravelsWithUnits()
    {
        var (itemId, versionId, fileId, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamE", "v1");
        var runId = await CatalogSeedHelper.SeedDataExtractedAsync(_fixture, itemId, versionId, fileId);
        var typeId = await ReadSingleTypeIdAsync(versionId);
        using (var conn = _fixture.GetDatabase().CreateConnection())
        {
            await conn.OpenAsync();
            await ExecAsync(conn, null,
                """
                INSERT INTO extracted_attribute_values
                    (id, catalog_item_id, version_id, file_id, type_id, parameter_name, parameter_scope,
                     storage_type, value_text, value_number, unit_type_id, status, extraction_run_id, extracted_at_utc)
                VALUES (@id, @itemId, @versionId, @fileId, @typeId, 'ADSK_Размер', 'Type',
                        'Double', NULL, 0.164041994750656, 'autodesk.spec.aec.length-1.0.0', 'Found', @runId, @t)
                """,
                ("@id", Guid.NewGuid().ToString()), ("@itemId", itemId), ("@versionId", versionId),
                ("@fileId", fileId), ("@typeId", typeId), ("@runId", runId),
                ("@t", DateTimeOffset.UtcNow.ToString("o")));
        }

        var manifest = await _builder.BuildAsync(Options());

        var type = Assert.Single(Assert.Single(Assert.Single(manifest.Items).Versions).Types);
        var p = Assert.Single(type.Parameters);
        Assert.Equal("ADSK_Размер", p.ParameterName);
        Assert.Equal("Type", p.ParameterScope);
        Assert.Equal("Double", p.StorageType);
        Assert.Equal(0.164041994750656, p.ValueNumber);
        Assert.Equal("autodesk.spec.aec.length-1.0.0", p.UnitTypeId);
    }

    [Fact]
    public async Task BuildAsync_RoutingWorldBAndHashes_TravelInSections()
    {
        var (itemId, versionId, fileId, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamF", "v1");
        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction();
        await ExecAsync(conn, tx,
            "INSERT INTO item_routing_rules (catalog_item_id, family_key, type_name, group_key, rule_order, part_name, description, criteria_json) " +
            "VALUES (@itemId, '', 'DN50', 'Муфты', 0, 'Муфта', '', '[{\"kind\":\"eq\"}]')",
            ("@itemId", itemId));
        await ExecAsync(conn, tx,
            "INSERT INTO item_routing_type_settings (catalog_item_id, family_key, type_name, preferred_junction_type) " +
            "VALUES (@itemId, '', 'DN50', 2)",
            ("@itemId", itemId));
        await ExecAsync(conn, tx,
            "INSERT INTO family_routing_rules (catalog_item_id, catalog_version_id, family_key, type_name, group_key, rule_order, part_name, description, criteria_json) " +
            "VALUES (@itemId, @versionId, '', 'DN50', 'Муфты', 0, NULL, '', '[]')",
            ("@itemId", itemId), ("@versionId", versionId));
        await ExecAsync(conn, tx,
            "INSERT INTO family_type_hashes (catalog_version_id, type_identity_key, type_name, type_hash, created_at_utc) " +
            "VALUES (@versionId, 'DN50', 'DN50', 'ab12cd34', @t)",
            ("@versionId", versionId), ("@t", DateTimeOffset.UtcNow.ToString("o")));
        await ExecAsync(conn, tx,
            "INSERT INTO family_segment_sizes (catalog_version_id, segment_name, nominal_diameter, inner_diameter, outer_diameter, used_in_size_lists, used_in_sizing, sort_order) " +
            "VALUES (@versionId, 'Диаметр', 0.164041994750656, 0.15, 0.18, 1, 0, 0)",
            ("@versionId", versionId));
        await ExecAsync(conn, tx,
            "INSERT INTO family_segment_rules (catalog_version_id, family_key, type_name, rule_order, segment_name, min_size_feet, max_size_feet, description) " +
            "VALUES (@versionId, '', 'DN50', 0, 'Диаметр', NULL, 0.5, 'до ДУ400')",
            ("@versionId", versionId));
        await ExecAsync(conn, tx,
            "UPDATE catalog_versions SET section_hashes = @hashes, section_strings = @strings WHERE id = @versionId",
            ("@versionId", versionId),
            ("@hashes", "{\"META|DN50\":\"aabb\"}"), ("@strings", "{\"META|DN50\":\"canonical\"}"));
        await tx.CommitAsync();

        var manifest = await _builder.BuildAsync(Options());

        var item = Assert.Single(manifest.Items);
        var rule = Assert.Single(item.RoutingRules);
        Assert.Equal("Муфта", rule.PartName);
        Assert.Equal("[{\"kind\":\"eq\"}]", rule.CriteriaJson);
        var setting = Assert.Single(item.RoutingTypeSettings);
        Assert.Equal(2, setting.PreferredJunctionType);

        var version = Assert.Single(item.Versions);
        Assert.Equal("ab12cd34", Assert.Single(version.TypeHashes).TypeHash);
        var size = Assert.Single(version.SegmentSizes);
        Assert.Equal(0.164041994750656, size.NominalDiameter);
        Assert.True(size.UsedInSizeLists);
        var segmentRule = Assert.Single(version.SegmentRules);
        Assert.Null(segmentRule.MinSizeFeet);
        Assert.Equal(0.5, segmentRule.MaxSizeFeet);
        Assert.Equal("до ДУ400", segmentRule.Description);
        Assert.Single(version.VersionRoutingRules);
        Assert.Equal("aabb", version.SectionHashes!["META|DN50"]);
        Assert.Equal("canonical", version.SectionStrings!["META|DN50"]);
    }

    [Fact]
    public async Task BuildAsync_GlbPoolAssetExcluded_ImageAssetIncluded()
    {
        var (itemId, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamG", "v1");
        var avatarDir = Path.Combine(_fixture.GetDatabaseRoot(), "files", itemId);
        Directory.CreateDirectory(avatarDir);
        await File.WriteAllBytesAsync(Path.Combine(avatarDir, "avatar.png"), [1, 2, 3]);

        using (var conn = _fixture.GetDatabase().CreateConnection())
        {
            await conn.OpenAsync();
            await ExecAsync(conn, null,
                "INSERT INTO family_assets (id, catalog_item_id, version_label, asset_type, file_name, relative_path, size_bytes, description, created_at_utc, is_primary) " +
                "VALUES (@id, @itemId, NULL, 'Model3D', 'hash40.glb', 'files/_shared/models/ab/ab12.glb', 100, '', @t, 0)",
                ("@id", Guid.NewGuid().ToString()), ("@itemId", itemId), ("@t", DateTimeOffset.UtcNow.ToString("o")));
            var imagePath = Path.Combine(_fixture.GetDatabaseRoot(), "files", itemId, "v1", "photo.png");
            Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
            await File.WriteAllBytesAsync(imagePath, [9, 9, 9, 9]);
            await ExecAsync(conn, null,
                "INSERT INTO family_assets (id, catalog_item_id, version_label, asset_type, file_name, relative_path, size_bytes, description, created_at_utc, is_primary) " +
                "VALUES (@id, @itemId, 'v1', 'Image', 'photo.png', @path, 4, 'фото', @t, 1)",
                ("@id", Guid.NewGuid().ToString()), ("@itemId", itemId),
                ("@path", $"files/{itemId}/v1/photo.png"), ("@t", DateTimeOffset.UtcNow.ToString("o")));
        }

        var manifest = await _builder.BuildAsync(Options());

        var item = Assert.Single(manifest.Items);
        var asset = Assert.Single(item.Assets);
        Assert.Equal("Image", asset.AssetType);
        Assert.Equal("фото", asset.Description);
        Assert.True(asset.IsPrimary);
        Assert.Equal("v1", asset.VersionLabel);
        Assert.NotNull(item.Avatar);
        Assert.Equal("avatar.png", item.Avatar!.FileName);
    }

    [Fact]
    public async Task BuildAsync_TagsFactsNestedDependencies_Travel()
    {
        var (itemId, versionId, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamH", "v1");
        var (childId, childVersionId, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "Nested", "v1");
        await CatalogSeedHelper.SeedFactAsync(_fixture, itemId, "PartType", "23", "Тройник");
        using (var conn = _fixture.GetDatabase().CreateConnection())
        {
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            await ExecAsync(conn, tx,
                "INSERT INTO catalog_tags (catalog_item_id, tag, normalized_tag) VALUES (@itemId, 'сталь', 'сталь')",
                ("@itemId", itemId));
            await ExecAsync(conn, tx,
                "INSERT INTO family_nested_shared_families (catalog_item_id, version_id, nested_family_name) VALUES (@itemId, @versionId, 'Болт М12')",
                ("@itemId", itemId), ("@versionId", versionId));
            await ExecAsync(conn, tx,
                "INSERT INTO family_dependencies (parent_catalog_item_id, parent_version_id, child_catalog_item_id, dependency_kind, part_name, ordinal, child_version_label) " +
                "VALUES (@itemId, @versionId, @childId, 'routing', 'Тройник', 0, 'v1')",
                ("@itemId", itemId), ("@versionId", versionId), ("@childId", childId));
            await tx.CommitAsync();
        }

        var manifest = await _builder.BuildAsync(Options());

        var item = Assert.Single(manifest.Items, i => i.Id == itemId);
        Assert.Equal("сталь", Assert.Single(item.Tags));
        var fact = Assert.Single(item.Facts);
        Assert.Equal("PartType", fact.Key);
        Assert.Equal("Тройник", fact.ValueDisplay);
        var version = Assert.Single(item.Versions);
        Assert.Equal("Болт М12", Assert.Single(version.NestedSharedFamilies));
        var dep = Assert.Single(version.Dependencies);
        Assert.Equal(childId, dep.ChildItemId);
        Assert.Equal("routing", dep.Kind);
        Assert.Equal("v1", dep.ChildVersionLabel);
    }

    [Fact]
    public async Task BuildAsync_MetaAttributesBindingsAssignmentRules_Travel()
    {
        var categoryId = Guid.NewGuid().ToString();
        var attributeId = Guid.NewGuid().ToString();
        var bindingId = Guid.NewGuid().ToString();
        var groupId = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow.ToString("o");
        using (var conn = _fixture.GetDatabase().CreateConnection())
        {
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            await ExecAsync(conn, tx,
                "INSERT INTO categories (id, name, parent_id, sort_order, created_at_utc) VALUES (@id, 'Арматура', NULL, 0, @t)",
                ("@id", categoryId), ("@t", now));
            await ExecAsync(conn, tx,
                "INSERT INTO attribute_definitions (id, name, group_name, is_active, created_at_utc) VALUES (@id, 'DN', 'Геометрия', 1, @t)",
                ("@id", attributeId), ("@t", now));
            await ExecAsync(conn, tx,
                "INSERT INTO category_attribute_bindings (id, category_id, attribute_id, sort_order, is_enabled) VALUES (@id, @cat, @attr, 0, 1)",
                ("@id", bindingId), ("@cat", categoryId), ("@attr", attributeId));
            await ExecAsync(conn, tx,
                "INSERT INTO category_validation_rules (binding_id, operator, value_text, is_enabled, sort_order) VALUES (@binding, 'InRange', NULL, 1, 0)",
                ("@binding", bindingId));
            await ExecAsync(conn, tx,
                "INSERT INTO category_assignment_rule_groups (id, category_id, sort_order, is_enabled) VALUES (@id, @cat, 0, 1)",
                ("@id", groupId), ("@cat", categoryId));
            await ExecAsync(conn, tx,
                "INSERT INTO category_assignment_conditions (group_id, source_kind, attribute_id, system_key, operator, value_text, sort_order, is_enabled) " +
                "VALUES (@group, 'attribute', @attr, NULL, 'Equals', 'Ду50', 0, 1)",
                ("@group", groupId), ("@attr", attributeId));
            await tx.CommitAsync();
        }

        var manifest = await _builder.BuildAsync(Options());

        var attribute = Assert.Single(manifest.Meta.Attributes);
        Assert.Equal("DN", attribute.Name);
        Assert.Equal("Геометрия", attribute.Group);
        var binding = Assert.Single(manifest.Meta.Bindings);
        Assert.Equal("Арматура", binding.CategoryPath);
        Assert.Equal("DN", binding.AttributeName);
        Assert.Single(binding.ValidationRules);
        var assignmentRule = Assert.Single(manifest.Meta.AssignmentRules);
        Assert.Equal("Арматура", assignmentRule.CategoryPath);
        var condition = Assert.Single(Assert.Single(assignmentRule.Groups).Conditions);
        Assert.Equal("attribute", condition.SourceKind);
        Assert.Equal("DN", condition.AttributeName);
        Assert.Equal("Ду50", condition.ValueText);
    }

    [Fact]
    public async Task Serialize_RoundTrip_JsonStableAndPiiSafe()
    {
        var (itemId, versionId, fileId, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "FamI", "v1", hashFormatVersion: 22, contentHash: "AABBCC");
        await CatalogSeedHelper.SeedDataExtractedAsync(_fixture, itemId, versionId, fileId);

        var manifest = await _builder.BuildAsync(Options());
        var json1 = JsonSerializer.Serialize(manifest, CatalogManifestJson.WriteCompact);

        // PII-whitelist: в манифесте не должно быть email/user@machine (ADR-076 §4).
        Assert.DoesNotContain("@", json1.Replace(".rfa", string.Empty).Replace("autodesk.spec", string.Empty));

        var restored = JsonSerializer.Deserialize<CatalogManifestV1>(json1, CatalogManifestJson.Read);
        Assert.NotNull(restored);
        var json2 = JsonSerializer.Serialize(restored, CatalogManifestJson.Read);
        // Round-trip через Read-опции (case-insensitive) должен давать стабильный wire-формат.
        Assert.Equal(json1, JsonSerializer.Serialize(
            JsonSerializer.Deserialize<CatalogManifestV1>(json2, CatalogManifestJson.Read),
            CatalogManifestJson.WriteCompact));

        Assert.Equal(22, restored!.HashFormatVersion);
        Assert.Equal("BIM-отдел ВентПроект", restored.PublishedBy);
        var restoredItem = Assert.Single(restored.Items);
        Assert.Equal("AABBCC", Assert.Single(restoredItem.Versions).ContentHash);
    }

    [Fact]
    public async Task BuildAsync_LegacyRetiredStatus_MappedToDeprecated()
    {
        var (itemId, versionId, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamJ", "v1");
        using (var conn = _fixture.GetDatabase().CreateConnection())
        {
            await conn.OpenAsync();
            await ExecAsync(conn, null,
                "UPDATE catalog_items SET content_status = 'Retired' WHERE id = @itemId",
                ("@itemId", itemId));
        }

        var manifest = await _builder.BuildAsync(Options());

        // Wire-контракт двух-state: legacy "Retired" маппится на границе (§5, #109).
        Assert.Equal("Deprecated", Assert.Single(manifest.Items).ContentStatus);
    }

    [Fact]
    public async Task BuildAsync_CorruptSectionJson_NullDictionaries()
    {
        var (itemId, versionId, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamK", "v1");
        using (var conn = _fixture.GetDatabase().CreateConnection())
        {
            await conn.OpenAsync();
            await ExecAsync(conn, null,
                "UPDATE catalog_versions SET section_hashes = '{broken', section_strings = '{broken' WHERE id = @versionId",
                ("@versionId", versionId));
        }

        var manifest = await _builder.BuildAsync(Options());

        var version = Assert.Single(Assert.Single(manifest.Items).Versions);
        Assert.Null(version.SectionHashes);
        Assert.Null(version.SectionStrings);
    }

    [Fact]
    public async Task WriteGoldenSample_WritesManifestJson()
    {
        // Генерирует golden-образец server/contracts/manifest-v1.sample.json (артефакт C0).
        // Прогон: dotnet test --filter WriteGoldenSample; файл копируется коммитом.
        var (itemId, versionId, fileId, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "Отвод 90° стальной", "v3", revitVersion: 2023,
            hashFormatVersion: 22, contentHash: "A1B2C3D4E5F6", revitCategory: "Трубы",
            revitCategoryId: -2008055);
        await CatalogSeedHelper.SeedDataExtractedAsync(_fixture, itemId, versionId, fileId);
        var typeId = await ReadSingleTypeIdAsync(versionId);
        using (var conn = _fixture.GetDatabase().CreateConnection())
        {
            await conn.OpenAsync();
            await ExecAsync(conn, null,
                "INSERT INTO catalog_tags (catalog_item_id, tag, normalized_tag) VALUES (@itemId, 'сталь', 'сталь')",
                ("@itemId", itemId));
            await ExecAsync(conn, null,
                "UPDATE family_types SET family_name = 'Отвод 90°' WHERE id = @typeId",
                ("@typeId", typeId));
        }

        var manifest = await _builder.BuildAsync(Options("f47ac10b-58cc-4372-a567-0e02b2c3d479"));
        var json = JsonSerializer.Serialize(manifest, CatalogManifestJson.WriteIndented);
        var target = Path.Combine(Path.GetTempPath(), "manifest-v1.sample.json");
        await File.WriteAllTextAsync(target, json);
        Assert.Contains("\"format\": \"smartcon.cloud.catalog-manifest\"", json);
    }

    private async Task<string> ReadSingleTypeIdAsync(string versionId)
    {
        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM family_types WHERE version_id = @versionId";
        cmd.Parameters.Add(new SqliteParameter("@versionId", versionId));
        var result = await cmd.ExecuteScalarAsync();
        return (string)result!;
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
}
