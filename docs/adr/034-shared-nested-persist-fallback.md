# ADR-034: Persist shared nested family names at import time (REVIT-198137)

**Status:** accepted (revised — V3 architecture simplification)
**Date:** 2026-06-23 (V3 revision 2026-06-23)
**Branch:** `feature/issue-shared-nested-revit2023-fix`
**Issue:** [#77](https://github.com/Alexandrisius/AGK-SmartCon-Pro/issues/77)
**Builds on:** ADR-029 (shared-nested-family load-mode dialog, commit `bbb5dc09`)

## Контекст

ADR-029 добавил пользовательский диалог с 3 вариантами (Use Project /
Overwrite Parameters / Replace) для конфликтующих общих вложенных семейств.
Диалог показывается через `IFamilyLoadOptions.OnSharedFamilyFound` callback
для каждого shared nested, загруженного в проект и изменённого в `.rfa`.

**Баг**: в Revit 2023 (23.1.60.36) и Revit 2024 < 24.3.0.13 параметр
`sharedFamily` callback'а приходит как `null` или указывает на **parent
family** вместо nested. Это подтверждённый тикет Autodesk **REVIT-198137** —
исправлен только в Revit 2024.3.0.13+.

Из-за этого в Revit 2023:

1. Условие `if (_onSharedDecision is not null && sharedFamily is not null)`
   в `RevitFamilyLoadOptions.cs:46` всегда `false`.
2. Наш диалог **не показывается**.
3. Срабатывает default branch: `FamilySource.Family + overwrite=true` —
   Revit молча перезаписывает все shared nested.
4. Пользователь не понимает почему «при ручной загрузке в Revit диалог есть,
   а через Family Manager — нет».

**Дополнительное ограничение проекта**: семейство `.rfa` должно открываться
**один раз за всё время жизни** в FM (принцип проекта для минимизации
freeze-багов Revit 2023 и нагрузки на UI). Открывать `.rfa` повторно при
каждой загрузке в проект — запрещено.

## Решение

При импорте в FM извлекаем имена всех shared nested families (один раз на
версию) и сохраняем в новую таблицу `family_nested_shared_families` в
локальной SQLite-БД. При загрузке в проект читаем эти имена и передаём в
`RevitFamilyLoadOptions` как **fallback** для случая, когда Revit API
вернёт `null`.

### 1. Схема БД (миграция V13)

```sql
CREATE TABLE family_nested_shared_families (
    catalog_item_id TEXT NOT NULL,
    version_id TEXT NOT NULL,
    nested_family_name TEXT NOT NULL,
    ordinal INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (catalog_item_id, version_id, nested_family_name),
    FOREIGN KEY (catalog_item_id) REFERENCES catalog_items(id) ON DELETE CASCADE,
    FOREIGN KEY (version_id) REFERENCES catalog_versions(id) ON DELETE CASCADE
);
CREATE INDEX ix_nested_shared_version ON family_nested_shared_families (version_id);
```

- **Композитный PK** обеспечивает уникальность внутри одной версии.
- `ON DELETE CASCADE` от обоих родителей — каскадная очистка при удалении
  семейства или версии. `PRAGMA foreign_keys=ON` уже включён в
  `LocalCatalogMigrator.MigrateAsync`.
- Только **один** индекс `ix_nested_shared_version` (по `version_id`) — нет
  запросов вида «где упоминается имя `Болт М12`» (для этого потребуется
  full-scan + десериализация JSON-подобных строк, а сейчас такого use case
  нет). Если в будущем появится такой сценарий — добавится индекс по
  `nested_family_name`. См. комментарий в
  `FamilyCatalogSql.cs:399` (`ix_nested_shared_name: intentionally removed`).
- Дедупликация и case-insensitive сравнение делаются в C#-слое
  (`LocalSharedNestedFamilyRepository` использует `StringComparer.OrdinalIgnoreCase`),
  а не на стороне SQL — SQLite `COLLATE NOCASE` некорректно работает для
  не-ASCII (русские/китайские имена семейств).

### 2. Извлечение имён при импорте (V3 — единый open-close цикл)

Имена shared nested собираются **внутри** уже существующего
`IFamilyDataExtractionService.ExtractFromManagedFile` open-close цикла,
а не отдельным extractor'ом:

- `RevitFamilyDataExtractionService.ExtractFromManagedFile` (SmartCon.Revit)
  открывает `.rfa` через `UIApplication.OpenDocumentFile(rfaPath)` (НЕ
  активирует — без переключения фокуса).
- Внутри того же открытого документа:
  1. `ExtractCore` собирает типы и значения параметров.
  2. **Затем**, до `Close(false)`, `CollectSharedNestedFamilyNames(doc)`
     итерирует `FilteredElementCollector.OfClass(typeof(FamilyInstance))`
     и фильтрует по `Family.FAMILY_SHARED` built-in parameter (=1 → shared).
- Имена дедуплицируются case-insensitive, возвращаются в порядке
  первого появления.
- `FamilyExtractionResult` (Core) расширен nullable-полем
  `SharedNestedFamilyNames` и non-null accessor'ом
  `SharedNestedFamilyNamesSafe` (для совместимости со старыми
  вызывающими, которые не передают параметр).
- Сохранение в БД делегировано вызывающему (ViewModel) — после
  возврата из `ExtractFromManagedFileAsync` он вызывает
  `SaveSharedNestedNamesAsync` (новый helper в
  `FamilyManagerMainViewModel.Extract.cs`), который
  использует `ISharedNestedFamilyRepository.ReplaceForVersionAsync`.
- `RevitBalloonNudge.Nudge()` после `Open+Close` cycle (freeze workaround
  REVIT-236376 / REVIT-237190, см. skill `revit-api-best-practice`).
- **Graceful degradation**: при любой ошибке внутри
  `CollectSharedNestedFamilyNames` пишется `Warn` с `[Action: ...]`, и
  результат с `SharedNestedFamilyNames = Array.Empty<string>()` всё равно
  возвращается. Импорт **не прерывается**.

#### V3 (rejected V2 — почему)

V2 делала то же самое через **отдельный** `ISharedNestedFamilyExtractor`,
который открывал `.rfa` **второй раз** параллельно с
`ExtractFromManagedFile`. Это давало **2 OpenDocumentFile** на одно
семейство и удваивало количество MFC family-upgrade диалогов
(пользователь увидел 4 диалога на 2 файла вместо ожидаемых 2). Это
**прямо нарушает** принцип проекта «открываем семейство один раз».

V3 (принятая) устраняет этот класс ошибок: нет отдельного extractor'а,
нет `IFamilyManagerAwaitableEvent` маршалинга между двумя операциями,
нет второго `OpenDocumentFile`. Существующая V2-реализация удалена
(`ISharedNestedFamilyExtractor` и `RevitSharedNestedFamilyExtractor`
больше не существуют).

Wiring: `SaveSharedNestedNamesAsync` вызывается в трёх местах,
сразу после успешного `ExtractFromManagedFileAsync`:

- `FamilyManagerMainViewModel.ExtractTypesForImportedFamilies` (Import.cs:227) — batch import.
- `FamilyManagerMainViewModel.ExtractAttributesForImportedFamilies` (FamilyEdit.cs:608) — active file.
- `FamilyManagerMainViewModel.ExtractAttributesForLoadableTasks` (FamilyEdit.cs:555) — loadable tasks.

### 3. Чтение при Load в Project

`RevitFamilyLoadService.LoadFamilyAsync`:
- Резолвит имена через `ISharedNestedFamilyRepository.GetNamesForCurrentVersionAsync(catalogItemId)`.
- Передаёт список в конструктор `RevitFamilyLoadOptions`.

`RevitFamilyLoadService.LoadFamilySymbolAsync` (Type Catalog load path):
- Также принимает `catalogItemId` (опциональный) и резолвит имена тем же способом.
- Этот путь наиболее подвержен REVIT-198137 (каждый symbol заново резолвит parent family → `OnSharedFamilyFound`).
- Если `catalogItemId` не передан и `nestedSharedNames` тоже null — логируется Warn с подсказкой caller'у передавать `catalogItemId`.

`RevitFamilyLoadOptions.OnSharedFamilyFound`:
- Использует `SharedFamilyNameResolver` (чистый C#, unit-тестируемый) для
  выбора источника имени: RevitApi → CatalogDb → FallbackPlaceholder.
- `Interlocked.Increment` счётчик обеспечивает правильное соответствие
  индекса вызова к индексу в списке (callback может вызываться в любом
  порядке, но **порядок вызовов одинаков** в Revit — счётчик только для
  защиты от race).

### 4. UX диалога

`SharedFamilyDecisionRequest` (record) расширен:
- `IndexInBatch` (1-based) — для отображения "Shared nested 2 of 5".
- `TotalInBatch` (0 = legacy / нет данных) — управляет видимостью строки.
- `NameSource` (enum: `RevitApi` / `CatalogDb` / `FallbackPlaceholder`) —
  для прозрачности источника имени.

Новый enum `SharedFamilyNameSource` в Core.

`SharedFamiliesLoadModeDialogView` (XAML) получил:
- Строка `BatchProgress` (мелкий серый текст) — "Shared nested 2 of 5",
  видна только при `TotalInBatch > 1`.
- Строка `SourceIndicator` (italic 10pt) — "name from SmartCon catalog" или
  "name unavailable — re-import the family in Family Manager",
  видна только при `NameSource != RevitApi`.

### 5. Архитектурные изменения (V3)

#### Core модели
```
src/SmartCon.Core/Models/FamilyManager/
├── SharedFamiliesLoadChoice.cs       (без изменений)
├── SharedFamilyDecisionRequest.cs    (расширен: +IndexInBatch, +TotalInBatch, +NameSource)
├── SharedFamilyNameSource.cs         (NEW enum)
└── SharedFamilyNameResolver.cs       (NEW — pure C#, testable counter + fallback)
```

#### Core interfaces
```
src/SmartCon.Core/Services/Interfaces/
├── ISharedNestedFamilyRepository.cs  (NEW)
└── IFamilyLoadService.cs             (расширен: +nestedSharedNames параметр)
```

`IFamilyDataExtractionService.cs` (V3):
- `FamilyExtractionResult` (record) расширен **nullable**-полем
  `IReadOnlyList<string>? SharedNestedFamilyNames = null` (для source
  compat) и non-null accessor'ом `SharedNestedFamilyNamesSafe`.

**Удалено (V3):**
- ~~`ISharedNestedFamilyExtractor.cs`~~ — больше не нужен; извлечение
  делает `IFamilyDataExtractionService.ExtractFromManagedFile` в своём
  open-close цикле.

#### FamilyManager implementation
```
src/SmartCon.FamilyManager/Services/LocalCatalog/
├── FamilyCatalogSql.cs               (+ CreateFamilyNestedSharedFamilies, +CreateNestedSharedFamiliesIndexes)
├── LocalCatalogMigrator.cs           (+ MigrateV13Async, + EnsureCriticalColumns)
└── LocalSharedNestedFamilyRepository.cs  (NEW)
```

`LocalFamilyImportService.cs` (V3):
- Удалены поля `_nestedSharedExtractor` и `_awaitableEvent`.
- Удалён метод `PersistNestedSharedFamilyNamesAsync` (~80 строк,
  включая `RaiseAsyncTask` маршалинг).
- Persistence shared-nested имён теперь живёт в
  `FamilyManagerMainViewModel.SaveSharedNestedNamesAsync` (новый
  helper в `FamilyManagerMainViewModel.Extract.cs`).
- Сохранение имён **не** привязано к DB-транзакции импорта — это
  отдельная операция, которая может упасть независимо.

#### Revit implementation
```
src/SmartCon.Revit/FamilyManager/
├── RevitFamilyDataExtractionService.cs  (РАСШИРЕН: +CollectSharedNestedFamilyNames,
│                                          +IsSharedFamily; existing
│                                          ExtractFromManagedFile populates
│                                          result.SharedNestedFamilyNames)
├── RevitFamilyLoadOptions.cs            (использует SharedFamilyNameResolver)
└── RevitFamilyLoadService.cs            (резолвит имена из БД)
```

**Удалено (V3):**
- ~~`RevitSharedNestedFamilyExtractor.cs`~~ — больше не нужен.

#### ViewModel
```
src/SmartCon.FamilyManager/ViewModels/
├── FamilyManagerMainViewModel.Extract.cs  (NEW helper: SaveSharedNestedNamesAsync;
│                                            +SharedNestedRepository injection;
│                                            +using SmartCon.Core.Logging)
├── FamilyManagerMainViewModel.Import.cs   (вызывает SaveSharedNestedNamesAsync
│                                            в ExtractTypesForImportedFamilies)
├── FamilyManagerMainViewModel.FamilyEdit.cs (вызывает SaveSharedNestedNamesAsync
│                                              в ExtractAttributesForImportedFamilies
│                                              и ExtractAttributesForLoadableTasks)
└── FamilyManagerServices.cs              (+ISharedNestedFamilyRepository SharedNestedRepository)
```

#### UI
```
src/SmartCon.FamilyManager/
├── ViewModels/SharedFamiliesLoadModeDialogViewModel.cs  (+BatchProgress, +SourceIndicator)
└── Views/SharedFamiliesLoadModeDialogView.xaml          (+ 2 строки)
```

#### DI
```
src/SmartCon.App/DI/ServiceRegistrar.cs
  + ISharedNestedFamilyRepository → LocalSharedNestedFamilyRepository
  − (V3) ISharedNestedFamilyExtractor → RevitSharedNestedFamilyExtractor  [УДАЛЕНО]
```

## Альтернативы (рассмотренные, но не выбранные)

### A. Открывать `.rfa` при каждой загрузке в проект

**Отвергнуто**: нарушает принцип проекта "открываем семейство один раз".
Также: в больших проектах (сотни shared nested) это приведёт к десяткам
открытий-закрытий `.rfa` за один сеанс, что усугубит баги
REVIT-236376 / REVIT-237190 (freeze) и REVIT-198137 (null sharedFamily).

### A2 (V3, rejected). Отдельный `ISharedNestedFamilyExtractor` с собственным OpenDocumentFile

**Отвергнуто в V3**: реализация V2 так делала и **удваивала** количество
MFC family-upgrade диалогов на импорт (4 диалога на 2 файла вместо
ожидаемых 2). Это нарушало явный принцип проекта «открываем семейство
один раз». V3 фиксит это: `RevitFamilyDataExtractionService.ExtractFromManagedFile`
собирает shared-nested имена в **том же** open-close цикле, что и
типы/параметры.

### B. JSON-колонка в `family_versions` вместо отдельной таблицы

**Отвергнуто**: не позволяет искать "где используется `Болт М12`?"
по индексу (потребуется full-scan + JSON parse). Проект версии 2.0.0
— breaking change, лучше сделать правильно один раз. Таблица также
позволяет будущие фичи типа "отчёт о зависимостях семейств".

### C. Извлечение имён через `BasicFileInfo.Extract` (без открытия)

**Отвергнуто**: `BasicFileInfo` не содержит информации о nested families
(только метаданные файла: version, format, family/title). Нужно полноценное
открытие документа.

### D. Передавать имена через `FamilyVersion` ExtensibleStorage (SSOT)

**Рассмотрено, отложено**: текущее ExtensibleStorage решение для
`project_usage` (ADR-031) хранит версии в самом `.rfa`. Это даёт
преимущество: версия привязана к файлу, а не к индексу в каталоге.
Однако:

1. Это не снимает необходимости открывать `.rfa` для записи (при импорте).
2. Это требует миграции ES-схемы для всех существующих `.rfa` в каталоге
   (мы заявили 2.0.0 breaking change, но миграция файлов на диске — это
   отдельный процесс).
3. SQLite-таблица проще в обслуживании, не требует transactional
   consistency между файловой системой и БД.

Решено: оставить SQLite-таблицу. Миграция на ExtensibleStorage — отдельная
задача, если проект пойдёт по пути "минимум файлов вне .rfa".

## Тестирование

### Unit-тесты (новые в V2, V3 оставил)

| Файл | Тестов | Что покрывает |
|---|---|---|
| `SharedFamilyNameResolverTests.cs` | 11 | counter increments, fallback chain (RevitApi > CatalogDb > Placeholder), thread-safety, edge cases (counter=0, beyond list, null name) |
| `LocalSharedNestedFamilyRepositoryTests.cs` | 18 | CRUD, replace semantics, case-insensitive dedup, ordinals, cascade FK, arg validation, migration V13 creates table (включая 5 интеграционных тестов на существующей V12 БД и concurrent threads) |
| `SharedFamiliesLoadModeDialogViewModelTests.cs` (расширен) | 15 (было 9, +6 в V2) | BatchProgress visible only when TotalInBatch > 1, SourceIndicator visible for CatalogDb and FallbackPlaceholder, hidden for RevitApi |
| `LocalCatalogMigratorTests.cs` (расширен) | 5 (было 4, +1 в V2) | V13 создаёт таблицу `family_nested_shared_families` + индексы |

**Удалено в V3:**
- ~~`LocalFamilyImportServiceNestedSharedTests.cs` (4 теста)~~ — тестировал
  `PersistNestedSharedFamilyNamesAsync`, который больше не существует.
  Persistence-логика тестируется через `LocalSharedNestedFamilyRepositoryTests`
  (18 SQL-тестов), а извлечение — co-located в
  `RevitFamilyDataExtractionService.ExtractFromManagedFile` и не покрывается
  unit-тестами (Revit API типы, как и другие native sealed типы,
  не мокаются — см. skill `smartcon-testing`).

**Итог V3:** 49 unit-тестов остаются (11 + 18 + 15 + 5), 4 удаляются. Полный прогон:
**1471/1471 проходят** (было 1475 в V2 → -4 за удалённые тесты V2).

### Что НЕ покрыто unit-тестами

`RevitFamilyLoadOptions.OnSharedFamilyFound` напрямую — Revit API тип
`Autodesk.Revit.DB.Family` sealed native, не мокается (см. skill
`smartcon-testing` / `revit-mocking.md`). Логика counter + fallback
вынесена в чистый `SharedFamilyNameResolver` и покрыта 11 unit-тестами.
End-to-end проверка — ручной тест в Revit 2023 (см. Issue #77).

## Сборка

- Debug.R25: ✅ 0 warnings, 0 errors
- Debug.R24: ✅ 0 warnings, 0 errors
- Debug.R21: ✅ 0 warnings, 0 errors
- Debug.R19: ✅ 0 warnings, 0 errors
- Тесты: ✅ 1471/1471 passed (было 1434 → +37 net: 18 repo + 11 resolver + 6 VM
  + 1 migrator + 5 nested shared integration − 4 за удалённые V2-тесты
  `LocalFamilyImportServiceNestedSharedTests`. Подробная разбивка: 11 + 18 + 15 + 5 = 49
  тестов в ADR-034-таблице выше; остальные +14 даёт расширение
  `SharedFamilyDecisionRequest` и `SharedFamilyNameSource` enum'а).

## Известные ограничения после валидации

- **Субагент-валидация** нашла 5 critical / 6 high багов в V1, затем
  1 critical (V2) и 1 critical (V3) после моих попыток исправления.
  Применённые фиксы:
  - **C1 (Revit API в `Task.Run`)** — V1. `RevitSharedNestedFamilyExtractor.ExtractAsync`
    вызывал `Task.Run` вокруг Revit API → исправлено в V2 через
    `IFamilyManagerAwaitableEvent.RaiseAsyncTask`. Полностью устранено в
    V3: extractor удалён.
  - **C-V2 (двойной OpenDocumentFile)** — V2. `RevitSharedNestedFamilyExtractor`
    открывал `.rfa` второй раз параллельно с `ExtractFromManagedFile`,
    удваивая MFC family-upgrade диалоги. **Исправлено в V3** через
    co-location: `RevitFamilyDataExtractionService.ExtractFromManagedFile`
    собирает shared-nested имена в своём open-close цикле.
  - **C2 (мёртвый код `TryResolveNestedNamesForPathAsync`)** — V2 →
    заменён на реальный lookup через `catalogItemId` в `LoadFamilySymbolAsync`.
  - **C15 (long-lived scope в `LoadFamilyAsync`)** — V2. scope вынесен в
    `PrepareForLoadAsync`, охватывает только preparation phase.
  - **Version bump** — V2. `Version.txt` 1.9.3 → 2.0.0.
  - **COLLATE NOCASE** — V2. убран с подробным комментарием (SQLite NOCASE
    не работает для не-ASCII; дедуп перенесён полностью в C#-слой).
  - **CASCADE delete + V12→V13 миграция** — V2. добавлены интеграционные
    тесты на существующей БД.

## Логирование

Все новые scope следуют правилам `skill smartcon-logging`:
- `FilePath` в scope = `Path.GetFileName()` (L8)
- Каждый `Warn` заканчивается `[Action: ...]` (L9)
- Долгие операции (OpenDocumentFile) — отдельный scope с `Freeze` маркерами,
  внешний scope метода не оборачивает (C15)
- Нет long-lived scope вокруг всего `ExtractFromManagedFile` (C15 fix) —
  scope только на период `Open+Extract+Collect+Close`.

Пример scope-цепочки при импорте:
```
[OpId=84bc3087 Op=FMImport ...] [OpId=deb14b6a Op=LocalImport ...]
[OpId=abc Op=FamilyDataExt Method=ExtractFromManagedFile ...]
[OpId=def Op=FMSharedNestedSave ...]
[OpId=77bb09b3 Op=NestedSharedRepo Method=ReplaceForVersionAsync ...]
```

## Ручное тестирование (обязательно перед merge)

1. Открыть Revit 2023.1.60.36.
2. Создать проект с шаровым краном (`Болт М12`, `Гайка М12`, `Шайба М12`
   как shared nested).
3. Импортировать 2 фланцевых затвора с теми же shared в FM (batch import).
4. **Проверить** в `smartcon.log` что для каждого `.rfa` ровно **один**
   `OpenDocumentFile` (не два) — раньше V2 выдавал 2 на файл, V3 — 1.
5. Проверить, что в SQLite БД (`%APPDATA%\SmartCon\FamilyManager\default\catalog.db`)
   появились строки в `family_nested_shared_families` (по 3 на семейство).
6. Разместить затвор через контекстное меню Family Manager.
7. **Ожидаемо**: диалог SmartCon с реальным именем "Болт М12" (а не
   "unavailable") и подписью "(имя из каталога SmartCon)".
8. Повторить в Revit 2025 — должно работать как раньше (имя из Revit API,
   индикатор скрыт).

## Следствие

- **Pos**: диалог снова виден в Revit 2023/2024.2, имя — настоящее.
- **Pos**: готовый data layer для будущего "report: где используется
  конкретное shared nested" (нужна только query-логика, не схема).
- **Pos**: логика fallback тестируется без Revit API (чистый C#).
- **Pos (V3)**: один `OpenDocumentFile` на `.rfa` (вместо двух в V2) —
  нет лишних MFC family-upgrade диалогов, нет лишней нагрузки на
  UI thread, нет дубля freeze workaround. Соответствует принципу
  проекта «открываем семейство один раз».
- **Neg**: версии 2.0.0 — breaking change: старые каталоги показывают
  placeholder до повторного импорта семейства в FM. Mitigated: пользователь
  получает подсказку "re-import the family in Family Manager".
- **Neg**: extractor открывает `.rfa` при импорте (1-2 секунды на семейство
  на больших файлах). Mitigated: импорт не hot-path, +Warn в лог.

## Связанные

- ADR-029: shared-nested-family load-mode dialog
- ADR-031: FireAndForget UI marshalling
- Issue #77: REVIT-198137 в Revit 2023
- Issue #67: original user report
