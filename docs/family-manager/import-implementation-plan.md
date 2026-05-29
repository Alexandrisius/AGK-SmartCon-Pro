# FamilyManager Import Implementation Plan v4 (Detailed)

> **Статус:** Детализация завершена, готов к реализации  
> **Дата:** 2026-05-29  
> **База:** main @ 550f553 (v1.9.3)  
> **Фокус:** Batch Import без Deep Scan, Stale marker, инкремент/перезапись, обратная совместимость путей

---

## 1. Архитектурные ограничения (из ADR + инвариантов)

**I-01:** Весь Revit API только через `IExternalEventHandler.Execute`. Batch Dialog UI — WPF thread. **OpenDocumentFile + Extract** — ExternalEvent. `BasicFileInfo.Extract` — статический метод, НЕ требует Revit runtime, можно вызывать из любого потока.

**I-09:** Core не вызывает Revit API. `BasicFileInfo.Extract` допустим в Core как чистый парсер файла (читается заголовок .rfa с диска, не требует Revit runtime).

**I-10:** MVVM строго. `.xaml.cs` только `DataContext = viewModel`.

**ADR-015:** `current_version_label` в `catalog_items` — единственный источник текущей версии.

**ADR-017:** `RevitFamilyDataExtractionService` использует `OpenDocumentFile`. **Критично:** добавить `Marshal.ReleaseComObject` после `Close(false)` (память не освобождается при batch >32 файлов).

**ADR-018:** `async void FireAndForget` внутри `ExternalEvent.Raise()`. Не `async Task`.

**ADR-022:** RBAC — `CanImport` проверяется перед открытием Batch Dialog.

---

## 2. Обратная совместимость путей хранения

### Текущая ситуация
- Старые файлы: `files/{catalogItemId}/{versionLabel}/r{revitMajorVersion}/{fileName}.rfa`
- Новые файлы должны быть: `files/{catalogItemId}/{versionLabel}/{fileName}.rfa`

### Решение: двухпутевой резолвер (read-compatible, write-new)

**Write-path (новые файлы):**
- `StoragePathResolver.GetRfaFilePath` — убрать `r{revitMajorVersion}` сегмент
- `StoragePathResolver.EnsureFamilyDirectories` — создавать только `{versionLabel}`, без `r{revit}`
- `StoragePathResolver.GetRevitFileDirectory` — **deprecated**, оставить для чтения старых файлов

**Read-path (обратная совместимость):**
- `LocalFamilyFileResolver.ResolveForLoadAsync` — сначала искать по новому пути, если не найден — fallback на старый путь с `r{revitMajorVersion}`
- SQL запрос JOIN `family_files` → `relative_path` содержит путь; файл может лежать по старому или новому пути
- `File.Exists` проверка делает fallback прозрачным

**Миграция:** не нужна. Старые файлы остаются на месте. При следующем `UpdateFamily` (Increment или Overwrite) файл будет записан по новому пути.

---

## 3. Batch Dialog — архитектура

### Данные ДО диалога (WPF thread, async I/O)

**Быстрый анализ файлов (без Revit API):**
1. `OpenFileDialog.Multiselect = true` → массив путей
2. Для каждого файла:
   - `Sha256FileHasher.ComputeHashAsync` — SHA256
   - `FileInfo.Length` — размер
   - **Revit версия:** `BasicFileInfo.Extract(filePath).Format` — статический метод, не требует ExternalEvent, можно вызывать из WPF thread напрямую. Уже используется в `RevitFileInfoReader` без ExternalEvent.
   
   **Сбор данных:** WPF thread → SHA256 + FileInfo + BasicFileInfo.Extract → Batch Dialog с полными данными.

**Дедупликация (SQLite, WPF thread):**
- SHA256 exact match → `Skip` (авто, не показываем в диалоге или показываем серым)
- `normalized_name` not in DB → `New` (default: Increment)
- `normalized_name` in DB + SHA256 different → `Existing` (default: Increment)

### Модели Core (новые файлы)

