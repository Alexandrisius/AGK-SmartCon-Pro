---
module: family-manager
---
# Модели FamilyManager — Снапшоты и геометрия

> Часть документации модуля FamilyManager. Индекс и навигация: [README.md](README.md).
> Источник истины: `src/SmartCon.Core/Models/FamilyManager/*.cs`.

## FamilySnapshot

Structured snapshot of a loadable family (.rfa) used to compute a `FamilyContentHash`. Extracted in-memory from an open family document — never from file bytes — so it is stable across SaveAs, rename, and Revit upgrade.

**Файл:** `Models/FamilyManager/FamilySnapshot.cs`

```csharp
public sealed record FamilySnapshot(
    string FamilyName,
    string Category,
    IReadOnlyList<FamilyParameterInfo> Parameters,
    IReadOnlyList<FamilyTypeSnapshot> Types,
    GeometryMetrics Geometry,
    IReadOnlyList<string> SharedNestedFamilyNames,
    int? CategoryId = null,
    IReadOnlyList<FamilyFact>? Facts = null,
    IReadOnlyList<ConnectorSnapshot>? Connectors = null,
    FamilyBehaviorFlags? BehaviorFlags = null,
    IReadOnlyList<string>? NonSharedNestedFamilyNames = null);

public sealed record FamilyParameterInfo(
    string Name,
    string StorageType,
    string ParameterGroup,
    bool IsInstance,
    bool IsShared,
    string? Formula,
    bool IsDeterminedByFormula,
    bool IsReporting,
    string? SharedParamGuid,
    string? BuiltInParameterId);

public sealed record FamilyTypeSnapshot(
    string Name,
    IReadOnlyList<FamilyParameterValue> Values);

public sealed record FamilyParameterValue(
    string ParameterName,
    string StorageType,
    bool HasValue,
    string? ValueText,
    double? ValueNumber,
    string? ResolvedElementName,
    string? ValueDisplay = null,
    string? SpecTypeId = null,
    string? UnitTypeId = null);
```

- `FamilyName` — from `FamilyManager` or family document title.
- `Category` — display name (e.g. "Pipe Fittings"). NOT hashed since FHV3 — the ordinal is (ADR-056).
- `CategoryId` — `BuiltInCategory` ordinal (ADR-055). Since FHV3 (ADR-056) it IS the hashed category identity — locale-invariant (RU/EN Revit produce the same hash); the display name is only a fallback when the ordinal is unknown.
- `Facts` — category-driven facts (Part Type; ADR-055). Part of the content hash since FHV3 (ADR-056): for fittings the Part Type defines the family function.
- `Connectors` — connector elements of the family (ADR-056), pre-sorted by the extractor with `LinkedIndex` computed against that order. Part of the content hash since FHV3.
- `BehaviorFlags` — Shared/WorkPlaneBased/AlwaysVertical/CutWithVoids from `OwnerFamily` built-in parameters (ADR-056). Part of the content hash since FHV3.
- `NonSharedNestedFamilyNames` — names of NON-shared nested families (ADR-056), sorted. Part of the content hash since FHV3.
- `Parameters` — all schema-level parameters, sorted by name. Includes `SharedParamGuid` for shared params and `BuiltInParameterId` enum name for built-ins (null for user/shared).
- `Types` — all family types with their values. The unnamed default type is extracted under the hash-stable synthetic name `<default>` so families without user-created types keep their attribute values. UI never shows the literal — `FamilyTypeSnapshot.ResolveDisplayName(typeName, familyName)` substitutes the family name (catalog tree, properties tabs, batch import tooltip).
- `Geometry` — aggregated `GeometryMetrics` from all `GenericForm` elements.
- `SharedNestedFamilyNames` — names of shared nested families (ADR-034), sorted.
- `FamilyParameterValue.HasValue` distinguishes "no value" (`false`) from "value is zero" (`true`, `ValueNumber=0`) — hash treats them differently.
- `FamilyParameterValue.ValueDisplay` — human-readable value formatted per the owning document's unit settings with the unit symbol (e.g. "300 мм", "16 бар"); `null` when not applicable. NOT part of the content hash — display metadata only. `SpecTypeId`/`UnitTypeId` carry the Forge TypeId strings (legacy enum names on R19-R20) and are likewise excluded from the hash.

