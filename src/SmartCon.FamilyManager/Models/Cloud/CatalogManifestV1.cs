using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using SmartCon.FamilyManager.Models.Metadata;

namespace SmartCon.FamilyManager.Models.Cloud;

/// <summary>
/// Wire-format облачной синхронизации FamilyManager — манифест v1
/// (ADR-075, мастер-план §5). Идемпотентный снимок каталога на publish
/// point: только активные версии (label = current_version_label вместе со
/// всеми его Revit-вариантами — файл, созданный в Revit N, не открывается
/// в Revit &lt; N, поэтому варианты r2021/r2025 одного label едут как
/// отдельные version-записи), routing World B (V37 item-level + V36/V38
/// per-version) и per-type/section хэши (V32/V33) — без них подписная
/// копия теряет вкладку «Трассировка» (actualization на Subscribed
/// отключена, ADR-075 §7). PII-whitelist: <see cref="CatalogManifestV1.PublishedBy"/>
/// и publishedBy версий — только displayName (ADR-076 §4).
/// </summary>
public sealed class CatalogManifestV1
{
    [JsonPropertyName("format")]
    public string Format { get; init; } = "smartcon.cloud.catalog-manifest";

    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; init; } = 1;

    [JsonPropertyName("catalogId")]
    public string CatalogId { get; init; } = string.Empty;

    [JsonPropertyName("publishSeq")]
    public long PublishSeq { get; init; }

    [JsonPropertyName("publishedAtUtc")]
    public DateTimeOffset PublishedAtUtc { get; init; }

    /// <summary>displayName автора публикации; email и user@machine запрещены (ADR-076 §4).</summary>
    [JsonPropertyName("publishedBy")]
    public string PublishedBy { get; init; } = string.Empty;

    [JsonPropertyName("minPluginVersion")]
    public string? MinPluginVersion { get; init; }

    /// <summary>FHV-эпоха каталога; несовпадение у подписчика → sync отклоняется (гейт E7).</summary>
    [JsonPropertyName("hashFormatVersion")]
    public int? HashFormatVersion { get; init; }

    /// <summary>Агрегат по sourceRevitVersion всех версий — гейт совместимости файлов.</summary>
    [JsonPropertyName("revitVersionRange")]
    public ManifestRevitRangeV1? RevitVersionRange { get; init; }

    /// <summary>Metadata-слой — metadata package v4 как есть (§5).</summary>
    [JsonPropertyName("meta")]
    public ManifestMetaV1 Meta { get; init; } = new();

    [JsonPropertyName("items")]
    public List<ManifestItemV1> Items { get; init; } = [];

    /// <summary>Tombstones удалённых у автора items (ADR-077 §6). Срез v1: всегда пусто.</summary>
    [JsonPropertyName("removed")]
    public List<ManifestRemovedV1> Removed { get; init; } = [];
}

public sealed class ManifestRevitRangeV1
{
    [JsonPropertyName("min")]
    public int Min { get; init; }

    [JsonPropertyName("max")]
    public int Max { get; init; }
}

/// <summary>Meta-слой манифеста: структуры metadata package v4 без изменений.</summary>
public sealed class ManifestMetaV1
{
    [JsonPropertyName("categories")]
    public List<MetadataExportCategoryNode> Categories { get; init; } = [];

    [JsonPropertyName("attributes")]
    public List<MetadataExportAttribute> Attributes { get; init; } = [];

    [JsonPropertyName("bindings")]
    public List<MetadataExportBinding> Bindings { get; init; } = [];

    [JsonPropertyName("assignmentRules")]
    public List<MetadataExportAssignmentRule> AssignmentRules { get; init; } = [];
}

public sealed class ManifestRemovedV1
{
    [JsonPropertyName("itemId")]
    public string ItemId { get; init; } = string.Empty;

    [JsonPropertyName("versionLabel")]
    public string? VersionLabel { get; init; }
}