**Файлы:**
- `src/SmartCon.Core/Models/FamilyManager/FamilyBatchImportItem.cs` — row data: `FilePath`, `FileName`, `Sha256`, `RevitMajorVersion`, `FileSizeBytes`, `Status`, `ExistingCatalogItemId`, `ExistingVersionLabel`
- `src/SmartCon.Core/Models/FamilyManager/FamilyBatchImportAction.cs` — enum: `IncrementVersion`, `OverwriteCurrent`, `Skip`
- `src/SmartCon.Core/Models/FamilyManager/FamilyBatchImportStatus.cs` — enum: `New`, `Existing`, `Duplicate`, `Error`

### UI (стиль Settings/ShareProject)

**Файлы:**
- `src/SmartCon.FamilyManager/Views/FamilyBatchImportView.xaml` — DialogWindowBase, таблица с колонками: Имя, Версия Revit, Размер, Статус, Действие (ComboBox)
- `src/SmartCon.FamilyManager/ViewModels/FamilyBatchImportViewModel.cs` — команды: `ImportCommand`, `CancelCommand`, `SelectAllCommand`
- `src/SmartCon.FamilyManager/ViewModels/FamilyBatchImportRow.cs` — per-row VM: `Action` (двусторонний биндинг), `AvailableActions` (зависит от статуса)

**Поведение:**
- `Duplicate` → только `Skip`, disabled
- `New` → `IncrementVersion` (default), `Skip`
- `Existing` → `IncrementVersion` (default), `OverwriteCurrent`, `Skip`
- Кнопка "Загрузить" активна если есть хотя бы один элемент не-Skip

### Диалоговый сервис

Добавить в `IFamilyManagerDialogService`:
```csharp
bool? ShowBatchImportDialog(object viewModel);
```

Реализация через `IDialogPresenter` (как остальные MVVM-диалоги).

---

## 4. Pipeline импорта (после нажатия "Загрузить")

### 4.1 ExternalEvent — подготовка данных

```
[Кнопка "Загрузить" в Batch Dialog]
  ↓
[Закрыть Dialog с Result=OK]
  ↓
[FamilyManagerMainViewModel.Import.cs]
  ↓
[ExternalEvent.Raise] — Revit UI thread
```

**Внутри ExternalEvent:**
1. Для каждого файла с действием != Skip:
   - `CopyToManagedStorageAsync` — новый путь (без `r{version}`)
   - SQLite транзакция:
     - `IncrementVersion` → `GetNextVersionLabelAsync` → `InsertVersionAsync` → `UpdateCatalogItemVersionAsync` (обновляет `current_version_label`)
     - `OverwriteCurrent` → `UpdateFamilyFileAsync` (заменяет `family_files` запись для текущей версии) → `current_version_label` НЕ трогаем
   - `_database.Checkpoint()`
   - **Экстракция:** `app.OpenDocumentFile(rfaPath)` → `FamilyManager.Types/Parameters` → `familyDoc.Close(false)` + `Marshal.ReleaseComObject(familyDoc)`
   - `FamilyDataImportService.SaveExtractionResultAsync` — сохраняет типы + атрибуты

2. Обновить дерево: `FireAndForget(() => LoadTreeAsync())`

### 4.2 Критичные изменения в существующих сервисах

**`RevitFamilyDataExtractionService.cs` (строка 87-100):**
- Добавить `Marshal.ReleaseComObject(familyDoc)` после `familyDoc.Close(false)`
- Это предотвращает memory corruption при batch >32 файлов (см. Autodesk forum, Jeremy Tammik)

**`LocalFamilyImportService.cs`:**
- Новый публичный метод: `ImportBatchAsync(FamilyBatchImportRequest request, IProgress<FamilyImportProgress>? progress, CancellationToken ct)`
- `FamilyBatchImportRequest` содержит список `FamilyBatchImportItem` с выбранным `Action`
- `ImportFileAsync` остаётся для backward compat, но внутри вызывает `ImportBatchAsync` с одним элементом

---

## 5. Stale Marker (маркер устаревания)

### Механизм

