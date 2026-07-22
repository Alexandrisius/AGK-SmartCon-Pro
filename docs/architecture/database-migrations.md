# Актуализация базы данных каталога (FamilyManager)

**Единый** механизм доставки изменений данных `catalog.db` пользователям, у которых база
создана старой версией SmartCon. Введён в Issue #126, переработан в движок актуализации —
ADR-054.

**Боль, которую решает паттерн:** раньше любое изменение формата данных означало
«создайте базу с нуля» или ручной переимпорт. Теперь база доезжает до актуального
состояния сама: одна команда, один диалог, один проход по файлам.

## Когда что использовать

| Ситуация | Инструмент |
|---|---|
| Новая таблица/колонка с безопасным дефолтом | Schema migration в `LocalCatalogMigrator` при подключении (CREATE/ALTER IF NOT EXISTS) — мгновенно, без UI |
| Данные надо извлечь/пересчитать из managed-файлов (нужен Revit API, долго) | **`IDatabaseActualizationTask`** — этот паттерн |

## Ключевая идея

«Хэш устарел», «нет атрибутов», «нет 3D», «нет Revit-категории» — это одно и то же: **у записи не хватает
артефактов, извлекаемых из файла**. Поэтому вместо «миграций-стадий» — **движок + задачи**:
движок находит семьи, которым не хватает хотя бы одного артефакта, открывает каждый файл
**ровно один раз** (snapshot + геометрия в одной сессии) и применяет **только недостающие**
задачи. Цена новой фичи со старыми данными — один класс-задача.

Группы грузятся для `family_source IN ('loadable','system')`; диспатч экстракции — по
расширению managed-файла: `.rfa` → полный snapshot+геометрия, staged `.rvt` (system) →
category-only `ExtractSystemCategoryAsync` (system-задачи не требуют полного snapshot).

## Состав паттерна

```
IDatabaseActualizationTask (Core)       — контракт «запроса на обновление» (задачи)
ICatalogActualizationService (Core)     — движок: union детектов, один open, apply, purge
CatalogActualizationService (FamilyManager) — реализация движка
Actualization{Group,Variant} + FamilyActualizationContext + ActualizationFailureKind (Core) — модели
DatabaseMigration{Progress,Result} (Core)   — прогресс/исход единого прогона
IDatabaseUpdateStateService (Core)      — разделяемое состояние «update required»
DatabaseUpdateStateService (FamilyManager)  — gate-диалог + единый прогон через диалог
DatabaseUpdateProgressView(Model) (FamilyManager) — ОДИН modeless диалог (ADR-048)
FamilyManagerMainViewModel.HashRecalc.cs    — маппинг состояния в VM + команда
FamilyManagerPaneControl.xaml               — красная точка + баннер + «Обновить базу»
```

Существующие задачи (`SmartCon.FamilyManager/Services/Actualization/`):

