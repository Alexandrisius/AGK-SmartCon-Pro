# ADR-054: Database Actualization Engine — единый сервис актуализации БД (задачи вместо миграций-стадий)

**Date:** 2026-07-21  
**Status:** accepted  
**Related:** ADR-050 (hash v1→v2 — механика первой задачи, **фреймворк superseded этим документом**), ADR-048 (modeless прогресс), ADR-049 (hash v2), Issue #126, #151/#152/#153 (брак данных, который чинит движок), I-01/I-14

## Context

Паттерн data-миграций (ADR-050) знал одну форму: «одна миграция = один прогон со своим
диалогом». Когда появилась вторая миграция (backfill атрибутов/3D/хэшей старых баз),
пошаговая эволюция «две команды → одна команда → один диалог со стадиями» показала на
ручных тестах, что **стадии — ложная модель**: «хэш устарел», «нет атрибутов», «нет 3D» —
это одно и то же: «у записи не хватает артефактов, извлекаемых из файла». Стадии заставляли
открывать один и тот же `.rfa` дважды (hash-стадия, затем backfill-стадия) и читались
пользователем как баг.

Нужна целевая форма: **один сервис, в который легко добавлять «запросы на обновление»**,
и один проход — открыл файл один раз → забрал всё, чего не хватает → записал → закрыл.

## Decision

### 1. `IDatabaseActualizationTask` — единица расширения

```csharp
public interface IDatabaseActualizationTask
{
    string Id { get; }                       // "hash-v2", "attributes-v1", "glb-v1"
    int Order { get; }                       // порядок apply внутри одной семьи
    bool IsCritical { get; }                 // critical → баннер + read-only гейт
    Task<int> CountPendingAsync(int revit, CancellationToken ct);          // для badge/меню
    Task<IReadOnlyCollection<string>> LoadPendingGroupKeysAsync(int revit, CancellationToken ct);
    Task<int> RunFileFreePassAsync(int revit, CancellationToken ct);       // работа без файлов (0 для большинства)
    Task ApplyAsync(FamilyActualizationContext ctx, CancellationToken ct); // записать своё
    Task HandleGroupFailureAsync(ActualizationGroup group, ActualizationFailureKind kind, CancellationToken ct);
}
```

Ключевые свойства:
- **Детект = «пустота колонок»** (SQL), маркерная колонка не нужна: записанный артефакт сам
  гасит детект → возобновляемость бесплатно.
- **Apply идемпотентен** и коммитит свои записи короткими транзакциями (I-14).
- **HandleGroupFailure** — задача сама решает семантику терминальных маркеров (hash: -2/-1;
  attributes/glb: no-op, ретрай при следующем запуске).
- **RunFileFreePass** — мгновенная работа без открытия файлов (hash: re-flag system-строк).

### 2. Движок `ICatalogActualizationService` — один проход

1. File-free passes всех задач.
2. Детекты задач → union ключей `catalogItemId|versionLabel`.
3. По каждой pending-группе: **один open** (`ExtractLoadableWithGeometryAsync` — snapshot +
   геометрия в одной сессии) → apply только задач, pending на этой группе (по Order) → close.
4. Файл не найден / не прочитался → задачи уведомляются через `HandleGroupFailureAsync`;
   missing — в сводку с purge (purge живёт в движке, не в задачах).
5. Версии новее запущенного Revit — пропуск + счётчик в сводке. Отмена между семьями,
   прогресс «Обработка X из Y — file.rfa».

### 3. Два уровня критичности — атрибут задачи, не UX-форма

```
IsCritical = true   → pending поднимает жёлтый баннер + красную точку + read-only гейт
                      всех write-операций (EnsureUpToDateAsync)
IsCritical = false  → ничего своего; лишь делает видимой команду «Обновить базу»
                      (для Owner/BimMaster — Engineer подключается read-only)
```

**UX:** ОДНА команда «Обновить базу» (видна при любом pending: critical — все роли,
optional — Owner/BimMaster) → ОДИН modeless диалог (ADR-048) с единым прогрессом и единой
сводкой + purge. Никаких стадий.

### 3a. Newer-Revit-only pending — CRITICAL гейтит, OPTIONAL — янтарь (2026-07-21, rev.2)

Гейт/баннер покрывают processable pending (фильтр `revit_major_version <= текущий`). А
pending, который нельзя починить в запущенном Revit (все варианты файла новее)? Первая
версия §3a делала его невидимым-негейтящим — отвергнуто пользователем по двум причинам:
(1) **целостность дедупа**: импорт дубликата семейства, чей stale-хэш живёт в записи
нового Revit, молча создаёт дубль — поэтому CRITICAL newer-only **гейтит точно так же**:
база read-only до идеальной миграции; (2) **видимость**: пользователь должен знать, что
обновление не завершено и где его завершить.