**При загрузке семейства в проект** (`LoadToProjectCommand` в `FamilyManagerMainViewModel.LoadPlace.cs`):
- Записывать `loaded_version_label` в `project_usage`
- Добавить поле `LoadedVersionLabel` в `ProjectFamilyUsage` (модель Core)
- Миграция БД V9: `ALTER TABLE project_usage ADD COLUMN loaded_version_label TEXT`

**При Refresh / LoadTreeAsync:**
- Для каждого семейства в дереве:
  - Получить `current_version_label` из `catalog_items` (уже есть в `FamilyCatalogItemRow`)
  - Получить последний `loaded_version_label` из `project_usage` для текущего проекта (`project_path = doc.PathName` fingerprint)
  - Если `loaded_version_label != current_version_label` → `IsStale = true`

### Отображение в UI

**`FamilyLeafNodeViewModel.cs`:**
- Добавить свойство `IsStale` (вычисляемое при создании в `BuildCategoryNode`)
- Добавить свойство `LoadedVersionLabel`

**`FamilyManagerPaneControl.xaml` (строка 515-528):**
- `HierarchicalDataTemplate` для `FamilyLeafNodeViewModel`:
  - Если `IsStale` → добавить иконку (например, оранжевый круг/треугольник) или изменить цвет текста на оранжевый
  - Текст: `"{DisplayName} (v{VersionLabel})"` → если stale добавить `" [Устарело]"`

**`FamilyManagerMainViewModel.Tree.cs` (строка 126-170):**
- `BuildCategoryNode` → при создании `FamilyLeafNodeViewModel` заполнять `IsStale`
- Для получения stale-статуса batch-ом: добавить метод `GetLoadedVersionLabelsAsync(List<string> catalogItemIds)` в `IProjectFamilyUsageRepository`

---

## 6. Удаление команд из UI

### Удалить

**`FamilyManagerPaneControl.xaml` (строка 219-226):**
- Удалить кнопку `FM_ImportFolder` из Popup
- Popup остаётся с одной кнопкой `FM_ImportFile` (пока placeholder для будущих команд: системные семейства, активное семейство)

**`FamilyManagerPaneControl.xaml` (строка 438-440):**
- Удалить пункт `FM_ImportData` из `FamilyLeafNodeContextMenu`

**`FamilyManagerMainViewModel.Import.cs`:**
- Удалить метод `ImportFolderAsync()` (команда `ImportFolderCommand`)
- Удалить метод `ImportData()` (команда `ImportDataCommand`)
- Удалить приватные хелперы `ImportFolder`, `ImportFile` (если не используются)

**`FamilyManagerMainViewModel.cs` (строка 73-74):**
- Убрать `NotifyCanExecuteChangedFor` для `ImportFolderCommand` и `ImportDataCommand`

### Оставить

- `ImportFilesCommand` — теперь открывает Batch Dialog
- `ImportFileToCategoryCommand` — остаётся, тоже через Batch Dialog
- `ImportFolderToCategoryCommand` — остаётся в контексте категории (другой сценарий)
- `ImportDataForCategoryCommand` — остаётся, но будет переименован/переделан позже
- `UpdateFamilyCommand` — теперь тоже открывает Batch Dialog (forced Existing mode)

---

## 7. Изменённые файлы (список)

### Core (модели)
| Файл | Изменение |
|------|-----------|
| `src/SmartCon.Core/Models/FamilyManager/FamilyBatchImportItem.cs` | **Новый** — row data для Batch Dialog |
| `src/SmartCon.Core/Models/FamilyManager/FamilyBatchImportAction.cs` | **Новый** — enum: Increment, Overwrite, Skip |
| `src/SmartCon.Core/Models/FamilyManager/FamilyBatchImportStatus.cs` | **Новый** — enum: New, Existing, Duplicate, Error |
| `src/SmartCon.Core/Models/FamilyManager/ProjectFamilyUsage.cs` | Добавить `LoadedVersionLabel` |
| `src/SmartCon.Core/Models/FamilyManager/FamilyImportRequest.cs` | Добавить `BatchAction`? (опционально) |

