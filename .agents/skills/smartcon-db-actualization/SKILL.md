---
name: smartcon-db-actualization
description: SmartCon database actualization engine (FamilyManager, ADR-054). Use when adding a migration/actualization task for old catalog.db databases (critical or optional), when a new feature needs extraction-time data backfilled into existing user databases, when working with IDatabaseActualizationTask / ICatalogActualizationService, hash_format_version, "Обновить базу" command, banner/gate/amber-dot UX, or tools/damage-catalog-db.ps1. Triggers on: миграция базы, актуализация БД, обновить базу, старые базы, legacy catalog.db, critical/optional migration task, actualization engine.
---

# SmartCon Database Actualization Engine

Как устаревшие `catalog.db` пользователей доезжают до актуального состояния.
SSOT по архитектуре: `docs/architecture/database-migrations.md` + `docs/adr/054-database-actualization-engine.md`
(ADR-050 — superseded, только механика hash-маркеров). Этот навык — оперативный чеклист.

## Модель в одном абзаце

«Хэш устарел», «нет атрибутов», «нет 3D» — одно и то же: **у записи не хватает артефакта,
извлекаемого из файла**. Движок union'ит детекты задач (SQL по «пустоте колонок»),
открывает каждую pending-семью **ровно один раз** (snapshot+геометрия в одной сессии) и
применяет только pending-задачи. Новая фича со старыми данными = **один класс-задача +
строка в DI** — движок, диалог, гейт, resume, purge бесплатны.

## Карта кода

