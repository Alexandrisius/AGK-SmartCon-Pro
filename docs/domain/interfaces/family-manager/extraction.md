---
module: family-manager-interfaces
---
# Интерфейсы FamilyManager — Снапшоты, хеширование и геометрия

> Часть документации модуля FamilyManager. Индекс и навигация: [README.md](README.md).
> Источник истины: `src/SmartCon.Core/Services/Interfaces/*.cs`.

## IFamilySnapshotExtractor

Extracts structured snapshots from open Revit documents for content-hash computation. All methods must be called on the Revit UI thread (I-01) — the caller is responsible for marshalling via `IFamilyManagerAwaitableEvent.RaiseAsync`.

**Файл:** `Services/Interfaces/IFamilySnapshotExtractor.cs`

```csharp
public interface IFamilySnapshotExtractor
{
    FamilySnapshot ExtractFromFamilyDocument(Document familyDoc);
    SystemFamilySnapshot ExtractFromProject(
        Document projectDoc,
        IReadOnlyList<string> typeUniqueIds,
        BuiltInCategory builtInCategory);
    SystemFamilySnapshot ExtractSystemCategoryFromStagedProject(
        Document stagedDoc,
        BuiltInCategory builtInCategory);
}
```

- `ExtractFromFamilyDocument` — extracts a `FamilySnapshot` (parameters, types, values, geometry, shared nested names) from an open family document. The document must be a family document (`IsFamilyDocument == true`).
- `ExtractFromProject` — extracts a `SystemFamilySnapshot` (category + types + parameter values) from an open project document.
- `ExtractSystemCategoryFromStagedProject` (ADR-056) — extracts a `SystemFamilySnapshot` from a staged mini-project (.rvt) during database actualization. Type discovery: placed instances first (domain truth); when nothing is placed (Phase-2 categories) ALL types of the category are collected — the caller trims them to the catalog's authoritative type list (`family_types`).

**Caller contract:** the active document may be the source project or a managed-storage mini-rvt (after `EditFamily` + `SaveAs`). The extracted hash is stable across both because it is based on in-memory content, not file bytes.

---

## IFamilyContentHasher

Computes a stable `FamilyContentHash` from a snapshot. Pure C# — no Revit API calls. The hash is a SHA-256 of a canonical string built from the snapshot data. Stable across SaveAs, rename, and Revit upgrade because it is based on in-memory content, not file bytes.

**Файл:** `Services/Interfaces/IFamilyContentHasher.cs`

```csharp
public interface IFamilyContentHasher
{
    FamilyContentHash? ComputeForLoadable(FamilySnapshot snapshot);
    FamilyContentHash? ComputeForSystem(SystemFamilySnapshot snapshot);
}
```

- `ComputeForLoadable` — returns `null` if the snapshot is null or empty (no parameters, no types, no geometry). FHV10 (2026-08-12): единственный грейд — используется и для identity (дедуп/версии, хранится в БД), и для embedded-верификации (эфемерно, в сессии); отдельного verification-метода больше нет.
- `ComputeForSystem` — returns `null` if the snapshot is null or has no types.

**v2.0.0 stability rules:**
- Blank parameter values are excluded from the canonical string (`HasValue=false`, empty string, `INVALID`, `UNSUPPORTED`, `READERROR`). Numeric zero is meaningful (e.g. IFC=0).
- The auto-generated `Код IfcGUID` parameter is excluded because Revit regenerates it on every `.rvt` save — including it would make identical content produce different hashes across source project and mini-rvt.
- Parameter values are sorted by parameter name for deterministic output.

---

## IContentHashDedupService

Content-hash dedup service. Combines the name-based lookup with the cross-version hash search to produce the final `FamilyBatchImportStatus` for a batch-import row.

**Файл:** `Services/Interfaces/IContentHashDedupService.cs`

```csharp
public interface IContentHashDedupService
{
    Task<ContentHashDedupResult> CheckAsync(
        string normalizedName,
        FamilyContentHash? contentHash,
        string familySource,
        int? revitCategoryId = null,
        CancellationToken ct = default);
}
```

