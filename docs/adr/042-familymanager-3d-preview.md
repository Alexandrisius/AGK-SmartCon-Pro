# ADR-042: FamilyManager 3D Geometry Preview

- **Status:** accepted
- **Date:** 2026-07-01
- **Issue:** [#92](https://github.com/Alexandrisius/AGK-SmartCon-Pro/issues/92)
- **Branch:** `feature/familymanager-3d-preview`
- **Supersedes:** —
- **Related:** ADR-015 (Published Storage), ADR-016 (ReadOnly managed files), ADR-040 (OverwriteCurrent), ADR-041 (per-version model + SetActiveVersion)

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

### GLB writer: SharpGLTF.Toolkit 1.0.4

- Лицензия: MIT
- TFMs: `netstandard2.0` + `net6.0` + `net8.0` → работает на both `net48` (Revit 2019-2024) и `net8.0-windows` (Revit 2025+)
- API: `MeshBuilder<VertexPositionNormal, VertexEmpty, VertexEmpty>` + `UsePrimitive(material).AddTriangle(v1, v2, v3)` → индексы дедуплицируются автоматически через внутренний dictionary; финиш через `var model = sceneBuilder.ToGltf2(); model.SaveGLB(path);`
- Используется реально open-source Revit-экспортёрами: `RevitGltfExporter`, `weiyu666/RevitExportObjAndGltf`, `NovaShang/BimDown`
- **Важно:** v1.0.6 (Dec 2025) требует System.Text.Json >= 10.0.1 — обновление до 10.x ломает net48 (CS1739 AppendFormatted — меняется сигнатура `DefaultInterpolatedStringHandler`). Поэтому v1.0.4 (May 2025), зависящая от System.Text.Json 8.x, совместимой с замороженным 8.0.6 во всём проекте.

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

### Multi-version: условный XAML через `mc:AlternateContent`

HelixToolkit types НЕ существуют на net48 (PackageReference с Condition в csproj только для net8.0-windows). Для корректной мульти-TFM компиляции XAML использован `mc:Ignorable="hx"` + `mc:AlternateContent`:
- `<mc:Choice Requires="hx">` — XAML внутри компилируется только в BAML для net8.0-windows (где `xmlns:hx="http://helix-toolkit.org/wpf/SharpDX"` разрешается в HelixToolkit assembly через `XmlnsDefinition`)
- `<mc:Fallback>` — используется на net48: показывает локализованное сообщение «3D-просмотр недоступен на Revit 2019-2024» + список загруженных Model3D ассетов с кнопкой «Открыть»

Это стандартный WPF markup-compatibility pattern от Microsoft для multi-targeting XAML — описан в `MSDN: Markup Compatibility (mc:) Language Features`.

### Извлечение геометрии: `element.get_Geometry(Options)` + `Solid.Faces` → `Face.Triangulate()`

Прямой путь extraction для одиночного family symbol (не `CustomExporter`):
- `FilteredElementCollector(familyDoc).OfClass(typeof(GenericForm))` — обходит все form-элементы family document (как в существующем `RevitFamilySnapshotExtractor.ExtractGeometry:324-394`)
- `form.get_Geometry(new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine })` → возвращает `GeometryElement`
- Итерация `GeometryElement`: `Solid` → `Solid.Faces` → `Face.Triangulate()` → `Mesh.Vertices`, `Mesh.TriangleIndexToCornersMap`, `Mesh.NormalVectors`
- Для `GeometryInstance`: использовать `GetSymbolGeometry()` (local coords), не `GetInstanceGeometry()` (world coords) — подтверждено Jeremy Tammik Building Coder (a/0278_abg01_geometry_options + a/1355_directshape_face) и Aurora fix в `NovaShang/BimDown` (commit `3d82a68`)
- Рекурсивный обход shared nested families через `FamilyInstance.GetSubComponentIds()` для случаев, когда контейнер-семья не имеет собственного solid (AUTODESK forum совет)
- `ComputeReferences = false` — экономит performance, нам не нужны StableRepresentation для dimensioning

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
  ├── Models/FamilyManager/MeshData.cs               ← record: float[] Positions, float[] Normals, int[] Indices, Color4f DiffuseColor
  ├── Models/FamilyManager/FamilyGeometryPreview.cs  ← record: Guid CatalogItemId, string VersionLabel, IReadOnlyList<MeshData> Meshes, string FamilyName
  ├── Services/Interfaces/IFamilyGeometryExtractor.cs
  └── Services/Interfaces/IGlbWriter.cs

SmartCon.Revit (Revit API impl)
  └── FamilyManager/RevitFamilyGeometryExtractor.cs  ← pattern из RevitFamilyDataExtractionService.cs

SmartCon.FamilyManager (UI + storage + GLB writer)
  ├── Services/Geometry/IFamilyGeometryPipeline.cs   ← interface (extends IGlbWriter + extractor coordination)
  ├── Services/Geometry/FamilyGeometryGlbWriter.cs   ← SharpGLTF.MeshBuilder
  ├── Services/Geometry/FamilyGeometryPipeline.cs    ← extract → write → register asset (auto-extracted-preview prefix)
  ├── Services/Geometry/GlbSceneLoader.cs            ← HelixToolkit.SharpDX.Assimp.Importer.Load → SceneNode
  ├── ViewModels/FamilyPropertiesViewModel.Preview3D.cs  ← EffectsManager, Camera3D, Scene3DRoot (SceneNodeGroupModel3D), Items3D (ObservableElement3DCollection), commands
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
| Shared nested families без собственного solid | Рекурсия через `GetSubComponentIds` |
| Empty geometry (некоторые семьи) | Pipeline возвращает `null` → `if (preview.Meshes.Count == 0) return;` + Warn `[Action: ...]`, не создавать asset |

## Out-of-scope (фон для будущих фаз)

- PBR materials с metallic/roughness textures — per-face material extraction реализован (Issue #108: группировка faces по `Face.MaterialElementId`, один `MeshData` на материал), но PBR textures/metalness/roughness — Phase 2
- Appearance Asset Color (`generic_diffuse`) — Phase 2. `Material.Color` покрывает большинство семей; для материалов imported from Rhino/SAT с invalid `Material.Color` нужен `AppearanceAssetElement.GetRenderingAsset()` → `AssetPropertyDoubleArray4d`
- Animation & skinning
- RPC/Plant rendering
- Draco/Meshopt GLB compression
- BLOB-embedding GLB в SQLite (для offline export) — filesystem storage (existing `family_assets.relative_path`) — это best-practice для binary >100KB

## Verification

- Build R25 (net8.0-windows): `dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R25` ✅ 0 errors, 0 warnings
- Build R24 (net48): `dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R24` ✅ 0 errors, 0 warnings
- Build R21 (net48): `dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R21` ✅ 0 errors, 0 warnings
- Build R19 (net48): `dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R19` ✅ 0 errors, 0 warnings
- Tests: `dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25` ✅ 1690/1690 passed (9 FamilyGeometryGlbWriterTests + 5 GlbSceneLoaderTests + 1676 existing) — Issue #108 добавил `WriteAsync_MultipleMeshesDifferentColors_PreservesMaterialColors` + `WriteAsync_FallbackGrayColor_PreservesBaseColor`
- Manual: импорт `.rfa` в Revit 2025 → открыть окно свойств семейства → вкладка «3D Просмотр» → должна появиться интерактивная 3D-модель (вращение правой кнопкой, zoom колесом, pan левой кнопкой). Переключение активной версии в вкладке «Версии» перезагружает превью (через `LoadAssetsAsync` → `Load3DPreviewAsync`). На net48 (Revit 2019-2024) показывается placeholder «3D-просмотр недоступен».