| Слой | Файлы |
|---|---|
| Контракт | `src/SmartCon.Core/Services/Interfaces/IDatabaseActualizationTask.cs`, `ICatalogActualizationService.cs` |
| Движок | `src/SmartCon.FamilyManager/Services/Actualization/CatalogActualizationService.cs` |
| База задач | `SqlDetectionActualizationTaskBase` (та же папка) — реализует 3 метода-детекта из абстрактного `DetectionSql`; дефолты: `RunFileFreePassAsync`→0, `HandleGroupFailureAsync`→retry. Наследуй её, не копируй шаблон |
| Задачи | та же папка: `HashFormatActualizationTask` (`hash-v2`, Order=10, critical, override'ит 3 члена), `AttributesActualizationTask` (`attributes-v1`, 20, optional), `GlbPreviewActualizationTask` (`glb-v1`, 30, optional), `RevitCategoryActualizationTask` (`revit-category-v1`, 40, optional), `FamilyFactsActualizationTask` (`family-facts-v1`, 50, optional, детект генерируется из `FamilyFactRuleSet` — ADR-055), `MiniProjectMarkerActualizationTask` (`mini-project-marker-v1`, 60, optional — **пишет В MANAGED-ФАЙЛ**, см. секцию ниже) |
| Модели Core | `Actualization{Group,Variant}`, `FamilyActualizationContext`, `ActualizationFailureKind`, `DatabasePendingBreakdown`, `NewerOnlyPendingInfo`, `DatabaseMigration{Progress,Result}` |
| Revit-граница | `IFamilyMigrationExtractor.ExtractLoadableWithGeometryAsync` (SmartCon.Revit) — ОДИН open: snapshot+geometry. Staged system `.rvt` → `ExtractSystemCategoryAsync` (category-only); движок диспатчит по расширению, группы грузятся для `family_source IN ('loadable','system')` |
| Состояние/UX | `IDatabaseUpdateStateService` (+ impl в `Services/Migrations/`), `DatabaseUpdateProgressViewModel/View`, `FamilyManagerMainViewModel.HashRecalc.cs`, `FamilyManagerPaneControl.xaml` (DbTools toggle/banner/popup) |
| DI | `SmartCon.App/DI/ServiceRegistrar.cs` — секция «Database actualization engine» |

## Решение: куда идёт новое требование к данным

| Ситуация | Инструмент |
|---|---|
| Новая таблица/колонка с безопасным дефолтом | DDL-мигратор `LocalCatalogMigrator` — мгновенно, без UI |
| Колонку/артефакт надо заполнить данными, извлекаемыми из managed-файла | **Задача `IDatabaseActualizationTask`** — этот навык |
| Только read-путь (вычисляется на лету) | Ничего — не плоди задачи |

## Решение: critical или optional

| | Critical (`IsCritical = true`) | Optional (`false`) |
|---|---|---|
| Когда | Без артефакта **запись в базу плодит мусор** (дедуп по хэшам, целостность ссылок, инвариант фичи) | Артефакт **косметический**: атрибуты, превью, счётчики, всё что можно дозаполнить отложенно |
| UX | Красная точка + баннер + **read-only гейт** всех write-операций до идеальной миграции (processable И newer-only!) | Янтарная точка («рекомендовано») + команда «Обновить базу» (Owner/BimMaster); гейта НЕТ |
| Тест на решение | «Если пользователь запишет в старую базу без этой миграции — будет ли дубль/рассинхрон?» Да → critical | Нет → optional |

Newer-only (все варианты файла новее запущенного Revit): critical — **гейтит так же**
(пользователь голосовал за это: дедуп важнее удобства), баннер/гейт показывают
`RequiredRevitVersion` («Revit {N}+ — обновится всё за раз»); optional — янтарь с версией.

## Как добавить задачу (пошагово)

1. Прочитай `references/task-template.md` — там минимальный шаблон на базовом классе.
2. Создай `XxxActualizationTask : SqlDetectionActualizationTaskBase` в `Services/Actualization/` (`internal sealed`). Объяви только: `Id`, `Order` (шаг 10), `IsCritical`, `DetectionSql`, `ApplyAsync` — три метода-детекта даёт база. Отклонения — через `override` отдельных virtual-членов (образец: hash-задача).
3. Детект = дешёвый SQL-фрагмент `FROM catalog_versions cv JOIN catalog_items ci ... WHERE ...` по «пустоте колонок», группировка `(item|label)` — в базе. Scope (active-only vs все версии, system vs loadable) — твой, живёт в SQL.
4. `ApplyAsync` — идемпотентная запись из `ctx.Snapshot`/`ctx.Geometry`; короткие транзакции через `Database` (унаследованное свойство, I-14). **Записанный артефакт обязан погасить твой детект** — иначе вечный pending.
5. `HandleGroupFailureAsync` — по умолчанию retry (ничего не пиши). Override только для терминального маркера (как у hash: -1/-2) — тогда детект обязан его уважать (`NOT IN (2,-1,-2)`).
6. `RunFileFreePassAsync` — по умолчанию 0. Override только для мгновенных UPDATE без файлов (как system re-flag у hash).
7. DI: `services.AddSingleton<IDatabaseActualizationTask, XxxActualizationTask>();`
8. Тесты: SQLite fixture + `CatalogSeedHelper` (см. `HashFormatActualizationTaskTests`): детект (processable/newer/healthy), apply пишет и **гасит детект** (re-count = 0!), failure-маркеры.
9. Доки: `docs/architecture/database-migrations.md` (таблица задач), domain interfaces/models, `validate-docs.ps1`.
10. Ручной тест: `tools/damage-catalog-db.ps1` ломает тестовую БД по критериям (см. `references/gotchas-and-testing.md`).

## Жёсткие правила (нарушение = баг, пойманный на этой сессии)

- **НЕ** открывай документы в задаче — snapshot/geometry приходят в контексте из одного open'а движка (исключения: сервисы со своим `IFamilyManagerAwaitableEvent` — `IFamilyGeometryPipeline` и задачи, пишущие в managed-файл, см. секцию ниже).
- Семантика детекта реализована в `SqlDetectionActualizationTaskBase` — **не переопределяй** три метода без нужды: `CountPendingAsync` — только processable (фильтр Revit), `LoadPendingGroupKeysAsync` — ВСЕ группы (движок классифицирует), `GetNewerOnlyPendingAsync` — openability по ВСЕМ вариантам группы (apply идёт на все!), `RequiredRevitVersion = MAX over groups of MIN(variant revit)`.
- Данные/хэш — на ВСЕ Revit-варианты label (контент идентичен); ресинк `catalog_items` только когда `Group.IsActiveLabel`.
- net48: **нет `IReadOnlySet<T>`** (используй `IReadOnlyCollection<T>`), **нет default interface methods** — все члены контракта обязательные.
- `Progress<T>` в тестах — гонка (Post в xUnit context): в координирующем коде inline-адаптер `IProgress<T>`, в тестах синхронный fake (см. gotchas).
- XAML: **запрещены вложенные markup-расширения** (`{loc:Loc ...}` внутри `{Binding ...}`) — крашит загрузку плагина целиком (runtime, не compile!). Fallback'ы локализации — в VM-свойствах.
- Диалоговые VM: состояние команд выставляй ДО флага экрана (`CanClose` перед `IsSummaryVisible`) — иначе дедлок `DialogCompletion`.

## Существующие задачи — образцы для копирования

| Нужно | Копируй |
|---|---|
| Маркерная колонка + терминальные состояния + все версии + file-free pass + кастомный count (override'ы поверх базы) | `HashFormatActualizationTask` |
| Replace-запись через существующие репозитории + счётчики + active-only | `AttributesActualizationTask` |
| Вызов сервиса, который сам маршалит в Revit (GLB pipeline) | `GlbPreviewActualizationTask` |
| Минимальная задача: item-level UPDATE одной колонки из snapshot | `RevitCategoryActualizationTask` |
| **Задача, пишущая В MANAGED-ФАЙЛ** (open → изменить → `Document.Save()` → cleanup) | `MiniProjectMarkerActualizationTask` + `RevitMiniProjectActualizationService` |

## Паттерн: задачи, пишущие в managed-файл (#189)

Базовое правило «НЕ открывай документы в задаче» имеет ОДНО легальное исключение:
файловый артефакт, который нельзя извлечь из snapshot-контекста — его надо
ЗАПИСАТЬ в managed-файл (ES-маркер, binary patch). Тогда:

1. **Revit-работа живёт в отдельном сервисе** за Core-интерфейсом
   (`IMiniProjectActualizationService`), который сам маршалит через
   `IFamilyManagerAwaitableEvent` (как `IFamilyGeometryPipeline`). Задача
   (FamilyManager-слой) Revit API не трогает; snapshot-контекст движка не используется.
2. **`Document.Save()`, НЕ `SaveAs`** — тот же путь/версия, иначе сломается
   `family_files.relative_path`. Managed storage read-only (I-16): снять
   `FileAttributes.ReadOnly` перед Save и ВЕРНУТЬ в `finally` (прецеденты:
   `SystemFamilyRevitOperations.cs:339/343`, staging).
3. **Бэкапы Revit**: `Save()` оставляет `name.NNNN.rvt` рядом — удалять
   (паттерн ровно 4 цифры; ошибка удаления = Warn, не фейл). В папке версии
   обязан остаться один .rvt. Cleanup вызывать и на skip-пути (стейл от прошлых сбоев).
4. **Идемпотентность двойная**: SQL-колонка (детект) И содержимое файла
   (skip-перезапись) — файл может быть уже «чиненым» при 0 в колонке
   (staging пишет артефакт до появления колонки).
5. **RevitAPIUI — только через NoInlining-метод**: статическая ссылка на
   `UIApplication` в основном пути метода крашит JIT в DB-only хосте
   Nice3point (`FileLoadException` + AVE сессии). Unwrap в отдельный
   `[MethodImpl(NoInlining)]` метод — хост поставляет bare
   `ApplicationServices.Application`, production-ветка JIT'ится только в Revit.
6. **Newer-варианты НЕ фейлить терминально**: вариант новее хоста не
   открывается — skip с оставлением pending (`variant.RevitMajorVersion >
   context.OpenedVariant.RevitMajorVersion` → continue), иначе -1 навсегда
   потеряет файл после апгрейда пользователя (review M1). `HandleGroupFailureAsync`
   -1/-2 на все варианты — принятый trade-off (конвенция hash-задачи).
7. **Маркерная колонка — через DDL-миграцию** (V28: `es_marker_version
   INTEGER NOT NULL DEFAULT 0`) + fresh CREATE TABLE + `EnsureCriticalColumnsAsync`
   + версии-тесты 27→28. **Новые версии при staging сразу пишут 1** —
   иначе каждый импорт re-pend'ит задачу (insert + overwrite пути
   `LocalFamilyImportService`).
8. Ошибка открытия файла (залочен, сеть) = terminal -1 с Warn+Action —
   принятое расхождение: summary движка считает группу updated (review M2,
   задокументировано в XML-doc задачи).

## Ссылки

- `references/task-template.md` — полный шаблон класса-задачи с SQL и тестами
- `references/gotchas-and-testing.md` — готчи сессии + ручное тестирование damage-скриптом
- `docs/architecture/database-migrations.md` — паттерн целиком (обновляй при изменениях!)
- `docs/adr/054-database-actualization-engine.md` — решения и отклонённые альтернативы
