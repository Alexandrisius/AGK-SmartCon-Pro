# ADR-066: Зависимости семейств (routing-фитинги, nested shared) как полноценные элементы каталога — модель `family_dependencies` (EPIC #207)

**Date:** 2026-08-06
**Status:** accepted
**Related:** Issue #207 (EPIC), #204, ADR-059 (validation gate), ADR-061 (system family sync), ADR-054 (actualization), ADR-064 (family_key), I-05, I-14, I-16

## Context

Стресс-тест 2026-08-05 (Revit 2023 + 2025): труба, импортированная в каталог
из реального проекта, ссылается routing-правилами на 13 ГОСТ-фитингов. При
DnD этой трубы в пустой проект фитинги не переносятся:
`EnsureFitting: Fitting family '…' is neither in the project nor in the catalog`,
routing-правила пропускаются с NotConverged. Root cause: импорт системной
категории фиксирует routing-фитинги только **именами** в снапшоте; как
loadable-семейства в каталог они не попадают. `CatalogFittingDependencyResolver`
уже умеет грузить фитинг ИЗ каталога — искать нечего.

Та же проблема структурно существует у **shared nested families** loadable-
родителей: имена известны (`FamilySnapshot.SharedNestedFamilyNames`), но
вложенные семейства не становятся самостоятельными элементами каталога.

Решения владельца (2026-08-05, зафиксированы как требования):

1. **Зависимость = полноценный элемент каталога** без исключений: версии,
   хэш FHV6, 3D-превью, валидация, ES-маркер при загрузке в проект.
2. **Жёсткая каскадная валидация по ADR-059**: зависимость проходит тот же
   двухфазный гейт (health-check + rule-check по правилам **своей**
   категории). Родитель не может быть импортирован, пока хотя бы один
   не-Skip ребёнок имеет gate=Failed; явный Skip ребёнка снимает блок.
3. **Грузится всегда активная версия** (`current_version_label ≤ targetRevit`)
   — это уже поведение `IFamilyFileResolver.ResolveForLoadAsync`; связи
   version-scoped только для истории/аудита, НЕ для выбора версии.
4. **Дедуп, а не дубликаты**: зависимость, уже существующая в каталоге,
   не импортируется повторно — создаётся только связь parent→child.

## Decision

### 1. Модель данных — миграция V29, таблица `family_dependencies`

```sql
CREATE TABLE IF NOT EXISTS family_dependencies (
    parent_catalog_item_id TEXT NOT NULL,
    parent_version_id      TEXT NOT NULL,
    child_catalog_item_id  TEXT NOT NULL,
    dependency_kind        TEXT NOT NULL,  -- 'routing' | 'shared_nested'
    part_name              TEXT,           -- 'Family:Type' (для routing)
    ordinal                INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (parent_catalog_item_id, parent_version_id, child_catalog_item_id, dependency_kind),
    FOREIGN KEY (parent_catalog_item_id) REFERENCES catalog_items(id)    ON DELETE CASCADE,
    FOREIGN KEY (parent_version_id)      REFERENCES catalog_versions(id) ON DELETE CASCADE,
    FOREIGN KEY (child_catalog_item_id)  REFERENCES catalog_items(id)    ON DELETE CASCADE
);
```

- **Version-scoped** (по образцу `family_nested_shared_families`): связь
  привязана к версии-эталону для истории/аудита. Sync читает связи
  **текущей активной версии** родителя (JOIN по `current_version_label`),
  а версию ребёнка резолвит как его активную (`ResolveForLoadAsync`).
- `dependency_kind` — дискриминатор: `'routing'` (E1, фитинги системных
  линейных категорий), `'shared_nested'` (E2, вложенные shared).
- `part_name` — оригинальный `"Family:Type"` из routing-правила: позволяет
  сматчить конкретное правило на конкретную связь без повторного парсинга.