**Business rules (Issue #126, hash-first):**
- Хэш совпал с любой версией (current или archived) ЛЮБОГО айтема каталога, независимо от имени → `Duplicate`. Найденный по хэшу айтем — канонический «existing» для MakeActive/IncrementVersion. Если его нормализованное имя отличается от имени строки — `IsCrossNameDuplicate = true` (batch-диалог рисует ⚠ с tooltip).
- Хэш не совпал (или хэша нет) и идентичность есть в каталоге → `Existing`.
- Хэш не совпал (или хэша нет) и идентичности нет в каталоге → `New`.
- Конфликт имя/контент (хэш совпал с айтемом A, имя занято другим айтемом B): контент важнее — Duplicate к A, Warn в лог, действие по умолчанию Skip.
- Cross-source separation: `"loadable"` hashes are never compared against `"system"` hashes and vice versa.

**Identity per source (Issue #192):**
- `"loadable"`: идентичность — нормализованное имя .rfa-файла (стабильно, контролируется пользователем). Cross-name семантика в силе.
- `"system"`: идентичность — `BuiltInCategory` ordinal (`revitCategoryId`), НЕ имя категории. Display-имя категории зависит от документа/шаблона/локали («Материалы изоляции воздуховодов» vs «Изоляция воздуховодов» для одного OST_DuctInsulations), а ordinal уже входит в каноническую строку хэша → hash-матч для system **никогда** не является cross-name duplicate; fallback без хэша ищет айтем через `IFamilyCatalogProvider.FindByRevitCategoryIdAsync`, а не по имени. Иначе: ложный ⚠-бейдж при совпадающем контенте и дубли айтемов каталога при изменённом контенте.

---

## IFamilyGeometryExtractor

Extracts tessellated 3D geometry from a managed `.rfa` file (ADR-042). Implementations MUST run on the Revit UI thread (I-01) because `OpenDocumentFile` / `element.get_Geometry(Options)` / `Face.Triangulate()` are all Revit API calls — callers marshal via `IFamilyManagerAwaitableEvent.RaiseAsync<T>`.

**Файл:** `Services/Interfaces/IFamilyGeometryExtractor.cs`
**Реализация:** `SmartCon.Revit/FamilyManager/RevitFamilyGeometryExtractor.cs`

```csharp
public interface IFamilyGeometryExtractor
{
    Task<FamilyGeometryPreview?> ExtractAsync(
        string managedRfaPath,
        string catalogItemId,
        string versionLabel,
        CancellationToken ct = default);
}
```

**Контракт:**
- Returns `FamilyGeometryPreview` with at least one mesh, or `null` when the family has no visible geometry / an error occurred (logged by the implementation, NOT rethrown — the pipeline treats `null` as "skip GLB write").

---

## IGlbWriter

Serializes a `FamilyGeometryPreview` to a GLB (binary glTF 2.0) file. Pure C# implementation (SharpGLTF.Toolkit) — no Revit API, no WPF (I-09), unit-testable without a Revit process.

**Файл:** `Services/Interfaces/IGlbWriter.cs`
**Реализация:** `SmartCon.FamilyManager/Services/Geometry/FamilyGeometryGlbWriter.cs`

```csharp
public interface IGlbWriter
{
    Task<bool> WriteAsync(
        FamilyGeometryPreview preview,
        string outputPath,
        CancellationToken ct = default);
}
```

**Контракт:**
- Creates the parent directory if it does not exist. Overwrites the file if it already exists.
- Returns `true` on success; `false` on failure (logged internally, not rethrown — pipeline treats `false` as "skip asset registration").

---

## IFamilyGeometryPipeline

Coordinates the end-to-end 3D geometry preview pipeline triggered from `LocalFamilyImportService` hooks H1/H2/H3 (ADR-042): extract → write GLB → delete previous auto-extracted asset → register new asset.

**Файл:** `Services/Interfaces/IFamilyGeometryPipeline.cs`
**Реализация:** `SmartCon.FamilyManager/Services/Geometry/FamilyGeometryPipeline.cs`

```csharp
public interface IFamilyGeometryPipeline
{
    Task RunAsync(
        string managedRfaPath,
        string catalogItemId,
        string versionId,
        string versionLabel,
        string familyName,
        CancellationToken ct = default);
}
```

**Контракт:**
- Safe to invoke from any thread — internally marshals Revit API calls to the UI thread via `IFamilyManagerAwaitableEvent`.
- Implementations MUST swallow all exceptions and log a Warn with an `[Action: ...]` suggestion (skill smartcon-logging L9) — geometry preview is a nice-to-have and MUST NOT break the import transaction that already committed before the hook was reached.
