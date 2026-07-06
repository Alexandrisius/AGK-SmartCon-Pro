# ADR-042: FamilyManager 3D Geometry Preview

- **Status:** accepted
- **Date:** 2026-07-01
- **Issue:** [#92](https://github.com/Alexandrisius/AGK-SmartCon-Pro/issues/92)
- **Branch:** `feature/familymanager-3d-preview`
- **Supersedes:** —
- **Related:** ADR-015 (Published Storage), ADR-016 (ReadOnly managed files), ADR-040 (OverwriteCurrent), ADR-041 (per-version model + SetActiveVersion), #99, #102, #105, #107, #108

## Context

FamilyManager уже имеет подготовленную (но пустую) вкладку «3D Просмотр» в окне свойств — `FamilyPropertiesView.xaml:529-559`. Локализационные ключи прямо называют GLB (`FM_Props_3DComingSoon` = «3D-просмотр модели (GLB) — в разработке»). Также заложены: enum `FamilyAssetType.Model3D`, `StoragePathResolver` маппинг на `assets/models/`, таблица `family_assets` с per-version `version_label`, рабочий `IFamilyAssetService`, OpenFile-диалог с фильтром `*.glb;*.gltf;*.fbx;*.obj;*.stl`.

Нужно реализовать end-to-end: извлечение геометрии из `.rfa` при импорте → конвертация в GLB → хранение per-version → интерактивный 3D-вьювер во вкладке Properties.

На момент принятия решения в репозитории отсутствуют любые NuGet-зависимости для 3D-графики (SharpGLTF, HelixToolkit, SharpDX) — это greenfield с точки зрения пакетов.

## Decision

### Формат хранения: GLB (binary glTF 2.0)

GLB — бинарный контейнер glTF 2.0 стандарта Khronos Group. Преимущества перед `.gltf+.bin`:
- один файл вместо трёх (`.gltf` + `.bin` + textures), проще управлять как asset в managed storage
- быстрее чтение (нет JSON-парсинга для буферов)
- компактнее для binary-данных семейств Revit (vertices + indices + normals как float32/int32)

Локализация уже упоминает GLB в `FM_Props_3DComingSoon`, OpenFile-диалог уже включает `*.glb` — формат соответствует UI-ожиданиям.

### GLB writer: SharpGLTF.Core 1.0.3

- Лицензия: MIT
- TFM: `netstandard2.0` → работает на both `net48` (Revit 2019-2024) и `net8.0-windows` (Revit 2025+)
- API: low-level Schema2 API — `ModelRoot.CreateModel` → `UseBufferView` → `CreateAccessor` → `SetVertexData`/`SetIndexData` → `CreateMesh` → `CreatePrimitive` → `SetVertexAccessor`/`SetIndexAccessor` → `SaveGLB`
- **Почему не SharpGLTF.Toolkit:** Toolkit (даже v1.0.4) транзитивно требует `System.Text.Json >= 9.0.4`, а обновление JSON до 9.x/10.x ломает net48 (`CS1739 AppendFormatted` — меняется сигнатура `DefaultInterpolatedStringHandler`). `SharpGLTF.Core 1.0.3` зависит от `System.Text.Json 8.0.5`, совместимого с замороженным `8.0.6` во всём проекте (see `Directory.Packages.props`).
- Используется реально open-source Revit-экспортёрами: `RevitGltfExporter`, `weiyu666/RevitExportObjAndGltf`, `NovaShang/BimDown`

### GLB reader (для viewer): HelixToolkit.SharpDX.Assimp.Importer

Реализация использует официальнуюproduction-библиотеку `HelixToolkit.SharpDX.Assimp 3.1.2` (обёртка над native `Assimp` через `SharpAssimp 6.0.6+`). Это решение взято после Exa-исследования паттерна из official `FileLoadDemo`:
- `Importer.Load(glbPath)` возвращает `HelixToolkitScene` с готовым `Root` (SceneNode), который напрямую вставляется в `SceneNodeGroupModel3D.AddNode(sceneRoot)`
- На pipeline'е не нужен custom converter SharpGLTF→HelixToolkit (предыдущая попытка упёрлась в cross-namespace type mismatch: `HelixToolkit.Geometry.MeshGeometry3D` vs `HelixToolkit.SharpDX.Geometry3D` — v3 AsharpDX и Geometry assemblies развели типы)
- Native Assimp DLL (win-x64/win-x86) уже входят в NuGet пакет `HelixToolkit.SharpDX.Assimp` через `runtimes/` — ручная работа с native dependencies не нужна
- Namespace в v3: `HelixToolkit.SharpDX.Assimp` (NOT `HelixToolkit.Wpf.SharpDX.Assimp` — этот namespace был в v2 и удалён в v3 при merge `Source/HelixToolkit.Wpf.SharpDX.Assimp/` → `Source/HelixToolkit.SharpDX.Assimp/`)

**Реализовано:** `GlbSceneLoader.cs` — статический класс, один метод `LoadScene(glbPath): SceneNode?`. Offloadable через `Task.Run` для ThreadPool парсинга, возвращает `null` + `Warn` log при ошибке.

### 3D viewer: HelixToolkit.Wpf.SharpDX 3.1.2

- Лицензия: MIT
- TFMs: `net48` + `net8.0-windows7.0` — точно под наш multi-version setup
- DirectX 11, MVVM/XAML-нативный через `<hx:Viewport3DX>`
- Поддержка PBR и Phong materials, ViewCube, FPS overlay
- Precedents интеграции в Revit-плагины: `ricaun-io/ricaun.HelixToolkit.Wpf.Revit`, `ViewSuSu/Su.Revit.HelixToolkit.SharpDX`
- Заменяет плейсхолдер в `FamilyPropertiesView.xaml:529-559` через `mc:AlternateContent` (net8.0 branch с hx:Viewport3DX; net48 fallback с placeholder message)
- `GroupModel3D` (with `ItemsSource` dependency property) как child of Viewport3DX — pattern из helix-toolkit issue #1590: прямой `Viewport3DX.ItemsSource` не существует
- Привязка: `Items3D` (ObservableElement3DCollection в VM) содержит lights + Scene3DRoot (SceneNodeGroupModel3D), который через `Scene3DRoot.AddNode(sceneRoot)` принимает loaded GLB

### Viewer lifecycle (Issue #107 follow-up)

`Viewport3DX` находится внутри `TabItem`. WPF `TabControl` не делает layout неактивных вкладок → `ActualWidth=0` → DirectX render host не стартует, если инициализировать на `Window.Loaded`. Поэтому:
- `Initialize3DInfrastructure()` вызывается из `Viewport3DX.Loaded` (срабатывает при первом открытии вкладки «3D Просмотр»), а не из `Window.Loaded`.
- `Scene3DRoot` добавляется в `Viewport3DX.Items` только когда `ActualWidth > 0`.
- `Dispose3DResources()` вызывается из `Window.Closed`.
- See `FamilyPropertiesView.xaml.cs:OnViewport3DXLoaded`.

### Camera behaviour (Issue #107)

- Первичная загрузка семейства / смена активной версии → `FitCameraToScene()` по bounding box модели.
- Смена типоразмера в ComboBox → камера сохраняется (`_savedCameraPosition` / `_savedCameraLookDirection` / `_savedCameraUpDirection`), `_isFirst3DLoad` не сбрасывается.
- Кнопка «Показать всё» → `ResetCamera3DCommand` вызывает `FitCameraToScene()`, а не нативную `hx:ViewportCommands.ZoomExtents` (последняя игнорировала custom `FarPlaneDistance` и приближала слишком близко).

### Multi-version: условный XAML через `mc:AlternateContent`

HelixToolkit types НЕ существуют на net48 (PackageReference с Condition в csproj только для net8.0-windows). Для корректной мульти-TFM компиляции XAML использован `mc:Ignorable="hx"` + `mc:AlternateContent`:
- `<mc:Choice Requires="hx">` — XAML внутри компилируется только в BAML для net8.0-windows (где `xmlns:hx="http://helix-toolkit.org/wpf/SharpDX"` разрешается в HelixToolkit assembly через `XmlnsDefinition`)
- `<mc:Fallback>` — используется на net48: показывает локализованное сообщение «3D-просмотр недоступен на Revit 2019-2024» + список загруженных Model3D ассетов с кнопкой «Открыть»

Это стандартный WPF markup-compatibility pattern от Microsoft для multi-targeting XAML — описан в `MSDN: Markup Compatibility (mc:) Language Features`.

### Извлечение геометрии: `element.get_Geometry(Options)` + `Solid.Faces` → `Face.Triangulate(1.0)`

Прямой путь extraction для каждого family type (не `CustomExporter`):
- `RevitFamilySnapshotExtractor` открывает family document, переключает `FamilyManager.CurrentType` по очереди на каждый тип и вызывает `ExtractMeshesFromFamilyDoc` внутри `Transaction` + `RollBack()` (так же, как `RevitFamilyDataExtractionService`).
- `FilteredElementCollector(familyDoc).OfClass(typeof(GenericForm))` — обходит все form-элементы family document.
- `FilteredElementCollector(familyDoc).OfClass(typeof(FamilyInstance))` — обходит nested family instances (дверные ручки, арматура коллекторов и т.д.).
- `form.get_Geometry(new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine, IncludeNonVisibleObjects = true })` → возвращает `GeometryElement`.
- `IncludeNonVisibleObjects = true` нужен для восстановления conditionally-visible solids (commit `5283765`), но вместе с тем обходит detail-level фильтрацию Revit — поэтому перед `get_Geometry` добавлена ручная фильтрация видимости (Issue #102, #105).

#### Visibility pre-filter (Issues #102, #105)

Перед вызовом `get_Geometry` каждый элемент проверяется:
1. `GenericForm.Visible == false` → skip.
2. `IS_VISIBLE_PARAM` (`BuiltInParameter.IS_VISIBLE_PARAM`) == `0` → skip (type-driven visibility off).
3. `IsShownAtDetailLevel(element, ViewDetailLevel.Fine)`:
   - `GenericForm` → `form.GetVisibility().IsShownInFine`.
   - `FamilyInstance` / прочие → `GEOM_VISIBILITY_PARAM` bitfield:
     - `Coarse = 1 << 13` (8192)
     - `Medium = 1 << 14` (16384)
     - `Fine = 1 << 15` (32768)
     - значение `0` означает detail component family → unconditional visibility (fail-open: считаем visible).
   - Если API бросает исключение — fail-open, элемент не выбрасывается.

#### Triangulation (Issue #99)

- Не `SolidUtils.TessellateSolidOrShell(LevelOfDetail = 1.0)` — для мелких цилиндров (16 мм отвод) Revit даёт ~412 треугольников, видны полигоны.
- Вместо этого каждая `Face` триангулируется отдельно через `Face.Triangulate(1.0)` → даёт плотную равномерную сетку (для того же отвода ~1747 треугольников) независимо от размера solid.

#### Per-face material extraction (Issue #108)

- `MeshData` имеет один `DiffuseColor` на весь mesh.
- Поэтому faces группируются по `Face.MaterialElementId`: каждая группа становится отдельным `MeshData` со своим цветом.
- Vertex dedup выполняется внутри одной `Face` (fresh dictionary на вызов), что сохраняет острые рёбра между faces, но даёт сглаживание внутри face.
- Нормали накапливаются per-triangle и нормализуются per material group (`AccumulateTriangleNormal` + `NormalizeVertexNormals`).

#### Nested families

- Для `GeometryInstance` вызывается `GetInstanceGeometry()` (НЕ `GetSymbolGeometry()`) — применяет instance transform и ставит nested geometry на правильное место в parent family.
- `GetSymbolGeometry()` возвращала бы geometry в локальных координатах symbol → nested families наложились бы на origin parent.
- Рекурсия через `CollectMeshWithMaterials` обходит `Solid`, `GeometryInstance` и `Mesh` (direct mesh, imported SAT/Rhino-style geometry).

#### Material color resolution (Issue #108)

`GetColorForMaterialId` разрешает `Face.MaterialElementId` в `DiffuseColor` по цепочке fallback:
1. `doc.GetElement(materialId) as Material` → `Material.Color`.
2. `element.Category.Material` (By Category).
3. Для `FamilyInstance`: `inst.Symbol.Family.Category.Material`.
4. `doc.OwnerFamily.Category.Material`.
5. `FallbackColor` (neutral gray 0.65).

`Material.Color` может быть invalid для материалов из Rhino/SAT — тогда fallback на следующий уровень.

#### Voids, ComputeReferences

- `GenericForm.IsSolid == false` (void forms) — skip: solid forms уже содержат применённые вырезания.
- `ComputeReferences = false` — экономит performance, stable references не нужны.

### Storage: reuse `family_assets` через `FamilyAssetType.Model3D` (0 миграций)

SQLite.org official guidance ([Internal Versus External BLOB](https://www.sqlite.org/intern-v-extern-blob.html)): для binary assets >100KB filesystem быстрее БД на чтение. GLB-файлы семейств Revit типично 50KB-1MB+ — filesystem storage предпочтительнее.

Это совпадает с текущей архитектурой: `family_assets.relative_path` хранит путь к файлу на диске (не BLOB). Поэтому:
- **0 миграций схемы** — не добавляется никаких новых таблиц/колонок
- GLB записывается в `{versionDir}/assets/models/{guid}.glb` через существующий `IFamilyAssetService.AddAssetAsync(catalogItemId, versionLabel, Model3D, sourceFilePath, description, ct)` (он делает `File.Copy` в managed storage)
- `Description` с префиксом `"auto-extracted-preview:"` отличает автоматически извлечённое превью от пользовательских Model3D-ассетов
  - UI-фильтр: если `Description` начинается с этого префикса → показывается в 3D-viewer; иначе → в обычном списке Model3D-ассетов

Преимущества решения `family_assets` + filesystem:
- cleanup при `DeleteVersion` уже работает: `DELETE FROM family_assets WHERE (catalog_item_id, version_label)` + recursive `Directory.Delete({versionDir})` (см. `LocalCatalogProvider.Versions.cs:256-263, 343-371`). Не нужно писать отдельной cleanup-логики
- `IFamilyAssetService`/`StoragePathResolver`/`Model3DAssets` VM binding — все уже готовы

### Re-extraction при `OverwriteCurrent` (ADR-040)

Перед `AddAssetAsync` для нового GLB: DELETE предыдущего auto-extracted asset через SQL: `DELETE FROM family_assets WHERE catalog_item_id = @itemId AND version_label = @label AND asset_type = 'Model3D' AND description LIKE 'auto-extracted-preview:%'`. Физический файл удаляется `Directory.Delete` версии → рекурсивно — но OverwriteCurrent перезаписывает файл, а не удаляет директорию, поэтому добавляем ручной delete old GLB + новый asset INSERT.

### Hook-точки импорта: 3 места в `LocalFamilyImportService`

Все 12 user-facing путей импорта концентрируются в одной реализации `IFamilyImportService`. Достаточно 3 хуков:

| Hook | Файл:строка | Метод | Покрывает |
|---|---|---|---|
| **H1** | `LocalFamilyImportService.cs:228` (после `tx.Commit()`) | `ImportFileAsync` — после `InsertVersionAsync` | Все **New** (Import File/Category, Import Active Family/Project, Import Selected, ImportFolder) |
| **H2** | `LocalFamilyImportService.cs:727` (после `tx.Commit()`) | `UpdateFamilyAsync` — после `InsertVersionAsync` | Все **IncrementVersion** |
| **H3** | `LocalFamilyImportService.Database.cs:451` (после `tx.Commit()`) | `OverwriteCurrentAsync` — после `UpdateVersionAsync` | Все **OverwriteCurrent** (ADR-040) |

В каждой точке: вызывается `IFamilyGeometryPipeline.RunAsync(managedRfaPath, catalogItemId, versionId, versionLabel, ct)`. Failures → `SmartConLogger.Warn` с `[Action: ...]` (L9), НЕ прерывают импорт (геометрия — nice-to-have, not critical operation).

### Rollback (ADR-041 SetActiveVersion): VM-reload assets

`SetActiveVersionAsync` переключает `catalog_items.current_version_label` без создания нового контента. Геометрия уже хранится per-version на диске — нужно только перезагрузить `Model3DAssets` в VM.

Текущая проблема: `FamilyPropertiesViewModel.Versions.cs:169` после `MakeActiveAsync` вызывает `LoadAttributesDataAsync`, но НЕ `LoadAssetsAsync`. Фикс — добавить `await LoadAssetsAsync(ct)` в строки 175+ (after LoadAttributesDataAsync catch block).

### Threading: `IFamilyManagerAwaitableEvent.RaiseAsync<T>()` (I-01)

Extract требует `OpenDocumentFile` (Revit API). Все вызовы — только через `IFamilyManagerAwaitableEvent.RaiseAsync`. WPF-поток НЕ трогает Revit API напрямую.

## Архитектура (слои)

```
SmartCon.Core (pure C#, no Revit, no WPF — I-09)
  ├── Models/FamilyManager/MeshData.cs               ← record: float[] Positions, float[] Normals, int[] Indices, Color4 DiffuseColor, string NodeName
  ├── Models/FamilyManager/FamilyGeometryPreview.cs  ← record: Guid CatalogItemId, string VersionLabel, IReadOnlyList<MeshData> Meshes, string FamilyName
  ├── Models/FamilyManager/FamilyGeometryPerType.cs  ← record: string TypeName, IReadOnlyList<MeshData> Meshes
  ├── Services/Interfaces/IFamilyGeometryExtractor.cs
  └── Services/Interfaces/IGlbWriter.cs

SmartCon.Revit (Revit API impl)
  └── FamilyManager/RevitFamilyGeometryExtractor.cs  ← pattern из RevitFamilyDataExtractionService.cs + Issue #99/#102/#105/#108 fixes

SmartCon.FamilyManager (UI + storage + GLB writer)
  ├── Services/Geometry/IFamilyGeometryPipeline.cs   ← interface
  ├── Services/Geometry/FamilyGeometryGlbWriter.cs   ← SharpGLTF.Core Schema2 low-level API
  ├── Services/Geometry/FamilyGeometryPipeline.cs    ← extract → write → register asset (auto-extracted-preview prefix)
  ├── Services/Geometry/GlbSceneLoader.cs            ← HelixToolkit.SharpDX.Assimp.Importer.Load → SceneNode
  ├── ViewModels/FamilyPropertiesViewModel.Preview3D.cs  ← EffectsManager, Camera3D, Scene3DRoot (SceneNodeGroupModel3D), type ComboBox, camera preserve/reset
  └── Views/FamilyPropertiesView.xaml (mc:AlternateContent: Choice=hx:Viewport3DX, Fallback=net48 placeholder)
```

## Инварианты (соблюдены)

- **I-01** — все вызовы Revit API только через `IFamilyManagerAwaitableEvent.RaiseAsync<T>()`
- **I-02** — геометрия извлекается в decimal feet (internal units); GLB хранится как float XYZ (форма, не координаты)
- **I-05** — `GeometryObject`/`Solid`/`Mesh` не хранятся между вызовами; сразу сериализуются в `MeshData` value-type结构
- **I-09** — `SmartCon.Core` не вызывает Revit API; `MeshData`/`FamilyGeometryPreview` — pure C# records без `using Autodesk.Revit.DB`. Допускаются только `using System.Numerics` для `Vector3`/`Vector4`
- **I-10** — MVVM строго: `FamilyPropertiesView.xaml.cs` только `DataContext = viewModel` + `Loaded`/`Closed` event handlers для lifecycle управления DirectX device (инициализация `Initialize3DInfrastructure` на Loaded, `Dispose` на Closed). Это допустимое расширение I-10 — нет бизнес-логики, только resource lifecycle. Вся Viewer-логика (load GLB, wireframe toggle, camera reset) в `FamilyPropertiesViewModel.Preview3D.cs` partial
- **I-15** — `FamilyManagerPaneProvider` singleton; Viewport3DX не пересоздаётся при show/hide (создаётся в окне Properties, не в dockable pane)
- **I-16** — `.glb` в managed storage: `IFamilyAssetService.AddAssetAsync` копирует файл в managed storage (read-only managed хранилище). `.rfa` остаётся readonly, `.glb` — новосозданный файл, его readonly-флаг не критичен (но рекомендуется для консистентности — `LocalFamilyAssetService.AddAssetAsync` уже делает `File.Copy` без явного ReadOnly; остаётся как есть)

## Логирование

Категория: `Geo3DExtract` (новая). Свойства scope: `CatalogItemId`, `VersionLabel`, `FilePath` (всегда `Path.GetFileName()`, не full path — L8).

```csharp
using var _scope = SmartConLogger.BeginScope("Geo3DExtract",
    ("Method", nameof(RunAsync)),
    ("CatalogItemId", catalogItemId),
    ("VersionLabel", versionLabel),
    ("FilePath", Path.GetFileName(managedRfaPath)));
```

`Warn` всегда заканчивается `[Action: ...]` (L9):
```csharp
SmartConLogger.Warn($"Geometry extraction failed: {ex.Message} [Action: check family .rfa geometry; import continues but 3D preview will be unavailable]");
```

## Риски и митигации

| Риск | Митигация |
|---|---|
| `Application.Current == null` в net48 → Helix binding issues (skill `revit-wpf-compat`) | `_uiDispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher` в ctor VM |
| Viewport3DX re-host ломает render (helix-toolkit issue #1120) | singleton dockable panel (I-15) — только hide/show; fallback `EnableSwapChainRendering=true` |
| SharpDX native dlls в multi-version build | NuGet разруливает native deps по RID; проверить на R19/R21/R24/R25 |
| Geometry extraction ~200мс-1сек на файл | Вызывается ПОСЛЕ `tx.Commit()` → импорт не замедляется БД-транзакцией; failures не прерывают; для batch-импорта >5 файлов — последовательные вызовы (I-01 запрещает параллельные Revit API) |
| Per-face triangulation увеличивает размер GLB | Issue #99: ~×2-8 больше треугольников для мелких цилиндров; типичный GLB вырастает с ~50KB до ~100-300KB, что приемлемо для preview |
| Shared nested families без собственного solid | Рекурсия через `GetInstanceGeometry()` в `CollectMeshWithMaterials` |
| Empty geometry (некоторые семьи) | Pipeline возвращает `null` → `if (preview.Meshes.Count == 0) return;` + Warn `[Action: ...]`, не создавать asset |

## Out-of-scope (фон для будущих фаз)

- PBR materials с metallic/roughness textures — base color (`Material.Color`) и per-face material extraction реализованы (Issue #108); metallic/roughness maps — Phase 2
- Appearance Asset Color (`generic_diffuse`) — Phase 2. `Material.Color` покрывает большинство семей; для материалов imported from Rhino/SAT с invalid `Material.Color` нужен `AppearanceAssetElement.GetRenderingAsset()` → `AssetPropertyDoubleArray4d`
- Animation & skinning
- RPC/Plant rendering
- Draco/Meshopt GLB compression
- BLOB-embedding GLB в SQLite (для offline export) — filesystem storage (existing `family_assets.relative_path`) — это best-practice для binary >100KB

## История ключевых баг-фиксов после принятия ADR

| Issue | Что изменилось | Где в коде |
|---|---|---|
| #99 | `SolidUtils.TessellateSolidOrShell` заменён на per-face `Face.Triangulate(1.0)` для равномерной плотности сетки на мелких деталях | `RevitFamilyGeometryExtractor.AddSolidWithMaterials` |
| #102 | Добавлена pre-filter видимости по `GenericForm.GetVisibility()` / `GEOM_VISIBILITY_PARAM`, чтобы coarse-only symbolic graphics не попадали в Fine preview | `RevitFamilyGeometryExtractor.IsShownAtDetailLevel` |
| #105 | Добавлена проверка `IS_VISIBLE_PARAM == 0` перед `get_Geometry` — учитывает type-driven visibility nested family instances | `RevitFamilyGeometryExtractor.GetIsVisibleParam` |
| #107 | Камера больше не сбрасывается при смене типоразмера; кнопка «Показать всё» использует `FitCameraToScene` вместо нативного ZoomExtents | `FamilyPropertiesViewModel.Preview3D.cs` |
| #108 | Один `MeshData` на element заменён на группировку по `Face.MaterialElementId`; nested instances теперь получают реальные материалы через `GetInstanceGeometry()` + fallback chain | `RevitFamilyGeometryExtractor.ExtractMeshesFromElement`, `CollectMeshWithMaterials`, `GetColorForMaterialId` |

## Verification

- Build R25 (net8.0-windows): `dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R25` ✅ 0 errors, 0 warnings
- Build R24 (net48): `dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R24` ✅ 0 errors, 0 warnings
- Build R21 (net48): `dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R21` ✅ 0 errors, 0 warnings
- Build R19 (net48): `dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R19` ✅ 0 errors, 0 warnings
- Tests: `dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25` ✅ 1690/1690 passed (9 FamilyGeometryGlbWriterTests + 5 GlbSceneLoaderTests + 1676 existing) — Issue #108 добавил `WriteAsync_MultipleMeshesDifferentColors_PreservesMaterialColors` + `WriteAsync_FallbackGrayColor_PreservesBaseColor`
- Manual: импорт `.rfa` в Revit 2025 → открыть окно свойств семейства → вкладка «3D Просмотр» → должна появиться интерактивная 3D-модель (вращение правой кнопкой, zoom колесом, pan левой кнопкой). Переключение активной версии в вкладке «Версии» перезагружает превью (через `LoadAssetsAsync` → `Load3DPreviewAsync`). На net48 (Revit 2019-2024) показывается placeholder «3D-просмотр недоступен».