| Задача | Order | Critical | Что делает |
|---|---|---|---|
| `HashFormatActualizationTask` (`hash-v2`) | 10 | да | Хэши v2: apply на все Revit-варианты + ресинк item; system re-flag в file-free pass; терминальные маркеры -1/-2 |
| `AttributesActualizationTask` (`attributes-v1`) | 20 | нет | Типы + значения + shared nested + счётчики `types_count`/`parameters_count` (active label; чинит #151/#152/#153) |
| `GlbPreviewActualizationTask` (`glb-v1`) | 30 | нет | Auto-extracted 3D GLB превью (active label) |
| `RevitCategoryActualizationTask` (`revit-category-v1`) | 40 | нет | Backfill `catalog_items.revit_category` для loadable+system (active label; system `.rvt` — category-only extraction `ExtractSystemCategoryAsync`) |

## Два уровня критичности

| | Critical задача | Optional задача |
|---|---|---|
| Когда | Без артефакта запись в базу плодит мусор (дедуп по хэшам v2) | Артефакт косметический (атрибуты, 3D-превью) |
| UX | Жёлтый баннер + красная точка + read-only гейт write-операций | Ничего своего — только видимость команды (Owner/BimMaster) |

## UX (единый)

1. **Никаких диалогов при старте Revit.** Проверка молчаливая: после инициализации
   панели и после каждого переключения/подключения базы (дешёвые SQL COUNT задач).
2. **Critical pending > 0** → жёлтый баннер (объяснение «режим просмотра» + кнопка
   «Обновить») + красная точка на кнопке «Инструменты базы».
3. В popup инструментов — ОДНА команда **«Обновить базу»**: видна при ЛЮБОМ
   processable pending И роли Owner/BimMaster (обновление — write-операция;
   Engineer подключается read-only, запись бы упала — для него баннер/гейт
   показывают «обновление может выполнить Owner или BIM-мастер»).
   Открывает ОДИН modeless диалог: прогресс «Обработка X из Y — file.rfa», «Прервать»,
   сводка (обновлено / не удалось / требуют нового Revit / файлы не найдены + purge).
4. **База read-only пока есть ЛЮБОЙ CRITICAL pending** (ADR-054 §3a) — processable
   ИЛИ требующий более нового Revit: дедуп/целостность важнее удобства, импорт
   дубликата со stale-хэшом недопустим. Каждая write-команда гейтится через
   `IDatabaseUpdateStateService.EnsureUpToDateAsync()`:
   - processable critical → диалог «Обновить сейчас?» запускает единый прогон;
   - только newer-critical → предупреждение с версией: «требуется обновление в
     Revit {N}+ — откройте базу там и выполните «Обновить базу», тогда всё
     обновится за один раз» (оффера нет — здесь починить нельзя).
   Optional pending ничего не гейтит.
5. **ОДНА точка-индикатор на кнопке «Инструменты базы»** (ADR-054 §3a):
   **красная** при любом critical-гейте (processable или newer-only — гейт
   держится до идеальной миграции), **янтарная** при ЛЮБОМ optional pending
   (неблокирующем, рекомендованном): processable здесь (тогда есть и команда
   «Обновить базу») или newer-only (тогда команды нет — чинится в новом
   Revit). Popup — только для команд; весь поясняющий текст живёт в баннере
   (в т.ч. вариант «требуется Revit {N}+», когда гейт держится только из-за
   newer-only — кнопка «Обновить» тогда скрыта).

Гейтнутые операции (по состоянию на Issue #126):

| Область | Команды |
|---|---|
| Импорт | `ImportFilesAsync`, `ImportFileToCategoryAsync`, `ImportSelectedElementsAsync`, `ImportActiveFileAsync` |
| Загрузка в проект | `LoadToProject*`, `UpdateStale*`, `UpdateCategoryStaleAsync`, loadable `PlaceTypeAsync`, loadable `StartPlacementDrag` (DnD) |
| Семейства | `DeleteFamilyAsync`, `DropFamilyAsync` (DnD по категориям), `OpenCategoryEditorAsync` |
| Свойства | `SaveAsync` (Общие), `MakeActiveAsync`, `DeleteVersion`, все write-команды ассетов |

НЕ гейтятся: просмотр (дерево, поиск, свойства, 3D, тултипы), открытие семейства в Revit,
управление подключениями БД, профиль, настройки project-base.

## Как добавить задачу под новую фичу

Сценарий: фича добавила колонку/артефакт, который надо заполнить для старых баз извлечёнными
из файла данными.

1. Создай класс-задачу в `SmartCon.FamilyManager/Services/Actualization/`, унаследовав
   `SqlDetectionActualizationTaskBase` — она реализует три метода-детекта из абстрактного
   `DetectionSql` и даёт дефолты `RunFileFreePassAsync`→0 / `HandleGroupFailureAsync`→retry:

```csharp
internal sealed class MyFeatureActualizationTask : SqlDetectionActualizationTaskBase
{
    public MyFeatureActualizationTask(LocalCatalogDatabase database) : base(database) { }

    public override string Id => "my-feature-v1";
    public override int Order => 50;                // после revit-category-v1 (40), с шагом 10
    public override bool IsCritical => false;       // true — если без данных запись плодит мусор

    // Дешёвый FROM/JOIN/WHERE фрагмент по «пустоте колонок»; группировка
    // (item|label), фильтр Revit, newer-only — в базовом классе.
    protected override string DetectionSql => """
        FROM catalog_versions cv
        JOIN catalog_items ci ON ci.id = cv.catalog_item_id
        WHERE ci.family_source = 'loadable'
          AND cv.version_label = ci.current_version_label
          AND NOT EXISTS(SELECT 1 FROM my_feature_table x
                          WHERE x.catalog_item_id = ci.id AND x.version_id = cv.id)
        """;

    public override Task ApplyAsync(FamilyActualizationContext ctx, CancellationToken ct)
        // Записать своё из ctx.Snapshot / ctx.Geometry / ctx.Group / ctx.OpenedVariant.
        // Идемпотентно! Короткие транзакции через унаследованное Database (I-14).
        // Артефакт обязан погасить свой детект — иначе семья останется pending навсегда.
}
```

Отклонения — через `override` virtual-членов базы (образец: `HashFormatActualizationTask`
переопределяет `CountPendingAsync` (+system rows), `RunFileFreePassAsync` (system re-flag) и
`HandleGroupFailureAsync` (терминальные маркеры -1/-2)). Полный шаблон: навык
`smartcon-db-actualization` → `references/task-template.md`.

2. Зарегистрируй в `ServiceRegistrar` (секция «Database actualization engine»):

```csharp
services.AddSingleton<IDatabaseActualizationTask, MyFeatureActualizationTask>();
```

Всё остальное — автоматически: badge/баннер (critical), единая команда и диалог,
resume после отмены, purge-контекст, сводка.

## Жёсткие требования к задаче

- **Детект — дешёвый SQL** по «пустоте колонок», без побочных эффектов: вызывается на
  каждом переключении базы. `CountPendingAsync` — только processable (фильтр Revit),
  `LoadPendingGroupKeysAsync` — все группы.
- **Артефакт гасит свой детект при записи** — это возобновляемость. Проверь: после
  успешного `ApplyAsync` группа НЕ должна попадать в детект повторно (unit-тест!).
- **Apply идемпотентен** — после частичного сбоя семья может быть обработана повторно.
- **Короткие транзакции** per-group, соединения только через `LocalCatalogDatabase`
  (I-14: DELETE journal, busy_timeout, никакого WAL).
- **Revit API — только через движок**: задача получает готовые snapshot/geometry и
  НЕ открывает документы сама (исключение — сервисы, которые сами маршалят через
  `IFamilyManagerAwaitableEvent`, как `IFamilyGeometryPipeline`).
- **Scope — собственность задачи**: active-only vs все версии, system vs loadable —
  решает детект задачи, движок не навязывает.

## Тесты

- Движок: `CatalogActualizationServiceTests` (union детектов, один open, routing
  apply/failure, newer-Revit, отмена, file-free, purge).
- Задачи: `HashFormatActualizationTaskTests`, `AttributesActualizationTaskTests`,
  `GlbPreviewActualizationTaskTests` — SQLite fixture + `CatalogSeedHelper`.
- Состояние: `DatabaseUpdateStateServiceTests` (fake engine, auto-close диалога).

## Связанные документы

- [ADR-054](../adr/054-database-actualization-engine.md) — движок актуализации,
  решения и отклонённые альтернативы
- [ADR-050](../adr/050-hash-recalculation-migration.md) — историческая запись hash-миграции
  (механика маркеров/purge; фреймворк superseded ADR-054)
- [ADR-049](../adr/049-content-hash-v2-rename-invariant.md) — формат хэша
- [ADR-048](../adr/048-batch-import-modeless-progress.md) — modeless прогресс-диалоги
- `docs/invariants.md` — I-14 (SQLite), I-01 (ExternalEvent), I-03 (транзакции)
- `tools/damage-catalog-db.ps1` — ручной тест: «состаривает» тестовую БД по критериям
  детекции (атрибуты/READERROR/GLB/хэш/счётчики) и печатает ожидаемый pending