### Core (интерфейсы)
| Файл | Изменение |
|------|-----------|
| `src/SmartCon.Core/Services/Interfaces/IFamilyImportService.cs` | Добавить `ImportBatchAsync` |
| `src/SmartCon.Core/Services/Interfaces/IFamilyManagerDialogService.cs` | Добавить `ShowBatchImportDialog` |
| `src/SmartCon.Core/Services/Interfaces/IProjectFamilyUsageRepository.cs` | Добавить `GetLoadedVersionLabelsAsync` |

### FamilyManager (сервисы)
| Файл | Изменение |
|------|-----------|
| `src/SmartCon.FamilyManager/Services/LocalCatalog/StoragePathResolver.cs` | Убрать `r{version}` из write-путей |
| `src/SmartCon.FamilyManager/Services/LocalCatalog/LocalFamilyFileResolver.cs` | Fallback на старые пути при чтении |
| `src/SmartCon.FamilyManager/Services/LocalCatalog/LocalFamilyImportService.cs` | Добавить `ImportBatchAsync` с Increment/Overwrite/Skip |
| `src/SmartCon.FamilyManager/Services/LocalCatalog/LocalFamilyImportService.Database.cs` | Добавить `OverwriteCurrentAsync` (UPDATE family_files для текущей версии) |
| `src/SmartCon.FamilyManager/Services/LocalCatalog/LocalProjectFamilyUsageRepository.cs` | Добавить `GetLoadedVersionLabelsAsync`, обновить `RecordUsageAsync` |
| `src/SmartCon.FamilyManager/Services/LocalCatalog/FamilyCatalogSql.cs` | Миграция V9: `project_usage.loaded_version_label` |
| `src/SmartCon.FamilyManager/Services/LocalCatalog/LocalCatalogMigrator.cs` | Добавить V9 миграцию |
| `src/SmartCon.FamilyManager/Services/FamilyManagerDialogService.cs` | Реализовать `ShowBatchImportDialog` |

### FamilyManager (ViewModels)
| Файл | Изменение |
|------|-----------|
| `src/SmartCon.FamilyManager/ViewModels/FamilyBatchImportViewModel.cs` | **Новый** |
| `src/SmartCon.FamilyManager/ViewModels/FamilyBatchImportRow.cs` | **Новый** |
| `src/SmartCon.FamilyManager/ViewModels/FamilyManagerMainViewModel.cs` | Добавить `IRevitFileInfoReader` в конструктор для сбора Revit-версий файлов |
| `src/SmartCon.FamilyManager/ViewModels/FamilyManagerMainViewModel.Import.cs` | Batch Dialog для ImportFiles + UpdateFamily; удалить ImportFolder, ImportData |
| `src/SmartCon.FamilyManager/ViewModels/FamilyManagerMainViewModel.Tree.cs` | Добавить IsStale в FamilyLeafNodeViewModel |
| `src/SmartCon.FamilyManager/ViewModels/FamilyLeafNodeViewModel.cs` | Добавить `IsStale`, `LoadedVersionLabel` |

### FamilyManager (Views)
| Файл | Изменение |
|------|-----------|
| `src/SmartCon.FamilyManager/Views/FamilyBatchImportView.xaml` | **Новый** |
| `src/SmartCon.FamilyManager/Views/FamilyBatchImportView.xaml.cs` | **Новый** (только `DataContext = viewModel`) |
| `src/SmartCon.FamilyManager/Views/FamilyManagerPaneControl.xaml` | Удалить ImportFolder из Popup, удалить ImportData из FamilyLeafNodeContextMenu |

### Revit (экстрактор)
| Файл | Изменение |
|------|-----------|
| `src/SmartCon.Revit/FamilyManager/RevitFamilyDataExtractionService.cs` | Добавить `Marshal.ReleaseComObject(familyDoc)` |

### Локализация
| Файл | Изменение |
|------|-----------|
| `src/SmartCon.Core/Services/LocalizationService.Keys.FamilyManager.cs` | Добавить строки Batch Dialog (~20 ключей) |

