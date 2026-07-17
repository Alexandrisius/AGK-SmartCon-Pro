# Миграции базы данных каталога (FamilyManager)

Паттерн «обновления базы» для FamilyManager: как доставлять изменения данных/схемы
`catalog.db` пользователям, у которых база уже создана старой версией SmartCon.
Введён в Issue #126 (hash-миграция v1→v2), обобщён для всех будущих миграций.

**Боль, которую решает паттерн:** раньше любое изменение формата БД означало
«создайте базу с нуля». Теперь миграции доезжают до пользователя сами, с
прогрессом, прерыванием и возобновлением.

## Когда что использовать

| Ситуация | Инструмент |
|---|---|
| Новая таблица/колонка с безопасным дефолтом | Schema migration в `DatabaseManager` при подключении (CREATE/ALTER IF NOT EXISTS) — мгновенно, без UI |
| Существующие данные надо пересчитать/починить (долго, нужен Revit API или прогресс) | **`IDatabaseMigration`** — этот паттерн |

Правило: если операция мгновенная и не нужен Revit API — schema migration.
Если дольше секунды или нужен Revit (открыть .rfa, пересчитать хэш) — `IDatabaseMigration`.

## Состав паттерна

```
IDatabaseMigration (Core)              — контракт одной миграции
DatabaseMigrationCoordinator (Core)    — агрегатор: сумма pending + запуск по Order
IDatabaseUpdateStateService (Core)     — разделяемое состояние «update required»
DatabaseUpdateStateService (FamilyManager) — реализация: gate-диалог + запуск миграций
HashRecalculationMigration (FamilyManager) — пример реализации (Issue #126)
FamilyManagerMainViewModel.HashRecalc.cs   — маппинг состояния в VM + команда
FamilyManagerPaneControl.xaml              — красная точка + баннер + «Обновить базу данных»
```

### UX (единый для всех миграций)

1. **Никаких диалогов при старте Revit.** Проверка молчаливая: после
   инициализации панели (после первого ExternalEvent round-trip — иначе
   `RevitContext` ещё не готов и версия Revit = 0) и после каждого
   переключения/подключения базы.
2. Pending > 0 → **жёлтый баннер** под строкой БД (основной индикатор:
   объяснение «режим просмотра» + кнопка «Обновить») + **красная точка**
   внутри кнопки «Инструменты базы» (beacon, указывает куда нажимать;
   tooltip меняется на «Требуется обновление базы данных»).
3. В popup инструментов — команда **«Обновить базу данных»** (видна только
   при pending > 0) — логичный дом операции для будущих миграций. Без
   собственной точки: она и так видна только при pending.
4. **База read-only пока pending > 0.** Каждая write-команда модуля гейтится
   через `IDatabaseUpdateStateService.EnsureUpToDateAsync()` — диалог с
   объяснением и кнопкой «Обновить сейчас»; «Нет» = операция отменена.
   Причина: миграция обычно чинит данные, от которых зависит корректность
   записей (напр. дедуп по хэшам) — писать в старую базу = плодить мусор.

Гейтнутые операции (по состоянию на Issue #126):

| Область | Команды |
|---|---|
| Импорт | `ImportFilesAsync`, `ImportFileToCategoryAsync`, `ImportSelectedElementsAsync`, `ImportActiveFileAsync` |
| Загрузка в проект | `LoadToProject*`, `UpdateStale*`, loadable `PlaceTypeAsync`, loadable `StartPlacementDrag` (DnD) |
| Семейства | `DeleteFamilyAsync`, `DropFamilyAsync` (DnD по категориям), `OpenCategoryEditorAsync` |
| Свойства | `SaveAsync` (Общие), `MakeActiveAsync`, `DeleteVersion`, все write-команды ассетов (avatar/files/primary/version-binding) |

НЕ гейтятся: просмотр (дерево, поиск, свойства, 3D, тултипы), открытие
семейства в Revit (`EditFamily*` — не пишет в БД), управление подключениями
БД, профиль, настройки project-base.

## Как добавить новую миграцию

1. Реализуй `IDatabaseMigration` (Core, `Services/Interfaces/IDatabaseMigration.cs`):

```csharp
public sealed class MyFeatureMigration : IDatabaseMigration
{
    public string Id => "my-feature-v1";   // стабильный id для логов
    public int Order => 20;                // после hash-v2 (10)

    public Task<int> CountPendingAsync(int revitMajorVersion, CancellationToken ct = default)
        // Дешёвый SQL COUNT по маркерной колонке. Без побочных эффектов —
        // вызывается на каждом переключении базы.

    public Task RunAsync(int revitMajorVersion, CancellationToken ct = default)
        // Сама миграция (+ свой прогресс-диалог по образцу
        // HashRecalculationProgressViewModel, паттерн ADR-048 modeless).
}
```

2. Зарегистрируй в `ServiceRegistrar` (секция «Database migrations»):

```csharp
services.AddSingleton<IDatabaseMigration, MyFeatureMigration>();
```

Всё остальное — автоматически: badge, баннер, команда, read-only gate,
агрегация pending, порядок выполнения. Координатор резолвится через
`IEnumerable<IDatabaseMigration>`; состояние держит singleton
`IDatabaseUpdateStateService` (его же инжектируй в свою VM, если твоей
фиче нужен gate на свои write-операции — вызови `EnsureUpToDateAsync()`
первой строкой команды).

## Жёсткие требования к реализации миграции

- **Маркер завершённости на запись.** Каждая обработанная запись помечается
  (колонка-маркер, напр. `hash_format_version`: NULL/старое = pending,
  текущая версия = done, `-1` = пропущено навсегда). Pending = COUNT по маркеру.
  Без маркера миграция не сможет возобновляться.
- **Chunked commits.** Коммит каждые N записей (10) в одной транзакции —
  прерывание теряет максимум пачку. I-14: DELETE journal, busy_timeout,
  **никакого WAL**, соединения только через `LocalCatalogDatabase`.
- **Возобновляемость.** `RunAsync` обязан корректно продолжить после
  прерывания/краша на любой записи.
- **Устойчивость координатора.** Один сломанный `CountPendingAsync` не
  роняет остальные (Warn + вклад 0) — но своя миграция тогда «невидима»,
  поэтому ошибки честно логируй.
- **Revit API — только через ExternalEvent** (`IFamilyManagerAwaitableEvent`),
  файлы открывать/закрывать по одному, не держать открытыми (деградация
  Revit после ~30 циклов open/close, см. ADR-050).
- **`Order`:** schema-critical миграции — меньшие значения; data repair —
  больше. hash-v2 = 10, дальше с шагом 10.

## Тесты

- Координатор: `SmartCon.Tests/Core/Services/DatabaseMigrationCoordinatorTests.cs`
  (сумма pending, порядок, устойчивость к сломанной миграции, отмена).
- Своя миграция: тесты на SQLite fixture по образцу
  `CatalogHashRecalculationServiceTests` (11 тестов).

## Связанные документы

- [ADR-050](../adr/050-hash-recalculation-migration.md) — первая миграция
  на этом паттерне (hash v1→v2), решения и отклонённые альтернативы
- [ADR-049](../adr/049-content-hash-v2-rename-invariant.md) — формат хэша,
  ради которого понадобилась та миграция
- [ADR-048](../adr/048-modeless-progress-dialogs.md) — modeless прогресс-диалоги
- `docs/invariants.md` — I-14 (SQLite), I-01 (ExternalEvent), I-03 (транзакции)
