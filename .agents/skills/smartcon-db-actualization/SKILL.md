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
| Задачи | та же папка: `HashFormatActualizationTask` (`hash-v2`, Order=10, critical), `AttributesActualizationTask` (`attributes-v1`, 20, optional), `GlbPreviewActualizationTask` (`glb-v1`, 30, optional) |
| Модели Core | `Actualization{Group,Variant}`, `FamilyActualizationContext`, `ActualizationFailureKind`, `DatabasePendingBreakdown`, `NewerOnlyPendingInfo`, `DatabaseMigration{Progress,Result}` |
| Revit-граница | `IFamilyMigrationExtractor.ExtractLoadableWithGeometryAsync` (SmartCon.Revit) — ОДИН open: snapshot+geometry |
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

1. Прочитай `references/task-template.md` — там полный шаблон класса с SQL.
2. Создай `XxxActualizationTask` в `Services/Actualization/` (`internal sealed`).
3. Детект = дешёвый SQL по «пустоте колонок» (три метода: count processable, newer-only info, keys всех групп). Scope (active-only vs все версии, system vs loadable) — твой, живёт в SQL.
4. `ApplyAsync` — идемпотентная запись из `ctx.Snapshot`/`ctx.Geometry`; короткие транзакции через `LocalCatalogDatabase` (I-14). **Записанный артефакт обязан погасить твой детект** — иначе вечный pending.
5. `HandleGroupFailureAsync` — терминальный маркер (как у hash: -1/-2) или `Task.CompletedTask` (ретрай при следующем запуске). Терминальный маркер = детект его уважает (`NOT IN (2,-1,-2)`).
6. `RunFileFreePassAsync` — почти всегда `Task.FromResult(0)` (только для мгновенных UPDATE без файлов, как system re-flag у hash).
7. DI: `services.AddSingleton<IDatabaseActualizationTask, XxxActualizationTask>();`
8. Тесты: SQLite fixture + `CatalogSeedHelper` (см. `HashFormatActualizationTaskTests`): детект (processable/newer/healthy), apply пишет и **гасит детект** (re-count = 0!), failure-маркеры.
9. Доки: `docs/architecture/database-migrations.md` (таблица задач), domain interfaces/models, `validate-docs.ps1`.
10. Ручной тест: `tools/damage-catalog-db.ps1` ломает тестовую БД по критериям (см. `references/gotchas-and-testing.md`).

## Жёсткие правила (нарушение = баг, пойманный на этой сессии)

- **НЕ** открывай документы в задаче — snapshot/geometry приходят в контексте из одного open'а движка (исключение: сервисы со своим `IFamilyManagerAwaitableEvent`, как `IFamilyGeometryPipeline`).
- `CountPendingAsync` — только processable (фильтр Revit), `LoadPendingGroupKeysAsync` — ВСЕ группы (движок классифицирует). `GetNewerOnlyPendingAsync` — openability по ВСЕМ вариантам группы (apply идёт на все!), `RequiredRevitVersion = MAX over groups of MIN(variant revit)`.
- Данные/хэш — на ВСЕ Revit-варианты label (контент идентичен); ресинк `catalog_items` только когда `Group.IsActiveLabel`.
- net48: **нет `IReadOnlySet<T>`** (используй `IReadOnlyCollection<T>`), **нет default interface methods** — все члены контракта обязательные.
- `Progress<T>` в тестах — гонка (Post в xUnit context): в координирующем коде inline-адаптер `IProgress<T>`, в тестах синхронный fake (см. gotchas).
- XAML: **запрещены вложенные markup-расширения** (`{loc:Loc ...}` внутри `{Binding ...}`) — крашит загрузку плагина целиком (runtime, не compile!). Fallback'ы локализации — в VM-свойствах.
- Диалоговые VM: состояние команд выставляй ДО флага экрана (`CanClose` перед `IsSummaryVisible`) — иначе дедлок `DialogCompletion`.

## Существующие задачи — образцы для копирования

| Нужно | Копируй |
|---|---|
| Маркерная колонка + терминальные состояния + все версии + file-free pass | `HashFormatActualizationTask` |
| Replace-запись через существующие репозитории + счётчики + active-only | `AttributesActualizationTask` |
| Вызов сервиса, который сам маршалит в Revit (GLB pipeline) | `GlbPreviewActualizationTask` |

## Ссылки

- `references/task-template.md` — полный шаблон класса-задачи с SQL и тестами
- `references/gotchas-and-testing.md` — готчи сессии + ручное тестирование damage-скриптом
- `docs/architecture/database-migrations.md` — паттерн целиком (обновляй при изменениях!)
- `docs/adr/054-database-actualization-engine.md` — решения и отклонённые альтернативы