---

## SystemFamilySnapshot

Structured snapshot of a system family (category + types) inside a project (.rvt). Used to compute a `FamilyContentHash` for system families. Extracted in-memory from the active project document.

**Файл:** `Models/FamilyManager/SystemFamilySnapshot.cs`

```csharp
public sealed record SystemFamilySnapshot(
    string CategoryName,
    int CategoryId,
    IReadOnlyList<SystemTypeSnapshot> Types);

public sealed record SystemTypeSnapshot(
    string Name,
    IReadOnlyList<SystemParameterValue> Values,
    CompoundStructureSnapshot? Structure = null,
    RoutingPreferencesSnapshot? Routing = null,
    string? FamilyName = null,
    string? FamilyKey = null,
    StairsSubtypesSnapshot? Stairs = null,
    RailingStructureSnapshot? Railing = null,
    IReadOnlyList<SegmentSnapshot>? Segments = null,
    WireSettingsSnapshot? Wire = null);

public sealed record SystemParameterValue(
    string ParameterName,
    string StorageType,
    bool HasValue,
    string? ValueText,
    double? ValueNumber,
    string? ResolvedElementName,
    string? ValueDisplay = null,
    string? SpecTypeId = null,
    string? UnitTypeId = null);
```

FHV4 (ADR-065): `FamilyKey` входит в content-хэш; `Stairs`/`Railing`/`Segments` —
identity-сводки для хэша (имена ссылок, не глубокие данные — sync читает эталон
живьём из мини-проекта, ADR-061). FHV5: `Wire` — identity-сводка настроек провода.

### StairsSubtypesSnapshot / RailingStructureSnapshot (FHV4, ADR-065)

```csharp
public sealed record StairsSubtypesSnapshot(
    string? RunTypeName, string? LandingTypeName,
    string? LeftSupportTypeName, string? RightSupportTypeName,
    string? MiddleSupportTypeName, string? CutMarkTypeName);

public sealed record RailingStructureSnapshot(
    string? TopRailTypeName, double? TopRailHeight,
    string? PrimaryHandrailTypeName, double? PrimaryHandrailHeight,
    double? PrimaryHandrailLateralOffset, int? PrimaryHandrailPosition,
    string? SecondaryHandrailTypeName, double? SecondaryHandrailHeight,
    double? SecondaryHandrailLateralOffset, int? SecondaryHandrailPosition,
    IReadOnlyList<RailingRailSnapshot> Rails,
    RailingBalusterSnapshot Balusters);

public sealed record RailingRailSnapshot(
    string Name, double Height, double Offset,
    string? ProfileName, string? MaterialName);

public sealed record RailingBalusterSnapshot(
    double PatternLength, int DistributionJustification, int BreakPattern,
    IReadOnlyList<string?> BalusterFamilyNames,
    bool UseBalusterPerTreadOnStairs, int BalusterPerTreadNumber,
    string? BalusterPerTreadFamilyName);
```

Ссылки на элементы — по ИМЕНАМ (user content, locale-stable); балясины/профили —
family-qualified (`"{Family}:{Type}"`, как routing part names).

### WireSettingsSnapshot (FHV5)

```csharp
public sealed record WireSettingsSnapshot(
    string? MaterialName, string? TemperatureRatingName, string? InsulationName,
    string? MaxSizeName, string? ConduitName,
    double? NeutralMultiplier, bool? NeutralRequired);
```

Настройки провода — это свойства `WireType` поверх графа `ElectricalSetting`
(WireMaterialType → TemperatureRatingType → InsulationType/WireSize, WireConduitType),
а НЕ параметры элемента — generic-пайплайн их не видит (ручной тест 2026-08-04:
смена материала провода не синхронизировалась). Revit 2026 заменил граф на
Conductor*-элементы — сигнатуры свойств ≤2025 там отсутствуют, чтение/запись
защищены try/catch (`MissingMethodException` → Warn + NotConverged).