public sealed class ManifestItemV1
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("normalizedName")]
    public string NormalizedName { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Путь категории строкой (переносимость между базами, §5); пустая строка = без категории.</summary>
    [JsonPropertyName("categoryPath")]
    public string CategoryPath { get; init; } = string.Empty;

    [JsonPropertyName("contentStatus")]
    public string ContentStatus { get; init; } = "Active";

    [JsonPropertyName("familySource")]
    public string FamilySource { get; init; } = "loadable";

    [JsonPropertyName("revitCategory")]
    public string? RevitCategory { get; init; }

    /// <summary>BuiltInCategory ordinal — локале-инвариантный (FHV3-философия).</summary>
    [JsonPropertyName("revitCategoryId")]
    public int? RevitCategoryId { get; init; }

    /// <summary>System-семейства: ключ системного семейства (ADR-064), из family_key типов активной версии. Заполняется после чтения типов.</summary>
    [JsonPropertyName("familyKey")]
    public string? FamilyKey { get; set; }

    [JsonPropertyName("currentVersionLabel")]
    public string CurrentVersionLabel { get; init; } = string.Empty;

    [JsonPropertyName("tags")]
    public List<string> Tags { get; init; } = [];

    /// <summary>Все Revit-варианты активного label (решение 2026-09-11: публикуются только активные версии).</summary>
    [JsonPropertyName("versions")]
    public List<ManifestVersionV1> Versions { get; init; } = [];

    /// <summary>Ассеты семейства, кроме GLB-превью из CAS-пула (превью генерятся локально, §5).</summary>
    [JsonPropertyName("assets")]
    public List<ManifestAssetV1> Assets { get; init; } = [];

    [JsonPropertyName("avatar")]
    public ManifestFileRefV1? Avatar { get; set; }

    /// <summary>Item-level трассировка World B (item_routing_rules, V37).</summary>
    [JsonPropertyName("routingRules")]
    public List<ManifestRoutingRuleV1> RoutingRules { get; init; } = [];

    /// <summary>Item-level routing-скаляры (item_routing_type_settings, V37).</summary>
    [JsonPropertyName("routingTypeSettings")]
    public List<ManifestRoutingTypeSettingV1> RoutingTypeSettings { get; init; } = [];

    /// <summary>Факты ADR-055 (family_facts — item-level по схеме, переносится в item-секцию).</summary>
    [JsonPropertyName("facts")]
    public List<ManifestFactV1> Facts { get; init; } = [];
}

public sealed class ManifestVersionV1
{
    [JsonPropertyName("versionLabel")]
    public string VersionLabel { get; init; } = string.Empty;

    [JsonPropertyName("contentHash")]
    public string? ContentHash { get; init; }

    /// <summary>V33 (ADR-071): плоский словарь SECTION→hash (ключ "SECTION" или "SECTION|TypeName").</summary>
    [JsonPropertyName("sectionHashes")]
    public Dictionary<string, string>? SectionHashes { get; init; }

    /// <summary>V33: канонические строки секций (SECTION→canonical string).</summary>
    [JsonPropertyName("sectionStrings")]
    public Dictionary<string, string>? SectionStrings { get; init; }

    [JsonPropertyName("sourceRevitVersion")]
    public int SourceRevitVersion { get; init; }

    /// <summary>"rfa" (loadable) | "stagedRvt" (system).</summary>
    [JsonPropertyName("fileKind")]
    public string FileKind { get; init; } = "rfa";

    [JsonPropertyName("typesCount")]
    public int? TypesCount { get; init; }

    [JsonPropertyName("parametersCount")]
    public int? ParametersCount { get; init; }

    [JsonPropertyName("publishedAtUtc")]
    public DateTimeOffset? PublishedAtUtc { get; init; }

    [JsonPropertyName("publishedBy")]
    public string? PublishedBy { get; init; }

    /// <summary>null = нормально; -1 = терминальный «нет извлекаемой геометрии» (#157).</summary>
    [JsonPropertyName("glbState")]
    public int? GlbState { get; init; }

    /// <summary>V35: 0 pending / 1 done / -1/-2 терминальные — апликер переносит как есть.</summary>
    [JsonPropertyName("routingBackfilled")]
    public int? RoutingBackfilled { get; init; }

    /// <summary>V13, workaround REVIT-198137 — восстановимо только здесь, без Revit.</summary>
    [JsonPropertyName("nestedSharedFamilies")]
    public List<string> NestedSharedFamilies { get; init; } = [];