- Репозиторий `IFamilyDependencyRepository` (Core) +
  `LocalFamilyDependencyRepository` (FamilyManager) по образцу
  `ISharedNestedFamilyRepository` (DELETE+INSERT per parent_version_id,
  дедуп в C# перед INSERT — NOCASE не работает для кириллицы).
- Таблица additive → `DbCompatibility.CurrentMinPluginVersion` НЕ бампается.

### 2. Сбор зависимостей — `IFamilyDependencyCollector` (Revit-граница)

Новый сервис в `SmartCon.Revit` за Core-интерфейсом:

- **Routing (E1):** по `SystemFamilySnapshot.Types[].Routing.Rules[].PartName`
  формата `"Family:Type"` резолвит `FamilySymbol` в активном документе и
  берёт `FamilySymbol.Family` — **по ссылке, не по имени** (защита от
  одноимённых семейств, #183). `Family.IsEditable == false` или
  `Family.IsInPlace` → skip + `Warn` с `[Action: …]`.
- **Nested shared (E2):** имена из `SharedNestedFamilyNames` → резолв в
  документе родителя; глубина ≤ 2, visited-set против циклов.

Результат — `FamilyDependencyDescriptor(DependencyKind, PartName,
FamilyUniqueId, FamilyName)`: collector возвращает identity, не документы
(I-05).

### 3. Авто-импорт зависимостей в batch-пайплайне

- **Механизм связей (as-built):** системный `PreparedFamilyItem` несёт
  `RoutingDependencies : IReadOnlyList<FamilyDependencyDescriptor>?`
  (transient, только Phase-1); дочерний `PreparedFamilyItem`/
  `FamilyBatchImportItem` несёт
  `DependencyLinks : IReadOnlyList<FamilyDependencyLink>?` — каждая ссылка
  указывает на родителя через `ParentSourcePath`
  (= `PreparedFamilyItem.SourcePath` = `FamilyBatchImportItem.FilePath`).
- В `FamilyImportPreparationService.PrepareProjectImportAsync` после
  подготовки родителей каждая уникальная зависимость (dedup по
  `FamilyUniqueId`) проходит **существующий путь**
  `PrepareLoadableFromProjectAsync` (EditFamily → snapshot → FHV6 →
  dedup `ContentHashDedupService.CheckAsync`) — полноценная подготовка
  со статусом New/Duplicate/Existing. Семейство, уже стоящее в очереди
  как top-level строка, НЕ дублируется — только получает ссылки.
- Действие по умолчанию для dependency-строк: `Skip` при Duplicate И при
  Existing (авто-импорт гарантирует НАЛИЧИЕ, а не авто-обновление;
  пользователь может вручную переключить на IncrementVersion/Overwrite).
- **Запись связей (as-built):** post-loop проход
  `ProjectFamilyBatchImportExecutor.WriteDependencyLinksAsync` (чистый
  `DependencyLinkPlanner` + репозиторий) — после того как обе стороны
  обработаны. Топологический порядок импорта не нужен: связи пишутся на
  ТЕКУЩУЮ версию родителя (`ReplaceForCurrentVersionAsync`). Детект
  «ребёнок импортирован» ведётся по ОРИГИНАЛЬНЫМ путям строк
  (`loadable://...`) — staging перезаписывает `FilePath` managed-путём.
- **Dedup-link**: статус Duplicate/Existing у ребёнка + действие Skip ≠
  пропуск связи — связь пишется на существующий `child_catalog_item_id`
  (`ExistingCatalogItemId` из dedup-результата). Пропущенный НОВЫЙ ребёнок
  (нет в каталоге) — связи нет, Warn.
- Родитель, не импортированный в этом батче (Skip/Error), связей не
  получает (Debug-лог per link).

### 4. Валидация — каскад по ADR-059 (E1: минимальный контур, E3: полный UX)

- Каждая дочерняя строка — обычная строка батча: health-check +
  rule-check по правилам **её собственной** категории (модель ADR-059 §2
  применяется без изменений: правила = свойство binding'а категории
  ребёнка, а не родителя).
- Категория ребёнка: как у обычной loadable-строки (категория существующего
  элемента каталога, иначе карантин «Без категории» — стандарт, не блок).
- **Блок родителя (as-built):** gate-failed ребёнок уже принудительно
  переведён в Skip стандартным гейтом ADR-059 — состояние «не-Skip
  Failed ребёнок» недостижимо, поэтому дополнительного force-Skip
  родителя не требуется. Родительская строка получает INFORMATIONAL
  индикатор (красный бейдж + тултип «зависимости не прошли проверку и
  будут пропущены: …»): родитель импортируется без таких зависимостей,
  связь на провалившегося ребёнка не пишется (он либо новый — нет
  catalog id, либо existing — существующее содержимое каталога уже
  валидно). Визуализация подсписком — E3.

### 5. Sync — резолв через связи + ES-маркер (E1)

- `IFittingDependencyResolver.EnsureFitting` получает опциональный
  родительский контекст (`parentCatalogItemId`). Порядок резолва:
  1. Уже в проекте (`FindSymbol`) — как сейчас;
  2. **По связям** `family_dependencies` активной версии родителя:
     match по `part_name` (`"Family:Type"`) → `child_catalog_item_id` →
     `ResolveForLoadAsync` (активная версия ≤ targetRevit) → `LoadFamilyAsync`;
  3. **Fallback по имени** (текущее поведение) — для эталонов,
     импортированных до фичи; miss → честный `Warn` + NotConverged.
- После успешной загрузки фитинга из каталога пишется **ES-маркер**
  (`IFamilyVersionStore.WriteToLoadedFamily`, child item + active version)
  — фитинг становится полноправным участником loadable stale-цикла (Q2).
  Маркер пишется отдельной транзакцией store'а (ADR-061 §7: загрузка и
  маркер вне основной sync-транзакции — фитинг переживает её rollback).

### 6. Этапы (каждый — отдельный PR с полным гейтом)

| Этап | Скоуп |
|---|---|
| **E1** | V29 + репозиторий + collector (routing) + авто-импорт фитингов + связи + sync по связям + маркер + каскад-блок в VM |
| **E2** | `shared_nested`: collector по SharedNestedFamilyNames (глубина ≤2), те же связи |
| **E3** | ~~Batch-диалог: `ChildRows` + `RowDetailsTemplate`, expander, multi-select рекурсивно, worst-of-children индикатор~~ **As-built (2026-08-13, #210): группировка и рекурсивный мультиселект ОТМЕНЕНЫ владельцем (Ctrl-мультиселект уже есть; у двух родителей с общим вложенным — одна строка с двумя ссылками, список остаётся плоским). Worst-of-children gate не строился: gate-failed ребёнок форс-Skip стандартным гейтом (§4), родитель получает индикатор. Сделано вместо: единый паттерн кликабельных статус-бэйджей — `StatusNotice` (title + буллет-список имён + guidance) + диалог `StatusDetailsView` (два раздельных вида: problem с действиями / info без действий), сплит-кнопка действий по эталону DbTools (ToggleButton⇄Popup, НЕ ContextMenu), presence-точка типа = команда размещения с семантикой по цветам (серый = загрузить+разместить, синий = разместить, оранжевый = обновить+разместить через `PlaceTypeFromIndicator` → `PlaceTypeCoreAsync`), DnD stale-типа перезагружает из каталога (`FamilyPlacementDragData.IsStaleInProject`).** |
| **E4** | Segment material `<none>` policy (fallback/ошибка импорта), Revit-варианты фитингов |

### 7. Что НЕ меняется

- FHV6: зависимости НЕ входят в хэш родителя (identity родителя —
  его собственный контент; смена ребёнка детектируется его собственным
  stale-циклом через маркер).
- `family_nested_shared_families` (load-time имена, REVIT-198137) —
  ортогональная таблица, не трогаем.
- Выбор версии при sync — всегда активная (`ResolveForLoadAsync`).

## Consequences

- Routing-sync в пустом проекте становится полным: фитинги гарантированно
  есть в каталоге (импортированы вместе с родителем) и грузятся по связям.
- Дубликаты ГОСТ-фитингов в каталоге исключены: dedup-link вместо
  повторного импорта.
- Каскадная валидация не даёт «мусору» пролезть через зависимости:
  битый фитинг блокирует родителя до явного Skip.
- Новые публичные типы Core: `IFamilyDependencyRepository`,
  `FamilyDependencyInfo`, `IFamilyDependencyCollector`,
  `PreparedFamilyDependency` — обновление `docs/domain/interfaces|models`
  + `validate-docs.ps1`.
- Обратная совместимость: эталоны без связей работают по fallback-имени;
  миграция V29 additive и мгновенная (без задачи актуализации — связи
  накапливаются при новых импортах; backfill существующих эталонов —
  осознанно не делаем: переимпорт родителя создаёт связи).
- Известные ограничения: (1) same-family fallback в резолвере делит
  part_name по ПОСЛЕДНЕМу ':', а sync-сплит правила — по ПЕРВОМУ: для
  семейств с ':' в имени fallback деградирует до поиска по имени
  (exact-match их покрывает, регрессии нет); (2) `PartName` связи хранит
  первый встреченный тип семейства — тип-уровневая точность не нужна:
  каталог оперирует семьями, тип подбирается в проекте (FindSymbol);
  (3) `CategoryName` дескриптора — display-имя (локализованное), как у
  существующих top-level loadable-строк.
- Follow-up (ручной тест 2026-08-06): DnD-хвост системного типа повторяет
  хвост «Загрузить в проект» — badge re-eval + ПОЛНЫЙ пересчёт presence-
  снимка (`RefreshSystemTypeProjectPresenceSafeAsync` с коалесцингом
  множественных placement-событий): неявно загруженные фитинги-
  зависимости сразу получают синие точки в дереве (ADR-063).
 - Follow-up #212 (ручной тест 2026-08-06): (а) resolver грузит фитинг
  PER-TYPE (`LoadFamilySymbolAsync` по типу правила, когда он есть в
  типах загружаемой версии) — полная загрузка семейства только для
  typeless-файлов и переименованных в проекте типов (ADR-023);
  (б) presence виртуального узла typeless-семейства (сентинел `<default>`,
  #172) — по факту загрузки семьи (`leafPresent`), иначе такие фитинги
  оставались серыми навсегда.
- Follow-up #216 (ручные тесты 2026-08-06): per-type загрузка resolver'а
  сужена до PIPE-фитингов (`OST_PipeFitting`). Доказано экспериментально:
  duct-стиль диалога настроек трассировки (воздуховоды; предположительно
  лотки/короба) индексирует части ТОЛЬКО на событии полной загрузки
  семейства (`Document.LoadFamily`) — `LoadFamilySymbol` индекс не
  обновляет (пустые строки + «НЕТ» в диалоге при рабочих правилах);
  reopen проекта и Activate+Regenerate не помогают. Pipe-диалог вычисляет
  части per-rule с size-критериями и per-type переносит. Не-pipe фитинги
  (и legacy-записи без `revit_category_id`) грузятся полным
  `LoadFamilyAsync` — единственный надёжный триггер перестройки индекса.