- `CategoryName` — display name (e.g. "Трубы", "Воздуховоды"). NOT hashed since FHV3 (ADR-056) — locale-dependent.
- `CategoryId` — numeric `BuiltInCategory` ordinal carried as `int` so Core does not depend on `Autodesk.Revit.DB` (I-09). The hashed category identity (FHV3).
- `Types` — selected system types with values, sorted by type name.
- `SystemTypeSnapshot.Structure` — compound structure (layer stack) for wall/floor/roof/ceiling types, `null` otherwise. Part of the content hash since FHV3 (ADR-056).
- `SystemTypeSnapshot.Routing` — routing preferences for MEP curve types (pipe/duct/cable tray/conduit), `null` otherwise. Part of the content hash since FHV3 (ADR-056).
- `SystemParameterValue` — same semantics as `FamilyParameterValue` — distinguishes "no value" from "zero".

**v2.0.0 hash stability:** the hasher skips blank values (`HasValue=false`, empty string, storage-scoped `INVALID`/`UNSUPPORTED`, `READERROR` — v3 narrows the first two by storage type so a user's literal text no longer collides, ADR-056) so the empty `ADSK_Завод-изготовитель` parameter does not contribute to the hash. The hasher also skips the auto-generated `Код IfcGUID` parameter (different per `.rvt` save).

---

## GeometryMetrics

Aggregated geometry metrics for a loadable family document. Used as part of the content fingerprint so that adding/removing a form, or changing an extrusion depth, shifts the hash.

**Файл:** `Models/FamilyManager/GeometryMetrics.cs`

```csharp
public sealed record GeometryMetrics(
    int TotalFormCount,
    IReadOnlyList<FormMetrics> Forms,
    int SymbolicCurveCount = 0,
    int DetailCurveCount = 0,
    int ModelCurveCount = 0,
    int TextNoteCount = 0,
    int ReferencePlaneCount = 0,
    int DimensionCount = 0,
    double TotalSymbolicCurveLength = 0,
    double TotalDetailCurveLength = 0,
    double TotalModelCurveLength = 0);

public sealed record FormMetrics(
    string FormKind,
    bool IsSolid,
    double Volume,
    int FaceCount,
    int EdgeCount,
    string? SubcategoryName,
    double SurfaceArea = 0,
    BoundingBoxSnapshot? Bounds = null);

public sealed record BoundingBoxSnapshot(
    double MinX, double MinY, double MinZ,
    double MaxX, double MaxY, double MaxZ);
```

- `TotalFormCount` — number of `GenericForm` elements (extrusions, sweeps, revolutions, blends, free-form).
- `Forms` — per-form metrics sorted by `(FormKind, IsSolid, Volume)` for deterministic output.
- `Volume` — total volume of all solids in Revit internal units (cubic feet), 6-decimal precision so a 1 mm change shifts the value.
- `FaceCount` / `EdgeCount` — totals across all solids, or 0 if geometry could not be extracted (known bug for shared nested families).
- `SurfaceArea` — summed face area (square feet), catches shape edits that preserve volume and face count (ADR-056).
- `Bounds` — view-independent bounding box, catches translations/proportion edits that preserve volume (ADR-056); hashed with 1e-4 ft rounding.
- `FormKind` — `"Extrusion"`, `"Sweep"`, `"Revolution"`, `"Blend"`, `"SweptBlend"`, or `"GenericForm"` for free-form.
- `TotalSymbolicCurveLength` / `TotalDetailCurveLength` / `TotalModelCurveLength` — summed 2D curve lengths (feet), catch redraws that keep element counts constant (ADR-056).

---

## ConnectorSnapshot (ADR-056)

Snapshot of a single `ConnectorElement` inside a family document — domain, profile, sizes, system classification, origin and intra-family linkage. Connectors carry MEP identity that parameters do not (changing a connector's system classification from ХВС to ГВС leaves every parameter untouched). All enum values are raw ordinals (I-09, same rule as ADR-055 facts). Part of the content hash since FHV3 (Issue #159).

**Файл:** `Models/FamilyManager/ConnectorSnapshot.cs`

```csharp
public sealed record ConnectorSnapshot(
    int Domain,
    int Shape,
    int SystemClassification,
    bool IsPrimary,
    double? Width,
    double? Height,
    double? Radius,
    double OriginX,
    double OriginY,
    double OriginZ,
    int LinkedIndex);
```

- `Domain` / `Shape` / `SystemClassification` — ordinals of `Domain`, `ConnectorProfileType`, `MEPSystemClassification`.
- `Width` / `Height` / `Radius` — connector sizes in feet; `null` when not applicable to the profile (e.g. radius on rectangular).
- `OriginX/Y/Z` — family-local origin in feet; hashed with 1e-4 ft rounding to absorb regen noise.
- `LinkedIndex` — index of the linked connector in the same sorted list (`-1` when unlinked) — captures intra-family connection topology.

---

## FamilyBehaviorFlags (ADR-056)

Behavior flags of a loadable family, read from built-in parameters on the `Family` element — invisible to `FamilyManager.GetParameters()`. `null` per flag = parameter absent in this Revit version/template. Part of the content hash since FHV3 (Issue #159).

**Файл:** `Models/FamilyManager/FamilyBehaviorFlags.cs`

```csharp
public sealed record FamilyBehaviorFlags(
    bool? IsShared,
    bool? IsWorkPlaneBased,
    bool? IsAlwaysVertical,
    bool? AllowsCutWithVoids);
```

---

## CompoundStructureSnapshot / CompoundLayerSnapshot (ADR-056)

Layer stack of a system host type (Basic walls, floors, roofs, ceilings). Not a parameter — changing a layer material/thickness leaves every type parameter untouched. `null` on the type = no compound structure (stacked/curtain walls, non-host categories). Layer order is content (never sorted). Part of the content hash since FHV3 (Issue #159).

**Файл:** `Models/FamilyManager/CompoundStructureSnapshot.cs`

```csharp
public sealed record CompoundStructureSnapshot(
    int ExteriorShellLayerCount,
    int InteriorShellLayerCount,
    IReadOnlyList<CompoundLayerSnapshot> Layers);

public sealed record CompoundLayerSnapshot(
    int Function,
    double Width,
    string? MaterialName,
    bool IsVariable);
```

- `Function` — `MaterialFunctionAssignment` ordinal.
- `MaterialName` — resolved material name (`null` = "By Category"); names are document content, not UI-localized.

---

## RoutingPreferencesSnapshot / RoutingRuleSnapshot / RoutingCriterionSnapshot (ADR-056)

Routing preferences of a MEP curve type (PipeType, DuctType, CableTrayType, ConduitType — all inherit `MEPCurveType.RoutingPreferenceManager`). Not parameters — editing routing rules leaves every type parameter untouched. `null` on the type = not a MEP curve type. Rule order is content (first matching rule wins — never sorted). Part of the content hash since FHV3 (Issue #159).

**Файл:** `Models/FamilyManager/RoutingPreferencesSnapshot.cs`

```csharp
public sealed record RoutingPreferencesSnapshot(
    int PreferredJunctionType,
    IReadOnlyList<RoutingRuleSnapshot> Rules);

public sealed record RoutingRuleSnapshot(
    int GroupType,
    string? PartName,
    string Description,
    IReadOnlyList<RoutingCriterionSnapshot> Criteria);

public sealed record RoutingCriterionSnapshot(
    string CriterionType,
    double MinimumSize,
    double MaximumSize);
```

- `PartName` — resolved part name: `"{Family}:{Type}"` for fitting symbols, element name for segments; `null` when the rule references `InvalidElementId` ("no part allowed" — real content).

---

## MeshData

Tessellated mesh extracted from a single Revit `Solid` or `GeometryInstance`. Pure C# value-type structure — NO Revit API references (I-09). The Revit extractor immediately serializes into this shape so no `GeometryObject` is held between calls (I-05).

**Файл:** `Models/FamilyManager/MeshData.cs`

```csharp
public sealed record MeshData(
    float[] Positions,
    float[]? Normals,
    int[] Indices,
    Vector4 DiffuseColor,
    string NodeName)
{
    public int VertexCount => Positions.Length / 3;
    public int TriangleCount => Indices.Length / 3;
    public bool IsEmpty => Positions.Length < 3 || Indices.Length < 3;
}
```

**Поля:**
- `Positions` — vertex positions as a flat float array layout `[x0, y0, z0, x1, y1, z1, …]`. Length is always a multiple of 3.
- `Normals` — optional vertex normals in the same layout as `Positions`. `null` when the source Revit `Mesh` did not produce normals (the GLB writer emits positions-only, viewer computes flat normals).
- `Indices` — triangle indices into `Positions`. Length is always a multiple of 3 (counter-clockwise winding in Revit's right-handed coordinate system).
- `DiffuseColor` — `Vector4` RGBA diffuse color in 0..1 range. Resolved from the element's `Category.Material.Color` → fallback `Category.LineColor` → fallback gray.
- `NodeName` — human-readable node name used in the glTF scene tree (usually the source `GenericForm` type name + id, e.g. `"Extrusion_12345"`).

---

## FamilyGeometryPreview

Aggregated 3D geometry of a single family version — the result of `IFamilyGeometryExtractor.ExtractAsync`. Pure C# value (I-09): no Revit `GeometryObject` retained (I-05). Passed to `IGlbWriter` which serializes it to a GLB file.

**Файл:** `Models/FamilyManager/FamilyGeometryPreview.cs`

```csharp
public sealed record FamilyGeometryPreview(
    string CatalogItemId,
    string VersionLabel,
    string FamilyName,
    IReadOnlyList<MeshData> Meshes)
{
    public int TotalTriangleCount => Meshes.Sum(m => m.TriangleCount);
    public int TotalVertexCount => Meshes.Sum(m => m.VertexCount);
    public bool IsEmpty => Meshes.Count == 0 || Meshes.All(m => m.IsEmpty);
}
```

**Поля:**
- `CatalogItemId` — catalog item identifier (matches `catalog_items.id`).
- `VersionLabel` — version label this geometry belongs to (matches `catalog_versions.version_label`). Used as `family_assets.version_label` when the GLB is registered as an auto-extracted asset (ADR-042).
- `FamilyName` — family display name (without `.rfa` extension), used as `family_assets.file_name` for the GLB.
- `Meshes` — all non-empty `MeshData` entries extracted from the family document. May be empty when the family has no visible geometry (the pipeline skips writing).

---

## FamilyGeometryPerType

3D geometry for a single family type — the unit produced by `IFamilySnapshotExtractor.ExtractGeometryPerType` and consumed by `IFamilyGeometryPipeline`. One `FamilyGeometryPerType` entry is generated per `FamilyType` that produces non-empty geometry. Families with no types produce a single entry with `TypeName = ""`.

**Файл:** `Models/FamilyManager/FamilyGeometryPerType.cs`

```csharp
public sealed record FamilyGeometryPerType(
    string TypeName,
    string FamilyName,
    IReadOnlyList<MeshData> Meshes)
{
    public int TotalTriangleCount => Meshes.Sum(m => m.TriangleCount);
    public int TotalVertexCount => Meshes.Sum(m => m.VertexCount);
    public bool IsEmpty => Meshes.Count == 0 || Meshes.All(m => m.IsEmpty);
}
```

**Поля:**
- `TypeName` — `FamilyType.Name` from `FamilyManager`. Empty string for families with no types.
- `FamilyName` — family display name (without `.rfa` extension).
- `Meshes` — all non-empty `MeshData` entries extracted for this type. May be empty (the pipeline skips writing a GLB for empty types).