Итоговая модель:

| Состояние | Индикатор | Гейт |
|---|---|---|
| Critical processable | **Одна точка — красная** + баннер («режим просмотра» + кнопка «Обновить», только Owner/BimMaster) | Да, с оффером обновить сейчас |
| Critical newer-only | **Одна точка — красная** + баннер «требуется Revit {N}+ — обновится всё за раз» (кнопка скрыта) | Да, без оффера (предупреждение с версией) |
| Optional processable | **Та же точка — янтарная** + команда «Обновить базу» (Owner/BimMaster) | Нет |
| Optional newer-only | **Та же точка — янтарная** (команды нет — здесь нечего чинить) | Нет |

Точка ОДНА (правый верхний угол кнопки «Инструменты базы»): красная при любом
critical-гейте, янтарная при ЛЮБОМ optional pending (неблокирующем,
рекомендованном) — независимо от открываемости: так рекомендация всегда указывает
на действие (команду здесь или проход в новом Revit). Никакого текста в popup —
popup для команд, весь текст в баннере.

**Роли (RBAC, 2026-07-22):** обновление — write-операция, поэтому команда и кнопка
баннера видны только Owner/BimMaster (`CanEdit`). Engineer (read-only SQLite) НЕ
может запустить обновление физически (запись упадёт на read-only соединении) —
для него: баннер/гейт-диалог с текстом «обновление может выполнить Owner или
BIM-мастер» и никакого оффера (defense in depth: `DatabaseUpdateStateService`
проверяет `IDbAccessControlService.CanEdit` перед оффером).

`IDatabaseActualizationTask.GetNewerOnlyPendingAsync` → `NewerOnlyPendingInfo(Count,
RequiredRevitVersion)`: openability считается по ВСЕМ вариантам группы (apply идёт на все),
RequiredRevitVersion = MAX over groups of MIN(variant revit) — минимальный Revit, в котором
всё newer-only обновится **за один раз** (показывается в баннере и gate-диалоге).
`DatabasePendingBreakdown`: `Critical`, `Optional` (processable), `NewerOnlyCritical`
(гейтит), `NewerOnlyOptional` (янтарь), `NewerOnlyCriticalRequiredRevitVersion`.
`IDatabaseUpdateStateService.IsUpdateRequired = TotalCritical > 0` (processable + newer-only).

### 4. Существующий функционал стал задачами

| Задача | Order | Critical | Что делает |
|---|---|---|---|
| `hash-v2` | 10 | да | Хэши v2 (ADR-049/050): apply на все Revit-варианты + ресинк item; system re-flag в file-free pass; терминальные маркеры -1/-2. **Заменена `hash-v3` (ADR-056, 2026-07-23): детект v1/v2/NULL→v3, system — полный пересчёт из staged .rvt, file-free pass упразднён** |
| `attributes-v1` | 20 | нет | Типы + значения + shared nested + счётчики (active label; чинит #151/#152/#153) |
| `glb-v1` | 30 | нет | Auto-extracted 3D GLB превью (active label) |

### 5. Как добавить задачу под новую фичу

Новая колонка с дефолтом → DDL-мигратор (без UI). Данные, извлекаемые из файла →
новый класс `XxxActualizationTask` (детект SQL + apply из готового snapshot/geometry) +
строка в DI. Движок, диалог, гейт, resume, purge — бесплатно. Подробный паттерн:
`docs/architecture/database-migrations.md`.

## Отклонённые альтернативы

- **Стадии миграций** (промежуточная версия ADR-054): два открытия одного файла, читается
  как баг. Superseded этим документом.
- **Детект/apply через движок с SQL-фильтром движка** — отклонено: scope (active-only vs
  все версии) и терминальные маркеры — собственность задачи, движок их не навязывает.
- **Default interface methods** — не поддерживаются рантаймом net48; все члены обязательные,
  `RunFileFreePassAsync` у большинства задач возвращает 0.
- **Batch commit на 10 файлов** — заменён per-group коммитами внутри задач: короче локи,
  та же устойчивость к отмене (I-14).

## Consequences

- Один файл открывается ровно один раз за прогон, сколько бы артефактов ни не хватало.
- Цена новой фичи со старыми данными — один класс с пятью методами.
- Critical-поведение ADR-050 (баннер/гейт/purge) сохранено; `ICatalogHashRecalculationService`,
  `ICatalogBackfillService`, `IDatabaseMigration`, `DatabaseMigrationCoordinator` и
  per-migration диалоги удалены.
- Известное ограничение: missing-файлы держат optional pending > 0 до восстановления файла
  или purge — осознанная честность UI.