### DI
| Файл | Изменение |
|------|-----------|
| `src/SmartCon.App/DI/ServiceRegistrar.cs` | Зарегистрировать BatchImport VM + View |

---

## 8. Миграция БД

### Schema V9

```sql
ALTER TABLE project_usage ADD COLUMN loaded_version_label TEXT;
```

**Мотивация:** stale marker требует знать какая версия была загружена в проект. `version_id` (UUID) нечитаем для сравнения. `version_label` ("v1", "v2") — human-readable и сравнимый.

---

## 9. Дедупликация — точный алгоритм

```
Для каждого выбранного файла:
  1. sha256 = Sha256FileHasher.ComputeHash(filePath)
  2. existingByHash = SELECT cv.* FROM catalog_versions cv
                      INNER JOIN family_files ff ON ff.id = cv.file_id
                      WHERE ff.sha256 = @sha256
                      LIMIT 1
     → Если найден → Status = Duplicate, Action = Skip (forced)
  
  3. normalizedName = FamilyNameNormalizer.Normalize(Path.GetFileNameWithoutExtension(filePath))
  4. existingByName = SELECT * FROM catalog_items WHERE normalized_name = @normalizedName LIMIT 1
     → Если найден → Status = Existing, Action = IncrementVersion (default)
     → Если не найден → Status = New, Action = IncrementVersion (default)
```

**Важно:** Дедупликация по SHA256 — **глобальная**, не per-family. Если тот же файл был импортирован под другим именем — это дубликат.

---

## 10. Stale Marker — точный алгоритм

```
При загрузке семейства в проект (LoadToProject / LoadAndPlace):
  1. Получить current_version_label из catalog_items
  2. INSERT INTO project_usage (... loaded_version_label) VALUES (... 'v2')

При RefreshTreeAsync / LoadTreeAsync:
  1. Получить projectFingerprint = doc.PathName (или hash)
  2. Получить все записи project_usage для текущего проекта:
     SELECT catalog_item_id, loaded_version_label FROM project_usage
     WHERE project_path = @fingerprint
     ORDER BY created_at_utc DESC
  3. Для каждого FamilyLeafNodeViewModel:
     loaded = map[catalogItemId]
     current = row.CurrentVersionLabel
     IsStale = loaded is not null && loaded != current
```

**Оптимизация:** Batch-запрос `GetLoadedVersionLabelsAsync(List<string> itemIds)` вместо N+1.

---

## 11. Этапы реализации (обновлённые оценки)

### Этап 1: Обратная совместимость путей + ReleaseComObject (0.5 ч)
- [ ] `StoragePathResolver` — убрать `r{version}` из write-путей, оставить read-fallback
- [ ] `LocalFamilyFileResolver` — fallback на старые пути
- [ ] `RevitFamilyDataExtractionService` — добавить `Marshal.ReleaseComObject`

### Этап 2: Миграция БД V9 (0.5 ч)
- [ ] `FamilyCatalogSql` — добавить `loaded_version_label`
- [ ] `LocalCatalogMigrator` — V9 миграция
- [ ] `ProjectFamilyUsage` — добавить поле
- [ ] `LocalProjectFamilyUsageRepository` — обновить методы

### Этап 3: Модели Core + Локализация (0.5 ч)
- [ ] `FamilyBatchImportItem`, `FamilyBatchImportAction`, `FamilyBatchImportStatus`
- [ ] Обновить `IFamilyImportService`, `IFamilyManagerDialogService`, `IProjectFamilyUsageRepository`
- [ ] Локализация Batch Dialog (~20 ключей)

### Этап 4: Batch Dialog UI (2 ч)
- [ ] `FamilyBatchImportView.xaml` (стиль Settings/ShareProject)
- [ ] `FamilyBatchImportViewModel` + `FamilyBatchImportRow`
- [ ] `FamilyManagerDialogService.ShowBatchImportDialog`
- [ ] DI регистрация

### Этап 5: ImportService — Batch с Increment/Overwrite (2 ч)
- [ ] `LocalFamilyImportService.ImportBatchAsync`
- [ ] `OverwriteCurrentAsync` — замена файла без смены current_version_label
- [ ] Интеграция экстрактора внутри ExternalEvent