    [JsonPropertyName("file")]
    public ManifestFileRefV1 File { get; init; } = new();

    [JsonPropertyName("types")]
    public List<ManifestTypeV1> Types { get; init; } = [];

    /// <summary>V32 per-type хэши (family_type_hashes).</summary>
    [JsonPropertyName("typeHashes")]
    public List<ManifestTypeHashV1> TypeHashes { get; init; } = [];

    /// <summary>V36 таблицы размеров сегментов (family_segment_sizes), футы — internal units.</summary>
    [JsonPropertyName("segmentSizes")]
    public List<ManifestSegmentSizeV1> SegmentSizes { get; init; } = [];

    /// <summary>V38 per-version сегментные правила (family_segment_rules).</summary>
    [JsonPropertyName("segmentRules")]
    public List<ManifestSegmentRuleV1> SegmentRules { get; init; } = [];

    /// <summary>V34 routing этой версии (family_routing_rules) — исторический per-version слой, копируется как есть.</summary>
    [JsonPropertyName("versionRoutingRules")]
    public List<ManifestRoutingRuleV1> VersionRoutingRules { get; init; } = [];

    [JsonPropertyName("versionRoutingTypeSettings")]
    public List<ManifestRoutingTypeSettingV1> VersionRoutingTypeSettings { get; init; } = [];

    /// <summary>V29/V30: зависимости от других items каталога (childItemId стабилен — те же guid).</summary>
    [JsonPropertyName("dependencies")]
    public List<ManifestDependencyV1> Dependencies { get; init; } = [];
}

public sealed class ManifestFileRefV1
{
    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; init; }

    [JsonPropertyName("fileName")]
    public string FileName { get; init; } = string.Empty;

    /// <summary>Локальный путь объекта у АВТОРА (publish-флоу ищет файлы для upload). Не сериализуется.</summary>
    [JsonIgnore]
    public string? LocalPath { get; set; }
}

public sealed class ManifestTypeV1
{
    [JsonPropertyName("typeName")]
    public string TypeName { get; init; } = string.Empty;

    [JsonPropertyName("familyName")]
    public string FamilyName { get; init; } = string.Empty;

    [JsonPropertyName("familyKey")]
    public string FamilyKey { get; init; } = string.Empty;

    [JsonPropertyName("sortOrder")]
    public int SortOrder { get; init; }

    [JsonPropertyName("parameters")]
    public List<ManifestParameterV1> Parameters { get; init; } = [];
}

/// <summary>Значение extracted_attribute_values: internal units + исходный unit_type_id, конвертация на клиенте (I-02).</summary>
public sealed class ManifestParameterV1
{
    [JsonPropertyName("parameterName")]
    public string ParameterName { get; init; } = string.Empty;

    [JsonPropertyName("parameterScope")]
    public string? ParameterScope { get; init; }

    [JsonPropertyName("storageType")]
    public string StorageType { get; init; } = string.Empty;

    [JsonPropertyName("valueText")]
    public string? ValueText { get; init; }

    [JsonPropertyName("valueRaw")]
    public string? ValueRaw { get; init; }

    [JsonPropertyName("valueNumber")]
    public double? ValueNumber { get; init; }

    [JsonPropertyName("unitTypeId")]
    public string? UnitTypeId { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "Found";
}

public sealed class ManifestTypeHashV1
{
    [JsonPropertyName("typeIdentityKey")]
    public string TypeIdentityKey { get; init; } = string.Empty;

    [JsonPropertyName("typeName")]
    public string TypeName { get; init; } = string.Empty;

    [JsonPropertyName("typeHash")]
    public string TypeHash { get; init; } = string.Empty;
}

public sealed class ManifestSegmentSizeV1
{
    [JsonPropertyName("segmentName")]
    public string SegmentName { get; init; } = string.Empty;

    [JsonPropertyName("nominalDiameter")]
    public double NominalDiameter { get; init; }

    [JsonPropertyName("innerDiameter")]
    public double InnerDiameter { get; init; }

    [JsonPropertyName("outerDiameter")]
    public double OuterDiameter { get; init; }