### Этап 6: ViewModel — Batch Dialog в Import + Update (1 ч)
- [ ] `ImportFilesAsync` → открыть Batch Dialog → ExternalEvent импорт
- [ ] `UpdateFamilyCommand` → открыть Batch Dialog (forced Existing mode)
- [ ] Удалить `ImportFolderAsync`, `ImportData` из VM

### Этап 7: Stale Marker (1 ч)
- [ ] `FamilyLeafNodeViewModel.IsStale`
- [ ] `FamilyManagerMainViewModel.Tree.cs` — batch-запрос loaded_version_label
- [ ] `FamilyManagerPaneControl.xaml` — визуальная индикация stale

### Этап 8: UI cleanup — удаление команд (0.5 ч)
- [ ] Удалить ImportFolder из Popup
- [ ] Удалить ImportData из FamilyLeafNodeContextMenu
- [ ] Убрать CanExecute для удалённых команд

### Этап 9: Тесты (1 ч)
- [ ] Инкремент версии
- [ ] Перезапись текущей
- [ ] Дедупликация SHA256
- [ ] Stale marker
- [ ] Обратная совместимость путей

**Итого: ~9 часов**

---

## 12. Критерии готовности

- [ ] Batch Dialog в стиле Settings/ShareProject (таблица, кнопки, локализация)
- [ ] Данные в Batch Dialog без OpenDocumentFile (BasicFileInfo + SHA256 в WPF thread, OpenDocumentFile только после "Загрузить")
- [ ] Режимы: Инкремент версии (default), Перезаписать текущую, Пропустить
- [ ] Дедупликация по SHA256 (авто-skip, глобальная)
- [ ] Инкремент создаёт vN+1 и обновляет current_version_label
- [ ] Перезапись заменяет файл, НЕ меняет current_version_label
- [ ] Stale marker отображается в дереве при устаревшей версии
- [ ] Типы и атрибуты извлекаются при импорте (через существующий экстрактор + ReleaseComObject)
- [ ] Команда "Импорт данных" удалена из контекстного меню
- [ ] Команда "Импорт папки" удалена из выпадающего меню (стрелочка остаётся)
- [ ] Обратная совместимость: старые файлы в `r{version}` читаются, новые пишутся без `r{version}`
- [ ] Все unit-тесты проходят (1105/1105)
- [ ] Сборка R24 + R25 = 0 errors, 0 warnings

---

## 13. Риски и mitigation

| Риск | Вероятность | Mitigation |
|------|-------------|------------|
| BasicFileInfo.Extract падает на файлах из будущей версии Revit | Средняя | Оборачивать в try/catch, показывать "Unknown version" |
| Batch import >32 файлов → замедление OpenDocumentFile | Средняя | Разбивать на батчи по 25 файлов с `Task.Delay(100)` между ними |
| Memory leak при batch extraction без ReleaseComObject | Высокая | **Уже добавлено в план** — Marshal.ReleaseComObject |
| Старые пути `r{version}` не находятся после изменения резолвера | Низкая | Fallback в `LocalFamilyFileResolver` + тесты |
| Stale marker тормозит LoadTreeAsync (N+1 запросов) | Средняя | Batch-метод `GetLoadedVersionLabelsAsync` |

---

## 14. Связанная документация

- `docs/adr/015-familymanager-published-storage.md` — Version → Revit-Version Model
- `docs/adr/017-familymanager-attribute-extraction.md` — OpenDocumentFile pattern
- `docs/adr/018-familymanager-refactoring.md` — async void FireAndForget, DI patterns
- `docs/adr/022-familymanager-rbac.md` — CanImport / CanEdit matrix
- `docs/invariants.md` — I-01 (ExternalEvent), I-09 (Core purity), I-10 (MVVM)
- `.agents/skills/revit-api-best-practice` — Transaction patterns, memory leaks
- `.agents/skills/revit-wpf-compat` — Application.Current is null