    [JsonPropertyName("usedInSizeLists")]
    public bool UsedInSizeLists { get; init; }

    [JsonPropertyName("usedInSizing")]
    public bool UsedInSizing { get; init; }

    [JsonPropertyName("sortOrder")]
    public int SortOrder { get; init; }
}

public sealed class ManifestSegmentRuleV1
{
    [JsonPropertyName("familyKey")]
    public string FamilyKey { get; init; } = string.Empty;

    [JsonPropertyName("typeName")]
    public string TypeName { get; init; } = string.Empty;

    [JsonPropertyName("ruleOrder")]
    public int RuleOrder { get; init; }

    [JsonPropertyName("segmentName")]
    public string SegmentName { get; init; } = string.Empty;

    /// <summary>null = без ограничения.</summary>
    [JsonPropertyName("minSizeFeet")]
    public double? MinSizeFeet { get; init; }

    [JsonPropertyName("maxSizeFeet")]
    public double? MaxSizeFeet { get; init; }

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;
}

/// <summary>Routing-правило: item-level (V37, RoutingRules item) или per-version (V34, VersionRoutingRules).</summary>
public sealed class ManifestRoutingRuleV1
{
    [JsonPropertyName("familyKey")]
    public string FamilyKey { get; init; } = string.Empty;

    [JsonPropertyName("typeName")]
    public string TypeName { get; init; } = string.Empty;

    [JsonPropertyName("groupKey")]
    public string GroupKey { get; init; } = string.Empty;

    [JsonPropertyName("ruleOrder")]
    public int RuleOrder { get; init; }

    /// <summary>null = правило «без детали» («Нет»).</summary>
    [JsonPropertyName("partName")]
    public string? PartName { get; init; }

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    /// <summary>criteria_json сохраняется как строку (произвольные виды критериев, V34).</summary>
    [JsonPropertyName("criteriaJson")]
    public string CriteriaJson { get; init; } = "[]";
}

public sealed class ManifestRoutingTypeSettingV1
{
    [JsonPropertyName("familyKey")]
    public string FamilyKey { get; init; } = string.Empty;

    [JsonPropertyName("typeName")]
    public string TypeName { get; init; } = string.Empty;

    [JsonPropertyName("preferredJunctionType")]
    public int PreferredJunctionType { get; init; }
}

public sealed class ManifestDependencyV1
{
    [JsonPropertyName("childItemId")]
    public string ChildItemId { get; init; } = string.Empty;

    /// <summary>"routing" | "shared_nested" (dependency_kind).</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("childVersionLabel")]
    public string? ChildVersionLabel { get; init; }

    [JsonPropertyName("partName")]
    public string? PartName { get; init; }

    [JsonPropertyName("ordinal")]
    public int Ordinal { get; init; }
}

public sealed class ManifestAssetV1
{
    /// <summary>Имя enum FamilyAssetType (Image/Video/Document/Model3D/LookupTable/Spreadsheet/Other).</summary>
    [JsonPropertyName("assetType")]
    public string AssetType { get; init; } = string.Empty;

    [JsonPropertyName("fileName")]
    public string FileName { get; init; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; init; }

    [JsonPropertyName("isPrimary")]
    public bool IsPrimary { get; init; }

    /// <summary>null = общесемейный ассет.</summary>
    [JsonPropertyName("versionLabel")]
    public string? VersionLabel { get; init; }

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    /// <summary>Локальный путь ассета у автора (upload). Не сериализуется.</summary>
    [JsonIgnore]
    public string? LocalPath { get; set; }
}

public sealed class ManifestFactV1
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("valueKey")]
    public string ValueKey { get; init; } = string.Empty;

    [JsonPropertyName("valueDisplay")]
    public string ValueDisplay { get; init; } = string.Empty;
}

/// <summary>Общие JsonSerializerOptions манифеста v1: чтение — толерантное, запись — relaxed-encoder (кириллица).</summary>
public static class CatalogManifestJson
{
    public static readonly JsonSerializerOptions Read = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static readonly JsonSerializerOptions WriteCompact = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly JsonSerializerOptions WriteIndented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
